using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace WetScrubber.Business.AI
{
    /// <summary>
    /// Talks to Groq's cloud-hosted, OpenAI-compatible chat completions API
    /// to draft report narrative text. Replaces OllamaChatProvider: same
    /// job (text in, text out), same IAiChatProvider contract, just a
    /// hosted, GPU-served model instead of a slow CPU-bound local one.
    ///
    /// IMPORTANT
    /// ---------
    /// This class only sends prompts and returns text. It never touches
    /// engineering values, never parses numbers out of the response, and
    /// never writes to the database. Saving is the caller's job
    /// (see ReportController), and only after human review.
    ///
    /// Register with:
    ///   builder.Services.AddHttpClient&lt;IAiChatProvider, GroqChatProvider&gt;();
    /// </summary>
    public sealed class GroqChatProvider : IAiChatProvider
    {
        private readonly HttpClient _httpClient;
        private readonly GroqOptions _options;

        public GroqChatProvider(HttpClient httpClient, IOptions<GroqOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;

            if (_httpClient.BaseAddress == null)
            {
                _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
            }

            // Groq is hosted and normally sub-second, but give it real
            // headroom instead of the HttpClient default (100s) in case of
            // cold starts or a slow network. Configurable via
            // Groq:TimeoutSeconds in appsettings.json.
            _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        }

        public async Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
                throw new ArgumentException("User prompt cannot be empty.", nameof(userPrompt));

            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                // Fail fast with a clear message instead of letting Groq
                // return a generic 401. The caller (AiNarrativeService ->
                // ReportController) falls back to the template-only report
                // either way, but this tells the operator exactly what to
                // set.
                throw new InvalidOperationException(
                    "Groq:ApiKey is not configured. Set it in appsettings.json, an " +
                    "environment variable (Groq__ApiKey), or user-secrets before " +
                    "drafting with AI. The template-only report is still available " +
                    "without AI drafting.");
            }

            var request = new GroqChatRequest
            {
                Model = _options.Model,
                Temperature = _options.Temperature,
                MaxTokens = _options.MaxTokens,
                Stream = false,
                Messages = new[]
                {
                    new GroqMessage { Role = "system", Content = systemPrompt ?? string.Empty },
                    new GroqMessage { Role = "user",   Content = userPrompt }
                }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            httpRequest.Content = JsonContent.Create(request);

            HttpResponseMessage response;

            try
            {
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                // Groq unreachable / network down. Surface a clear error so
                // the caller can fall back to the template-only narrative
                // instead of crashing the report screen.
                throw new InvalidOperationException(
                    $"Could not reach Groq at {_options.BaseUrl}. Check network access " +
                    "and firewall rules for api.groq.com. The template-only report is " +
                    "still available without AI drafting.",
                    ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Groq request timed out after {_options.TimeoutSeconds}s.", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Groq returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            var payload = await response.Content.ReadFromJsonAsync<GroqChatResponse>(
                cancellationToken: cancellationToken);

            var content = payload?.Choices is { Length: > 0 }
                ? payload.Choices[0].Message?.Content
                : null;

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("Groq returned an empty response.");
            }

            return content;
        }

        // ── Wire types for Groq's OpenAI-compatible /chat/completions ──
        // Kept private/nested: these are transport details, not part of
        // this project's domain model, so they shouldn't leak out.

        private sealed class GroqChatRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "";

            [JsonPropertyName("messages")]
            public GroqMessage[] Messages { get; set; } = Array.Empty<GroqMessage>();

            [JsonPropertyName("temperature")]
            public double Temperature { get; set; }

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

            [JsonPropertyName("stream")]
            public bool Stream { get; set; }
        }

        private sealed class GroqMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = "";

            [JsonPropertyName("content")]
            public string Content { get; set; } = "";
        }

        private sealed class GroqChatResponse
        {
            [JsonPropertyName("choices")]
            public GroqChoice[]? Choices { get; set; }
        }

        private sealed class GroqChoice
        {
            [JsonPropertyName("message")]
            public GroqMessage? Message { get; set; }
        }
    }
}