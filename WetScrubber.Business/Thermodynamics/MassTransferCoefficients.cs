namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Onda-based film coefficients and derived volumetric coefficients.
    /// </summary>
    public sealed class MassTransferCoefficients
    {
        public double WettedAreaM2M3 { get; set; }
        public double GasFilmCoeffKmolM2SPa { get; set; }
        public double LiquidFilmCoeffMS { get; set; }
        public double EnhancementFactor { get; set; } = 1.0;

        public double GasSideKgaKmolM3HrKPa { get; set; }
        public double LiquidSideKlaKmolM3HrMolL { get; set; }
        public double OverallKGaKmolM3HrKPa { get; set; }

        public double GasSideResistanceFraction { get; set; }
        public double LiquidSideResistanceFraction { get; set; }
    }
}
