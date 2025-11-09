using Microsoft.AspNetCore.Mvc;
using GenerativeAI;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using AngleSharp.Html.Parser;
using System.Net.Http; // We need this now!

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

        List<float>? liveThesisVector = await _embeddingService.GetEmbeddingAsync(thesis, "query");

        string geminiApiKey = _configuration["ApiKeys:Gemini"];

        // 2. Ask Gemini for key words
        var model = new GenerativeModel(
            apiKey: geminiApiKey,
            model: "models/gemini-2.5-flash"
        );
        string prompt = "return nothing but the 2 best keywords/keyphrases for finding supporting resources on google scholar for this thesis in the format keyword1, keyword2 : " + thesis;
        var response = await model.GenerateContentAsync(prompt);

        // 3. Webscrape keywords on Google Scholar (This part still uses Selenium)
        IWebDriver driver = new ChromeDriver();
        driver.Navigate().GoToUrl("https://scholar.google.com/scholar?q=" + response.Text);
        List<IWebElement> articleResults = driver.FindElements(By.ClassName("gs_r")).Take(15).ToList();
        List<string> articleLinks = new List<string>();
        foreach (var result in articleResults)
        {
            try
            {
                // Find the link on each result
                string link = result.FindElement(By.TagName("a")).GetAttribute("href");
                if (!string.IsNullOrEmpty(link))
                {
                    articleLinks.Add(link);
                }
            }
            catch (Exception ex)
            {
                // Log if a result didn't have a link, etc.
                Console.WriteLine($"Failed to find link on one result: {ex.Message}");
            }
        }

        driver.Quit();

        List<ArticleDetails> articles = new List<ArticleDetails>();

        

        // Create ONE HttpClient for this whole request
        var client = _httpClientFactory.CreateClient();
        // Set a realistic User-Agent to avoid being blocked
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");

        foreach (string link in articleLinks)
        {
            if (link.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Skipping PDF link: {link}");
                continue; // Skip to the next article
            }

            string rawHtml;
            try
            {
                rawHtml = await client.GetStringAsync(link);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Failed to get {link}: {ex.Message}");
                continue; // Skip this article
            }

            // 4. Parse with AngleSharp
            var parser = new HtmlParser();
            var document = parser.ParseDocument(rawHtml);
            var mainContentElement =
                document.QuerySelector("article") ??
                document.QuerySelector("main") ??
                document.QuerySelector("[role='main']");

            string contentToSendToAI = mainContentElement?.TextContent ?? document.Body?.TextContent ?? "";

            // 5. Send to Nemotron (using our injected service)
            try
            {
                ArticleDetails article = await _extractor.ExtractArticleDetailsAsync(contentToSendToAI);
                if(article!=null){
                    article.Link = link;
                    if(!string.IsNullOrEmpty(article.Abstract)){
                        var abstractVector = await _embeddingService.GetEmbeddingAsync(article.Abstract, "passage");
                        if (abstractVector != null)
                        {
                            article.Relevance = CalculateRelevancePercentage(liveThesisVector, abstractVector);
                        }
                    }
                    articles.Add(article);
                    if (articles.Count >= 5) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to extract data from {link}: {ex.Message}");
                continue; // Skip on extraction failure
            }
        }

        // 6. Return the object list. ASP.NET will serialize it.
        Console.WriteLine($"Returning {articles.Count} articles");
        return Ok(articles);
    }

    private double CalculateRelevancePercentage(List<float> vectorA, List<float> vectorB)
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
            return 0.0;

        double cosine = dotProduct / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
        
        // Scale the similarity (which is -1 to 1) to a percentage (0 to 100).
        // For retrieval, similarity is usually > 0, so this scaling is mostly for presentation.
        double percentage = ((cosine + 1) / 2) * 100; 

        return Math.Round(percentage, 2);
    }
}