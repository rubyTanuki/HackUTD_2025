using Microsoft.AspNetCore.Mvc;
using GenerativeAI;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using AngleSharp.Html.Parser;
using System.Net.Http; // We need this now!
using System.Threading;

using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.IO;
using System.Net.Http.Headers;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using System.Text;

[ApiController]
public class ThesisAPIController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly NvidiaDataExtractor _extractor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NeMoEmbeddingService _embeddingService;
    private readonly NvidiaVlmService _vlmService;

    // Services are "injected" into the constructor
    public ThesisAPIController(
        IConfiguration configuration, 
        NvidiaDataExtractor extractor,
        IHttpClientFactory httpClientFactory,
        NeMoEmbeddingService embeddingService,
        NvidiaVlmService vlmService)
    {
        _configuration = configuration;
        _extractor = extractor;
        _httpClientFactory = httpClientFactory;
        _embeddingService = embeddingService;
        _vlmService = vlmService;
    }

    List<string> domainBlocklist = new List<string>
    {
        "books.google.com",
        "scholar.google.com",
        "igi-global.com",
        "link.springer.com",
        "ieeexplore.ieee.org",
        "researchgate.net",
        "tandfonline.com", // Taylor & Francis
        "sciencedirect.com",
        "pure.ulster.ac.uk"
    };

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
        var semaphore = new SemaphoreSlim(10); // Max 5 concurrent requests
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
        
        string prompt = "return nothing but the 3 best keywords/keyphrases for finding supporting resources on google scholar for this thesis in the format keyword1, keyword2 : " + thesis;
        var response = await model.GenerateContentAsync(prompt);
        return response.Text;
    }

    private List<string> ScrapeGoogleScholar(string keywords)
    {
        var chromeOptions = new ChromeOptions();
        chromeOptions.AddArgument("--headless");
        chromeOptions.AddArgument("--disable-gpu");
        chromeOptions.AddArgument("--disable-extensions");
        IWebDriver driver = new ChromeDriver(chromeOptions);
        
        try
        {
            List<string> articleLinks = new List<string>();
            int targetCount = 30;
            int currentPage = 0;

            while (articleLinks.Count < targetCount && currentPage < 2)
            {
                // Calculate the start parameter for pagination (0 for page 1, 10 for page 2)
                int startParam = currentPage * 10;
                string url = "";
                if(startParam == 0)
                    url = $"https://scholar.google.com/scholar?q={keywords}";
                else
                    url = $"https://scholar.google.com/scholar?q={keywords}&start={startParam}";
                
                Console.WriteLine($"Scraping page {currentPage + 1}: {url}");
                driver.Navigate().GoToUrl(url);
                
                // Add a small delay to let the page load
                Thread.Sleep(2000);
                
                List<IWebElement> articleResults = driver.FindElements(By.ClassName("gs_r")).ToList();
                
                if (articleResults.Count == 0)
                {
                    Console.WriteLine("No more results found");
                    break;
                }
                
                foreach (var result in articleResults)
                {
                    if (articleLinks.Count >= targetCount)
                        break;
                    
                    try
                    {
                        string link = result.FindElement(By.TagName("a")).GetAttribute("href");
                        if (string.IsNullOrEmpty(link))
                        {
                            continue;
                        }

                        bool isBlocked = domainBlocklist.Any(domain => link.Contains(domain));

                        if (!isBlocked)
                        {
                            articleLinks.Add(link);
                            Console.WriteLine($"Added link {articleLinks.Count}: {link}");
                        }
                        else
                        {
                            Console.WriteLine($"Skipping blocked domain: {link}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to find link on one result: {ex.Message}");
                    }
                }
                
                currentPage++;
            }
            
            Console.WriteLine($"Total links collected: {articleLinks.Count}");
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

        await semaphore.WaitAsync();
        ArticleDetails? article = null;
        string contentToSendToAI = null;

        //IF PDF, THEN PARSE TEXT FROM FIRST 2 PAGES
        if (link.Contains("pdf", StringComparison.OrdinalIgnoreCase)
        || link.Contains("download", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Processing PDF: {link}");
            byte[] pdfBytes;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, link);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));

                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                // If the server doesn't send a PDF, fail fast.
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/pdf")
                {
                    // Console.WriteLine($"Failed to download valid PDF from {link}: Server sent {response.StatusCode}.");
                    return null;
                }
                
                pdfBytes = await response.Content.ReadAsByteArrayAsync();
            }
            catch (HttpRequestException ex) { /* ... */ return null; }

            // Get the first 3 pages
            byte[] trimmedPdfBytes = GetFirstPdfPages(pdfBytes, 3);

            // Convert PDF to text
            contentToSendToAI = ExtractTextFromPdf(trimmedPdfBytes);
        }else{
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
                    // Console.WriteLine($"Failed to get {link}: {ex.Message}");
                    return null;
                }

                // Parse with AngleSharp
                var parser = new HtmlParser();
                var document = parser.ParseDocument(rawHtml);
                var mainContentElement =
                    document.QuerySelector("article") ??
                    document.QuerySelector("main") ??
                    document.QuerySelector("[role='main']");

                contentToSendToAI = mainContentElement?.TextContent ?? 
                    document.Body?.TextContent ?? "";
            }
            catch (Exception ex){
                // Console.WriteLine($"Failed to process {link}: {ex.Message}");
                return null;
            }
        }
        try{
            if (IsGarbageContent(contentToSendToAI))
            {
                Console.WriteLine($"Skipping garbage content: {link}");
                return null;
            }

            if (contentToSendToAI.Length > 10000) 
            {
                contentToSendToAI = contentToSendToAI.Substring(0, 10000);
            }

            // Extract article details
            article = await _extractor.ExtractArticleDetailsAsync(contentToSendToAI);

            
            
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
        }catch (Exception ex)
        {
            
            Console.WriteLine($"Failed to extract details for {link}: {ex.Message}");
            Console.WriteLine(contentToSendToAI);
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

    private bool IsGarbageContent(string textContent)
    {
        // 1. Check for null or empty.
        if (string.IsNullOrWhiteSpace(textContent) || textContent.Length < 200)
        {
            return true; // This is definitely garbage.
        }


        // 2. Check for common block/error page keywords.
        //    (Use .ToLowerInvariant() for a case-insensitive check)
        var lowerCaseText = textContent.ToLowerInvariant();

        if (lowerCaseText.Contains("please enable javascript") ||
            lowerCaseText.Contains("you must be logged in") ||
            lowerCaseText.Contains("to continue reading") ||
            lowerCaseText.Contains("manage your cookies") ||
            lowerCaseText.Contains("use of cookies") ||
            lowerCaseText.Contains("403 forbidden") ||
            lowerCaseText.Contains("access denied"))
        {
            return true; // This is a login wall, cookie popup, or error page
        }

        // 3. If it passes, let the AI try to process it.
        return false;
    }

    private byte[] GetFirstPdfPages(byte[] originalPdfBytes, int pageCount)
    {
        try
        {
            // 1. Load the original PDF from the byte array
            using (var originalStream = new MemoryStream(originalPdfBytes))
            {
                PdfSharpCore.Pdf.PdfDocument originalDoc = PdfReader.Open(originalStream, PdfDocumentOpenMode.Import);

                // 2. Create a new, blank PDF
                PdfSharpCore.Pdf.PdfDocument newDoc = new PdfSharpCore.Pdf.PdfDocument();

                // 3. Copy the first 'pageCount' pages
                for (int i = 0; i < pageCount && i < originalDoc.PageCount; i++)
                {
                    newDoc.AddPage(originalDoc.Pages[i]);
                }

                // 4. Save the new PDF to a memory stream
                using (var newStream = new MemoryStream())
                {
                    newDoc.Save(newStream);
                    return newStream.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to trim PDF: {ex.Message}. Returning original.");
            // Fallback: If trimming fails, just return the original PDF
            // (it might fail, but it's better than crashing)
            return originalPdfBytes;
        }
    }
    private string ExtractTextFromPdf(byte[] pdfBytes)
    {
        try
        {
            var sb = new StringBuilder();
            // Open the PDF from the byte array
            using (UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes))
            {
                // Loop through each page
                foreach (var page in document.GetPages())
                {
                    sb.Append(page.Text);
                    sb.Append(" \n\n"); // Add a space between pages
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to extract text from PDF: {ex.Message}");
            return null; // Return null on failure
        }
    }
}