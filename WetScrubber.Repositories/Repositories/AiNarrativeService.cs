using System;
using System.Threading;
using System.Threading.Tasks;

namespace WetScrubber.Business.AI
{
    public sealed class AiNarrativeService : IAiNarrativeService
    {
        private readonly IAiPromptBuilder _promptBuilder;
        private readonly IAiChatProvider _chatProvider;

        public AiNarrativeService(
            IAiPromptBuilder promptBuilder,
            IAiChatProvider chatProvider)
        {
            _promptBuilder = promptBuilder;
            _chatProvider = chatProvider;
        }

        public async Task<string> DraftNarrativeAsync(
            string deterministicReport,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deterministicReport))
                throw new ArgumentException(
                    "Deterministic report cannot be empty.",
                    nameof(deterministicReport));

            var systemPrompt =
                _promptBuilder.BuildSystemPrompt();

            var userPrompt =
                _promptBuilder.BuildUserPrompt(
                    deterministicReport);

            var response =
                await _chatProvider.CompleteAsync(
                    systemPrompt,
                    userPrompt,
                    cancellationToken);

            return response.Trim();
        }
    }
}