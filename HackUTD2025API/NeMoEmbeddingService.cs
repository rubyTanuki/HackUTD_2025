using System.Text.Json;
using System.Text.Json.Serialization;

// ----------------------------------------------------
// 1. DATA TRANSFER OBJECTS (DTOs)
// ----------------------------------------------------

// Request DTOs
public class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "nvidia/llama-3_2-nemoretriever-300m-embed-v2"; 
    
    [JsonPropertyName("input")]
    public List<string> Input { get; set; }
}

// Response DTOs
public class EmbeddingData
{
    [JsonPropertyName("embedding")]
    public List<float> Embedding { get; set; }
    
    [JsonPropertyName("index")]
    public int Index { get; set; }
}

public class EmbeddingResponse
{
    [JsonPropertyName("data")]
    public List<EmbeddingData> Data { get; set; }
}

// ----------------------------------------------------
// 2. EMBEDDING API SERVICE
// ----------------------------------------------------
public class NeMoEmbeddingService
{
    private readonly HttpClient _httpClient = new HttpClient();
    private const string EmbeddingApiUrl = "https://integrate.api.nvidia.com/v1/embeddings";
    
    // NOTE: In a real app, load the API key securely from configuration
    private readonly string _apiKey;

    public NeMoEmbeddingService(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// Calls the NeMo API to convert text into a vector embedding.
    /// </summary>
    public async Task<List<float>?> GetEmbeddingAsync(string textToEmbed)
    {
        var requestBody = new EmbeddingRequest
        {
            Input = new List<string> { textToEmbed }
        };

        try
        {
            var jsonPayload = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync(EmbeddingApiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"API Error: {response.StatusCode} - {error}");
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var embeddingResponse = JsonSerializer.Deserialize<EmbeddingResponse>(jsonResponse);

            return embeddingResponse?.Data.FirstOrDefault()?.Embedding;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"General Error during API call: {ex.Message}");
            return null;
        }
    }
}