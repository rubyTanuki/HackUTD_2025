using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json; // May need NuGet package
using System.Text;
using System.Text.Json;

public class NvidiaDataExtractor
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    // Use JsonSerializerOptions for case-insensitivity
    private readonly JsonSerializerOptions _jsonOptions = 
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    public NvidiaDataExtractor(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _apiKey = configuration["ApiKeys:OpenRouter"]; // Read key from appsettings
        
        // Get a pre-configured HttpClient from the factory
        _httpClient = httpClientFactory.CreateClient(); 
        _httpClient.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public async Task<ArticleDetails> ExtractArticleDetailsAsync(string articleText)
    {
        // 1. Define our prompts
        string systemPrompt = "/think"; // This enables the reasoning_content feature
        string userPrompt = @"You are a data extraction bot.
Analyze the user-provided text from a scholarly article.
Extract the title, authors, abstract, release date, and publisher.
Respond ONLY with a valid JSON object matching this schema:
{
  ""title"": ""The Article Title"",
  ""authors"": [""Author One"", ""Author Two""],
  ""abstract"": ""The full abstract text..."",
  ""release_year"": ""The publication year"",
  ""publisher"": ""The journal or publisher name""
}
If a field is not found, return null for that field.

Here is the article text:
" + articleText;

        // 2. Build the request object
        var requestPayload = new NvidiaApiRequest
        {
            Model = "nvidia/nemotron-nano-9b-v2",
            Messages = new List<ApiMessage>
            {
                new ApiMessage { Role = "system", Content = systemPrompt },
                new ApiMessage { Role = "user", Content = userPrompt }
            },
            Stream = false
            // Other parameters are set by default in the class
        };

        var response = await _httpClient.PostAsJsonAsync("chat/completions", requestPayload);
        response.EnsureSuccessStatusCode();
        var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse>();
        string jsonContent = apiResponse?.Choices[0]?.Message?.Content;
        if (string.IsNullOrEmpty(jsonContent))
            return null; 
        if (jsonContent.StartsWith("```"))
        {
            // Find the first '{' and the last '}'
            int firstBrace = jsonContent.IndexOf('{');
            int lastBrace = jsonContent.LastIndexOf('}');

            if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
            {
                // Extract just the JSON part
                jsonContent = jsonContent.Substring(firstBrace, (lastBrace - firstBrace) + 1);
            }
        }

        var details = JsonSerializer.Deserialize<ArticleDetails>(jsonContent, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        return details; 
    }
}