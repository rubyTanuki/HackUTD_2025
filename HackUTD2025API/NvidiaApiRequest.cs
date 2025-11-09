using System.Text.Json.Serialization;

// The main request object
public class NvidiaApiRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; }

    [JsonPropertyName("messages")]
    public List<ApiMessage> Messages { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.6;

    [JsonPropertyName("top_p")]
    public double TopP { get; set; } = 0.95;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 2048;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false; // Set to false

    // These fields came from the Python 'extra_body'
    [JsonPropertyName("min_thinking_tokens")]
    public int MinThinkingTokens { get; set; } = 1024;

    [JsonPropertyName("max_thinking_tokens")]
    public int MaxThinkingTokens { get; set; } = 2048;
}

// Re-used for both request and response
public class ApiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }
}

// The top-level object of the response
public class ApiResponse
{
    [JsonPropertyName("choices")]
    public List<ApiChoice> Choices { get; set; }
}

// Each item in the "choices" array
public class ApiChoice
{
    [JsonPropertyName("message")]
    public ApiMessage Message { get; set; }
}

// The final, clean data object you want
public class ArticleDetails
{
    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("authors")]
    public List<string> Authors { get; set; }

    [JsonPropertyName("abstract")]
    public string Abstract { get; set; }

    [JsonPropertyName("release_year")]
    public string ReleaseYear { get; set; }

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; }

    [JsonPropertyName("link")]
    public string Link { get; set; }

    [JsonPropertyName("relevance")]
    public double? Relevance { get; set; }
}