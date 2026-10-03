using System.Text.Json.Serialization;
using EngineeringAI.Core.Abstractions;
using WetScrubber.Database.Enums;

namespace WetScrubber.Plugins
{
    public class WetScrubberDraftState : IDraftState
    {
        [JsonIgnore]
        public string SessionId { get; set; } = string.Empty;

        [JsonIgnore]
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

        // ── Gas stream ───────────────────────────────────────────
        public double? NormalFlowRate { get; set; }          // Nm³/hr
        public double? ActualFlowRate { get; set; }          // m³/hr
        public double? InletTemperature { get; set; }        // °C
        public double InletPressure { get; set; } = 101325;  // Pa
        public double MoistureContent { get; set; }          // % vol

        // ── Pollutant ────────────────────────────────────────────
        public string? PollutantName { get; set; }
        public double? InletConcentration { get; set; }      // mg/Nm³
        public double TargetRemovalEfficiency { get; set; } = 95; // %

        // ── Scrubbing liquid ─────────────────────────────────────
        public string LiquidName { get; set; } = "Caustic Soda";
        public double LiquidConcentration { get; set; } = 10;     // % wt
        public double LiquidPH { get; set; } = 12;
        public double LiquidTemperature { get; set; } = 25;       // °C
        public double LiquidToGasRatio { get; set; } = 3.0;       // L/m³ gas

        // ── Packing and materials ────────────────────────────────
        public string PackingCode { get; set; } = "PallRing50";
        public ConstructionMaterial ShellMaterial { get; set; } = ConstructionMaterial.FRP;
        public ConstructionMaterial InternalMaterial { get; set; } = ConstructionMaterial.PP;

        public IReadOnlyList<string> GetMissingMandatoryFields()
        {
            var missing = new List<string>();

            if (NormalFlowRate is null && ActualFlowRate is null)
            {
                missing.Add("gas flow rate (Nm³/hr or m³/hr)");
            }

            if (InletTemperature is null)
            {
                missing.Add("inlet gas temperature (°C)");
            }

            if (string.IsNullOrWhiteSpace(PollutantName))
            {
                missing.Add("pollutant name");
            }

            if (InletConcentration is null)
            {
                missing.Add("inlet pollutant concentration (mg/Nm³)");
            }

            return missing;
        }
    }
}