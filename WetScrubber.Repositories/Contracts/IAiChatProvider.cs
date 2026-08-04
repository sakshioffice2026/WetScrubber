using System.Threading;
using System.Threading.Tasks;

namespace WetScrubber.Business.AI
{
    public interface IAiChatProvider
    {
        Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default);
    }
}