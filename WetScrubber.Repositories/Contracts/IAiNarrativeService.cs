using System.Threading;
using System.Threading.Tasks;

namespace WetScrubber.Business.AI
{
    public interface IAiNarrativeService
    {
        /// <summary>
        /// Generates an AI draft from the deterministic report.
        /// Never saves anything.
        /// Never modifies engineering values.
        /// </summary>
        Task<string> DraftNarrativeAsync(
            string deterministicReport,
            CancellationToken cancellationToken = default);
    }
}