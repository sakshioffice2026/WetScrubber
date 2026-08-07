using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace WetScrubber.Business.AI
{
    /// <summary>
    /// Request shape for the design-calibration model. Feed it both the
    /// design inputs and what the deterministic calculation engine already
    /// predicted (ScrubberGeometry.RemovalEfficiency / PressureDrop) — the
    /// model learns the correction from that baseline toward what field
    /// measurements actually showed, not efficiency/pressure-drop from
    /// scratch.
    /// </summary>
    public sealed class DesignOutcomePredictionRequest
    {
        [JsonPropertyName("scrubber_type")]
        public string ScrubberType { get; set; } = "";

        [JsonPropertyName("design_gas_flow_rate")]
        public double DesignGasFlowRate { get; set; }

        [JsonPropertyName("inlet_temperature")]
        public double InletTemperature { get; set; }

        [JsonPropertyName("moisture_content")]
        public double MoistureContent { get; set; }

        [JsonPropertyName("liquid_ph")]
        public double LiquidPh { get; set; }

        [JsonPropertyName("liquid_temperature")]
        public double LiquidTemperature { get; set; }

        [JsonPropertyName("design_lg_ratio")]
        public double DesignLgRatio { get; set; }

        [JsonPropertyName("tower_diameter")]
        public double TowerDiameter { get; set; }

        [JsonPropertyName("tower_height")]
        public double TowerHeight { get; set; }

        [JsonPropertyName("packing_height")]
        public double PackingHeight { get; set; }

        [JsonPropertyName("design_predicted_efficiency")]
        public double DesignPredictedEfficiency { get; set; }

        [JsonPropertyName("design_predicted_pressure_drop")]
        public double DesignPredictedPressureDrop { get; set; }
    }

    public sealed class DesignOutcomePrediction
    {
        [JsonPropertyName("predicted_removal_efficiency")]
        public double? PredictedRemovalEfficiency { get; set; }

        [JsonPropertyName("predicted_pressure_drop")]
        public double? PredictedPressureDrop { get; set; }

        [JsonPropertyName("confidence_band")]
        public string ConfidenceBand { get; set; } = "LowSimilarity";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "none";   // "learned_model" | "none"

        [JsonPropertyName("trained_on_n_samples")]
        public int TrainedOnNSamples { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public interface IDesignOutcomePredictionClient
    {
        Task<DesignOutcomePrediction?> PredictAsync(
            DesignOutcomePredictionRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Talks to chemistrypredictor.py's /predict/design endpoint — the
    /// self-learning calibration model trained on WetScrubber.Database's
    /// DesignOutcome table. Same review-gate philosophy as chemistry
    /// predictions: show the prediction + confidence band + how many
    /// field outcomes it's based on, but never silently overwrite the
    /// deterministic engine's own number.
    /// </summary>
    public sealed class DesignOutcomePredictionClient : IDesignOutcomePredictionClient
    {
        private readonly HttpClient _httpClient;

        public DesignOutcomePredictionClient(HttpClient httpClient, IOptions<ChemistryPredictionOptions> options)
        {
            _httpClient = httpClient;
            var opts = options.Value;

            if (_httpClient.BaseAddress == null)
                _httpClient.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");

            _httpClient.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        }

        public async Task<DesignOutcomePrediction?> PredictAsync(
            DesignOutcomePredictionRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync("predict/design", request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                return await response.Content.ReadFromJsonAsync<DesignOutcomePrediction>(
                    cancellationToken: cancellationToken);
            }
            catch (HttpRequestException)
            {
                return null; // predictor unreachable — never block the design page on this
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null; // timed out
            }
        }
    }
}