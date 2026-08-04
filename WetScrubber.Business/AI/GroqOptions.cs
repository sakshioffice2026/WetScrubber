namespace WetScrubber.Business.AI
{
    public class GroqOptions
    {
        public const string SectionName = "Groq";

        // Get a free key at https://console.groq.com/keys — do not commit a
        // real key to source control. Prefer user-secrets or an environment
        // variable (Groq__ApiKey) over hardcoding it in appsettings.json.
        public string ApiKey { get; set; } = "";

        public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";

        public string Model { get; set; } = "llama-3.3-70b-versatile";

        public double Temperature { get; set; } = 0.2;

        public int MaxTokens { get; set; } = 900;

        public int TimeoutSeconds { get; set; } = 30;
    }
}