using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json; // Make sure you have this NuGet package
using Microsoft.Extensions.Configuration; // For IConfiguration

// --- DTO Classes (No change needed) ---

public class EmbeddingRequest
{
    [JsonPropertyName("model")]
    // This model ID is correct for the NVIDIA endpoint
    public string Model { get; set; } = "nvidia/llama-3.2-nemoretriever-300m-embed-v2"; 
    
    [JsonPropertyName("input")]
    public List<string> Input { get; set; }

    [JsonPropertyName("input_type")]
    public string InputType { get; set; }
}

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


// --- EMBEDDING API SERVICE (FIXED FOR NVIDIA) ---

public class NeMoEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = 
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    public NeMoEmbeddingService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        // --- FIX 1: Read your NVIDIA key ---
        _apiKey = configuration["ApiKeys:Nvidia"]; 
        
        _httpClient = httpClientFactory.CreateClient();
        
        // --- FIX 2: Point to the NVIDIA BaseAddress ---
        _httpClient.BaseAddress = new Uri("https://integrate.api.nvidia.com/v1/");
        
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _apiKey);
            
        // --- FIX 3: Remove the Referer header ---
        // (NVIDIA's API doesn't need it)
    }

    public async Task<List<float>?> GetEmbeddingAsync(string textToEmbed, string inputType)
    {
        var requestBody = new EmbeddingRequest
        {
            // This model is correct
            Model = "nvidia/llama-3.2-nemoretriever-300m-embed-v1",
            Input = new List<string> { textToEmbed },
            InputType = inputType
        };

        try
        {
            // This will now POST to https://integrate.api.nvidia.com/v1/embeddings
            string jsonPayload = JsonSerializer.Serialize(requestBody, _jsonOptions);
            
            // 2. Create StringContent
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            // 3. THIS IS THE FIX: Remove the "charset=utf-8" part
            content.Headers.ContentType.CharSet = ""; 

            // 4. Use the standard PostAsync with our modified content
            HttpResponseMessage response = await _httpClient.PostAsync("embeddings", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"NVIDIA API Error: {response.StatusCode} - {error}");
                return null;
            }

            var embeddingResponse = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(_jsonOptions);
            return embeddingResponse?.Data.FirstOrDefault()?.Embedding;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"General Error during API call: {ex.Message}");
            return null;
        }
    }
}