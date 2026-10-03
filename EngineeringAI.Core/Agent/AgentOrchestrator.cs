using System.Text.Json;
using EngineeringAI.Core.Abstractions;
using EngineeringAI.Core.Security;
using EngineeringAI.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace EngineeringAI.Core.Agent;

public sealed record AgentComputation(string CalculationJson, string ChecksJson);

public sealed record AgentReply(
    string Message,
    bool DesignComplete,
    IReadOnlyList<string> MissingFields,
    string? CalculationJson,
    string? ChecksJson);

public sealed class AgentDomainDefinition<TState> where TState : class, IDraftState, new()
{
    public required string DomainDescription { get; init; }

    public required string FieldSchemaJson { get; init; }

    public required Action<TState, JsonElement> ApplyExtracted { get; init; }

    public required Func<TState, CancellationToken, Task<AgentComputation>> ComputeAsync { get; init; }
}

public sealed class AgentOrchestrator<TState> where TState : class, IDraftState, new()
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IChatCompletionService _chat;
    private readonly DesignFlowStore<TState> _store;
    private readonly AgentDomainDefinition<TState> _domain;
    private readonly ILogger<AgentOrchestrator<TState>> _logger;

    public AgentOrchestrator(
        IChatCompletionService chat,
        DesignFlowStore<TState> store,
        AgentDomainDefinition<TState> domain,
        ILogger<AgentOrchestrator<TState>> logger)
    {
        _chat = chat;
        _store = store;
        _domain = domain;
        _logger = logger;
    }

    public async Task<AgentReply> HandleAsync(
        string sessionId,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        if (PromptSanitizer.LooksLikeInjection(userMessage))
        {
            _logger.LogWarning("Possible prompt injection detected for session {SessionId}", sessionId);
        }

        var cleanMessage = PromptSanitizer.Sanitize(userMessage);

        if (string.IsNullOrWhiteSpace(cleanMessage))
        {
            var current = _store.GetOrCreate(sessionId);
            var pending = current.GetMissingMandatoryFields();
            return new AgentReply("Please describe your design requirements.", false, pending, null, null);
        }

        await ExtractAndUpdateAsync(sessionId, cleanMessage, cancellationToken).ConfigureAwait(false);

        var state = _store.GetOrCreate(sessionId);
        var missing = state.GetMissingMandatoryFields();

        if (missing.Count > 0)
        {
            return new AgentReply(BuildMissingMessage(missing), false, missing, null, null);
        }

        var computation = await _domain.ComputeAsync(state, cancellationToken).ConfigureAwait(false);
        var review = BuildReview(computation);

        return new AgentReply(
            review,
            true,
            Array.Empty<string>(),
            computation.CalculationJson,
            computation.ChecksJson);
    }

    private async Task ExtractAndUpdateAsync(
        string sessionId,
        string cleanMessage,
        CancellationToken cancellationToken)
    {
        var history = new ChatHistory(
            EngineeringPrompts.ExtractionSystemPrompt(_domain.DomainDescription, _domain.FieldSchemaJson));
        history.AddUserMessage(cleanMessage);

        var response = await _chat.GetChatMessageContentAsync(history, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var json = ExtractJsonObject(response.Content);
        if (json is null)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var root = document.RootElement.Clone();
            _store.Update(sessionId, state => _domain.ApplyExtracted(state, root));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Extraction output was not valid JSON for session {SessionId}", sessionId);
        }
    }

    private static string BuildMissingMessage(IReadOnlyList<string> missing)
    {
        return $"Please provide: {string.Join(", ", missing)}.";
    }

    private static string BuildReview(AgentComputation computation)
    {
        try
        {
            using var calc = JsonDocument.Parse(computation.CalculationJson);
            if (calc.RootElement.ValueKind == JsonValueKind.Object &&
                calc.RootElement.TryGetProperty("error", out var error))
            {
                return $"The calculation could not be completed: {error.GetString()}";
            }
        }
        catch (JsonException)
        {
            // Fall through to the checks summary.
        }

        var issues = new List<string>();
        var passed = 0;
        var hasFail = false;
        var hasWarn = false;

        try
        {
            using var checks = JsonDocument.Parse(computation.ChecksJson);
            if (checks.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.RootElement.EnumerateArray())
                {
                    var status = check.TryGetProperty("status", out var st) ? st.GetString() ?? string.Empty : string.Empty;
                    var name = check.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                    var detail = check.TryGetProperty("detail", out var dt) ? dt.GetString() ?? string.Empty : string.Empty;
                    var parameter = check.TryGetProperty("parameter", out var pr) ? pr.GetString() ?? string.Empty : string.Empty;

                    if (string.Equals(status, "PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        passed++;
                        continue;
                    }

                    if (string.Equals(status, "FAIL", StringComparison.OrdinalIgnoreCase))
                    {
                        hasFail = true;
                    }
                    else
                    {
                        hasWarn = true;
                    }

                    var line = string.IsNullOrWhiteSpace(parameter)
                        ? $"- {name}: {detail}"
                        : $"- {name} ({parameter}): {detail}";
                    issues.Add(line);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed checks; the verdict below stays conservative.
            hasWarn = true;
        }

        var verdict = hasFail ? "NOT ACCEPTABLE"
            : hasWarn ? "ACCEPTABLE WITH CAUTION"
            : "ACCEPTABLE";

        var sb = new System.Text.StringBuilder();
        sb.Append("Design calculated. ").Append(passed).Append(" check(s) passed.");

        if (issues.Count > 0)
        {
            sb.Append("\nIssues:\n").Append(string.Join("\n", issues));
        }

        sb.Append("\nVerdict: ").Append(verdict);
        return sb.ToString();
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}