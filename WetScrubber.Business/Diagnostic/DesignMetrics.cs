namespace WetScrubber.Business.Diagnostics
{
    /// <summary>
    /// The numeric inputs the diagnostics rule table is evaluated against.
    ///
    /// IMPORTANT
    /// =========
    /// This is a plain data carrier — it deliberately does NOT reference
    /// WetScrubber.Services.CalculationResult, because that type lives in
    /// the web project, which references WetScrubber.Business (not the
    /// other way around). Callers map their CalculationResult (or
    /// ScrubberGeometry) onto this DTO before calling the engine.
    ///
    /// Every value here is something the deterministic calculation engine
    /// already computed. This class never computes anything itself.
    /// </summary>
    public sealed class DesignMetrics
    {
        public string ScrubberType { get; init; } = string.Empty;

        public double AbsorptionFactor { get; init; }

        public double ActualLGRatio { get; init; }
        public double MinLGRatio { get; init; }

        public double PressureDrop { get; init; }        // Pa, total
        public double GasVelocity { get; init; }          // m/s
        public double PackingHeight { get; init; }         // m

        public double RemovalEfficiency { get; init; }    // %
        public double? TargetRemovalEfficiency { get; init; } // % (optional — from the pollutant target)
        public string? PackingCode { get; init; }
        public string? PackingSizingMethod { get; init; }
        public bool IsLimestoneSlurry { get; init; }
        public double SolidsLoadingWtPercent { get; init; }
    }
}
