using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text; // We need this for StringContent

// --- DTOs for the VLM (Parse) API ---

public class VlmRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "nvidia/nemotron-parse";

    [JsonPropertyName("messages")]
    public List<VlmMessage> Messages { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 1024;
}

public class VlmMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<VlmContentPart> Content { get; set; }
}

public abstract class VlmContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; }
}

public class VlmTextPart : VlmContentPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; }

    public VlmTextPart(string text)
    {
        Type = "text";
        Text = text;
    }
}

public class VlmPdfPart : VlmContentPart
{
    [JsonPropertyName("source")]
    public PdfSource Source { get; set; }

    public VlmPdfPart(byte[] pdfBytes)
    {
        Type = "document";
        Source = new PdfSource
        {
            Type = "base64",
            MediaType = "application/pdf",
            Data = Convert.ToBase64String(pdfBytes)
        };
    }
}

public class PdfSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; }

    [JsonPropertyName("data")]
    public string Data { get; set; }
}

// --- THE NEW VLM SERVICE ---

public class NvidiaVlmService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = 
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    public NvidiaVlmService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _apiKey = configuration["ApiKeys:Nvidia"]; 
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri("https://integrate.api.nvidia.com/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// Calls the VLM API with a PDF to extract structured data.
    /// </summary>
    public async Task<ArticleDetails?> ExtractDetailsFromPdfAsync(byte[] pdfBytes)
    {
        // This is the prompt for the VLM
        string prompt = @"You are a data extraction bot.
Analyze the provided PDF document.
Extract the title, all authors, the abstract, the release date, and the publisher.
Respond ONLY with a valid JSON object matching this schema:
{
  ""title"": ""The Article Title"",
  ""authors"": [""Author One"", ""Author Two""],
  ""abstract"": ""The full abstract text..."",
  ""release_date"": ""The publication date"",
  ""publisher"": ""The journal or publisher name""
}
If a field is not found, return null for that field.";

        var requestBody = new VlmRequest
        {
            Messages = new List<VlmMessage>
            {
                new VlmMessage
                {
                    Content = new List<VlmContentPart>
                    {
                        new VlmTextPart(prompt),
                        new VlmPdfPart(pdfBytes) // This sends the file
                    }
                }
            }
        };

        try
        {
            // We use the manual method to fix the "charset" issue
            string jsonPayload = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            content.Headers.ContentType.CharSet = ""; 

            // Call the correct endpoint: multimodal/chat/completions
            HttpResponseMessage response = await _httpClient.PostAsync("chat/completions", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"NVIDIA VLM API Error: {response.StatusCode} - {error}");
                return null;
            }

            // De-serialize the response (re-using your extractor's classes)
            var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse>(_jsonOptions);
            string jsonContent = apiResponse?.Choices[0]?.Message?.Content;

            if (string.IsNullOrEmpty(jsonContent)) return null; 
            
            // Trim the markdown backticks
            if (jsonContent.StartsWith("```"))
            {
                int firstBrace = jsonContent.IndexOf('{');
                int lastBrace = jsonContent.LastIndexOf('}');
                if (firstBrace != -1 && lastBrace != -1)
                {
                    jsonContent = jsonContent.Substring(firstBrace, (lastBrace - firstBrace) + 1);
                }
            }

            var details = JsonSerializer.Deserialize<ArticleDetails>(jsonContent, _jsonOptions);
            return details;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"General Error during VLM API call: {ex.Message}");
            return null;
        }
    }
}