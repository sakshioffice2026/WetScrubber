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
            var ask = await AskForMissingAsync(missing, cancellationToken).ConfigureAwait(false);
            return new AgentReply(ask, false, missing, null, null);
        }

        var computation = await _domain.ComputeAsync(state, cancellationToken).ConfigureAwait(false);
        var review = await ReviewAsync(state, computation, cancellationToken).ConfigureAwait(false);

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

    private async Task<string> AskForMissingAsync(
        IReadOnlyList<string> missing,
        CancellationToken cancellationToken)
    {
        var history = new ChatHistory(EngineeringPrompts.MissingFieldsPrompt(missing));
        history.AddUserMessage("Ask me for the missing inputs.");

        var response = await _chat.GetChatMessageContentAsync(history, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(response.Content)
            ? $"Please provide: {string.Join(", ", missing)}."
            : response.Content.Trim();
    }

    private async Task<string> ReviewAsync(
        TState state,
        AgentComputation computation,
        CancellationToken cancellationToken)
    {
        var draftJson = JsonSerializer.Serialize(state, JsonOptions);

        var history = new ChatHistory(EngineeringPrompts.ReviewSystemPrompt(_domain.DomainDescription));
        history.AddUserMessage(
            EngineeringPrompts.ReviewUserPrompt(draftJson, computation.CalculationJson, computation.ChecksJson));

        var response = await _chat.GetChatMessageContentAsync(history, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Content?.Trim() ?? string.Empty;
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