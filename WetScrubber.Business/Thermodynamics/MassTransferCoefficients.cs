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

        /// <summary>
        /// Volumetric liquid-film coefficient kL·a (including enhancement
        /// factor), in 1/hr. Equals kL [m/s] x E x a [m2/m3] x 3600.
        /// </summary>
        public double LiquidSideKlaPerHr { get; set; }

        public double OverallKGaKmolM3HrKPa { get; set; }

        public double GasSideResistanceFraction { get; set; }
        public double LiquidSideResistanceFraction { get; set; }
    }
}