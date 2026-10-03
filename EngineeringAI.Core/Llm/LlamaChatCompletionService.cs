using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace EngineeringAI.Core.Llm;

public sealed class LlamaChatCompletionService : IChatCompletionService
{
    private readonly LlamaModelProvider _provider;

    public LlamaChatCompletionService(LlamaModelProvider provider)
    {
        _provider = provider;
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?>
        {
            ["ModelType"] = "LLamaSharp-GGUF"
        };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildChatMlPrompt(chatHistory);
        var text = await _provider.GenerateAsync(prompt, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new List<ChatMessageContent>
        {
            new(AuthorRole.Assistant, text)
        };
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = BuildChatMlPrompt(chatHistory);

        await foreach (var token in _provider.StreamAsync(prompt, cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, token);
        }
    }

    private static string BuildChatMlPrompt(ChatHistory chatHistory)
    {
        var sb = new StringBuilder();

        foreach (var message in chatHistory)
        {
            var role = message.Role == AuthorRole.System ? "system"
                : message.Role == AuthorRole.Assistant ? "assistant"
                : "user";

            sb.Append("<|im_start|>").Append(role).Append('\n');
            sb.Append(message.Content).Append("<|im_end|>\n");
        }

        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }
}