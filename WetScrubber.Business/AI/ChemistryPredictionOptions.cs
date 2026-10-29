using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace WetScrubber.Business.AI
{
    /// <summary>
    /// Bound from the "ChemistryPrediction" configuration section.
    /// </summary>
    public sealed class ChemistryPredictionOptions
    {
        public const string SectionName = "ChemistryPrediction";

        public string BaseUrl { get; set; } = "http://localhost:8500/";

        public int TimeoutSeconds { get; set; } = 5;
    }

    /// <summary>
    /// Response of chemistrypredictor.py POST /predict.
    /// </summary>
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

        [JsonPropertyName("confidence_band")]
        public string ConfidenceBand { get; set; } = "LowSimilarity";

        [JsonPropertyName("nearest_matches")]
        public List<string> NearestMatches { get; set; } = new List<string>();

        [JsonPropertyName("source")]
        public string Source { get; set; } = "none";

        [JsonPropertyName("trained_on_n_samples")]
        public int TrainedOnNSamples { get; set; }
    }

    internal sealed class ChemistryPredictionRequest
    {
        [JsonPropertyName("pollutant_name")]
        public string PollutantName { get; set; } = "";

        [JsonPropertyName("pollutant_molecular_weight")]
        public double PollutantMolecularWeight { get; set; }

        [JsonPropertyName("liquid_type")]
        public string LiquidType { get; set; } = "";
    }

    public interface IChemistryPredictionClient
    {
        Task<ChemistryPrediction?> PredictAsync(
            string pollutantName,
            double pollutantMolecularWeight,
            string liquidType,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Talks to chemistrypredictor.py's /predict endpoint.
    /// </summary>
    public sealed class ChemistryPredictionClient : IChemistryPredictionClient
    {
        private readonly HttpClient _httpClient;

        public ChemistryPredictionClient(HttpClient httpClient, IOptions<ChemistryPredictionOptions> options)
        {
            _httpClient = httpClient;
            var opts = options.Value;

            if (_httpClient.BaseAddress == null)
                _httpClient.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");

            _httpClient.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        }

        public async Task<ChemistryPrediction?> PredictAsync(
            string pollutantName,
            double pollutantMolecularWeight,
            string liquidType,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new ChemistryPredictionRequest
                {
                    PollutantName = pollutantName,
                    PollutantMolecularWeight = pollutantMolecularWeight,
                    LiquidType = liquidType
                };

                var response = await _httpClient.PostAsJsonAsync("predict", request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                return await response.Content.ReadFromJsonAsync<ChemistryPrediction>(
                    cancellationToken: cancellationToken);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}