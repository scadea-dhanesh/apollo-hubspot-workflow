namespace ApolloHubSpot.Api.Options;

public sealed class GroqOptions
{
    /// <summary>OpenAI-compatible chat completions (e.g. User Secrets or <c>GROQ_API_KEY</c>).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>See https://console.groq.com/docs/models</summary>
    public string Model { get; set; } = "llama-3.3-70b-versatile";

    public string ChatCompletionsPath { get; set; } = "/openai/v1/chat/completions";

    public double Temperature { get; set; } = 0.15;

    public int MaxTokens { get; set; } = 2048;
}
