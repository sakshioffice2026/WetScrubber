using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace WetScrubber.Business.AI
{
    public enum RetrainTarget
    {
        All,
        Chemistry,
        Design
    }

    /// <summary>
    /// Tells the self-learning prediction service (chemistrypredictor.py)
    /// to retrain right now instead of waiting for its next background
    /// poll. Call this right after a human reviewer promotes data the
    /// model should learn from:
    ///   - a new/edited ChemicalReaction (chemistry model)
    ///   - a new DesignOutcome (design-calibration model)
    ///
    /// This is a nice-to-have, not a requirement — the Python service also
    /// polls the DB on its own timer (RETRAIN_POLL_MINUTES), so a missed
    /// or failed call here just means the model catches up a few minutes
    /// later instead of immediately. Never blocks or fails the caller's
    /// request because of that.
    /// </summary>
    public interface IModelRetrainTrigger
    {
        Task TriggerAsync(RetrainTarget target = RetrainTarget.All, CancellationToken cancellationToken = default);
    }

    public sealed class ModelRetrainClient : IModelRetrainTrigger
    {
        private readonly HttpClient _httpClient;

        public ModelRetrainClient(HttpClient httpClient, IOptions<ChemistryPredictionOptions> options)
        {
            _httpClient = httpClient;
            var opts = options.Value;

            if (_httpClient.BaseAddress == null)
                _httpClient.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");

            // Retraining can legitimately take longer than a normal predict
            // call, but this is still fire-and-forget from the caller's
            // perspective — give it a generous ceiling rather than letting
            // it hang indefinitely.
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        public async Task TriggerAsync(RetrainTarget target = RetrainTarget.All, CancellationToken cancellationToken = default)
        {
            var query = target switch
            {
                RetrainTarget.Chemistry => "chemistry",
                RetrainTarget.Design => "design",
                _ => "all"
            };

            try
            {
                await _httpClient.PostAsync($"retrain?target={query}", content: null, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Predictor unreachable — the background poller will pick
                // this up on its own schedule, so don't block or fail the
                // caller's save.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out — same reasoning as above.
            }
        }
    }
}