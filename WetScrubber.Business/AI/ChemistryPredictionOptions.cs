using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace WetScrubber.Business.AI
{
    public class ChemistryPredictionOptions
    {
        public const string SectionName = "ChemistryPrediction";

        public string BaseUrl { get; set; } = "http://localhost:8500";

        public int TimeoutSeconds { get; set; } = 10;
    }

    public interface IChemistryPredictionClient
    {
        Task<ChemistryPrediction?> PredictAsync(
            string pollutantName,
            double pollutantMolecularWeight,
            string liquidType,
            CancellationToken cancellationToken = default);
    }

    public sealed class ChemistryPrediction
    {
        [JsonPropertyName("henrys_law_constant")]
        public double HenrysLawConstant { get; set; }

        [JsonPropertyName("max_removal_efficiency")]
        public double MaxRemovalEfficiency { get; set; }

        [JsonPropertyName("stoichiometric_ratio")]
        public double StoichiometricRatio { get; set; }

        [JsonPropertyName("min_operating_ph")]
        public double MinOperatingPh { get; set; }

        [JsonPropertyName("max_operating_ph")]
        public double MaxOperatingPh { get; set; }

        // "HighSimilarity" | "ModerateSimilarity" | "LowSimilarity" — mirrors
        // WetScrubber.Database.Enums.ConfidenceBand, kept as a plain string
        // here since this client has no reference to the Database project.
        [JsonPropertyName("confidence_band")]
        public string ConfidenceBand { get; set; } = "LowSimilarity";

        [JsonPropertyName("nearest_matches")]
        public string[] NearestMatches { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Talks to the chemistry_predictor.py service — the MVP stand-in for a
    /// future GNN. Never used to auto-fill a design silently: callers show
    /// the prediction + confidence band and require a human to promote it
    /// into the real ChemicalReaction table before it's trusted, same
    /// review gate as the AI narrative.
    /// </summary>
    public sealed class ChemistryPredictionClient : IChemistryPredictionClient
    {
        private readonly HttpClient _httpClient;
        private readonly ChemistryPredictionOptions _options;

        public ChemistryPredictionClient(HttpClient httpClient, IOptions<ChemistryPredictionOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;

            if (_httpClient.BaseAddress == null)
                _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");

            _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        }

        public async Task<ChemistryPrediction?> PredictAsync(
            string pollutantName,
            double pollutantMolecularWeight,
            string liquidType,
            CancellationToken cancellationToken = default)
        {
            var request = new
            {
                pollutant_name = pollutantName,
                pollutant_molecular_weight = pollutantMolecularWeight,
                liquid_type = liquidType
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("predict", request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null; // caller falls back to "no prediction available"

                return await response.Content.ReadFromJsonAsync<ChemistryPrediction>(
                    cancellationToken: cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Predictor unreachable — never block the design form on this.
                return null;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null; // timed out
            }
        }
    }
}