using Microsoft.AspNetCore.Mvc;
using GenerativeAI;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using AngleSharp.Html.Parser;
using System.Net.Http; // We need this now!
using System.Threading;

[ApiController]
public class ThesisAPIController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly NvidiaDataExtractor _extractor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NeMoEmbeddingService _embeddingService;

    // Services are "injected" into the constructor
    public ThesisAPIController(
        IConfiguration configuration, 
        NvidiaDataExtractor extractor,
        IHttpClientFactory httpClientFactory,
        NeMoEmbeddingService embeddingService)
    {
        _configuration = configuration;
        _extractor = extractor;
        _httpClientFactory = httpClientFactory;
        _embeddingService = embeddingService;
    }

    [HttpPost("getArticles")]
    public async Task<IActionResult> GetArticles([FromBody] Dictionary<string, object> json)
    {
        // 1. Get thesis
        string thesis = json["thesis"].ToString();

        // Start thesis embedding and keyword generation concurrently
        var thesisEmbeddingTask = _embeddingService.GetEmbeddingAsync(thesis, "query");
        var keywordsTask = GetKeywordsAsync(thesis);

        await Task.WhenAll(thesisEmbeddingTask, keywordsTask);

        List<float>? liveThesisVector = thesisEmbeddingTask.Result;
        string keywords = keywordsTask.Result;

        // 2. Webscrape keywords on Google Scholar (still synchronous due to Selenium)
        List<string> articleLinks = await Task.Run(() => ScrapeGoogleScholar(keywords));

        // 3. Process articles concurrently
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");

        // Use SemaphoreSlim to limit concurrent requests (avoid overwhelming servers)
        var semaphore = new SemaphoreSlim(5); // Max 5 concurrent requests
        var articleTasks = articleLinks.Select(link => ProcessArticleAsync(
            link, client, liveThesisVector, semaphore));

        var processedArticles = await Task.WhenAll(articleTasks);

        // Filter out nulls and take top 5 by relevance
        var articles = processedArticles
            .Where(a => a != null)
            .OrderByDescending(a => a.Relevance)
            .Take(5)
            .ToList();

        Console.WriteLine($"Returning {articles.Count} articles");
        return Ok(articles);
    }

    private async Task<string> GetKeywordsAsync(string thesis)
    {
        string geminiApiKey = _configuration["ApiKeys:Gemini"];
        var model = new GenerativeModel(
            apiKey: geminiApiKey,
            model: "models/gemini-2.5-flash"
        );
        
        string prompt = "return nothing but the 2 best keywords/keyphrases for finding supporting resources on google scholar for this thesis in the format keyword1, keyword2 : " + thesis;
        var response = await model.GenerateContentAsync(prompt);
        return response.Text;
    }

    private List<string> ScrapeGoogleScholar(string keywords)
    {
        IWebDriver driver = new ChromeDriver();
        try
        {
            driver.Navigate().GoToUrl("https://scholar.google.com/scholar?q=" + keywords);
            List<IWebElement> articleResults = driver.FindElements(By.ClassName("gs_r")).Take(15).ToList();
            List<string> articleLinks = new List<string>();
            
            foreach (var result in articleResults)
            {
                try
                {
                    string link = result.FindElement(By.TagName("a")).GetAttribute("href");
                    if (!string.IsNullOrEmpty(link))
                    {
                        articleLinks.Add(link);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to find link on one result: {ex.Message}");
                }
            }
            
            return articleLinks;
        }
        finally
        {
            driver.Quit();
        }
    }

    private async Task<ArticleDetails?> ProcessArticleAsync(
        string link, 
        HttpClient client, 
        List<float>? liveThesisVector,
        SemaphoreSlim semaphore)
    {
        if (link.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Skipping PDF link: {link}");
            return null;
        }

        await semaphore.WaitAsync();
        try
        {
            // Fetch HTML
            string rawHtml;
            try
            {
                rawHtml = await client.GetStringAsync(link);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Failed to get {link}: {ex.Message}");
                return null;
            }

            // Parse with AngleSharp
            var parser = new HtmlParser();
            var document = parser.ParseDocument(rawHtml);
            var mainContentElement =
                document.QuerySelector("article") ??
                document.QuerySelector("main") ??
                document.QuerySelector("[role='main']");

            string contentToSendToAI = mainContentElement?.TextContent ?? 
                document.Body?.TextContent ?? "";

            // Extract article details
            ArticleDetails article = await _extractor.ExtractArticleDetailsAsync(contentToSendToAI);
            
            if (article == null || string.IsNullOrEmpty(article.Abstract))
            {
                return null;
            }

            article.Link = link;

            // Calculate relevance
            var abstractVector = await _embeddingService.GetEmbeddingAsync(article.Abstract, "passage");
            if (abstractVector != null && liveThesisVector != null)
            {
                article.Relevance = CalculateRelevancePercentage(liveThesisVector, abstractVector);
            }

            return article;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to process {link}: {ex.Message}");
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private int CalculateRelevancePercentage(List<float> vectorA, List<float> vectorB)
    {
        if (vectorA.Count != vectorB.Count)
            throw new ArgumentException("Vectors must have the same dimension.");

        double dotProduct = 0.0;
        double magnitudeA = 0.0;
        double magnitudeB = 0.0;

        for (int i = 0; i < vectorA.Count; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
            magnitudeA += Math.Pow(vectorA[i], 2);
            magnitudeB += Math.Pow(vectorB[i], 2);
        }

        // Handle zero vectors
        if (magnitudeA == 0.0 || magnitudeB == 0.0)
            return 0;

        double cosine = dotProduct / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
        
        // Scale the similarity (which is -1 to 1) to a percentage (0 to 100).
        // For retrieval, similarity is usually > 0, so this scaling is mostly for presentation.
        double percentage = ((cosine + 1) / 2) * 100; 

        return (int)Math.Round(percentage);
    }
}