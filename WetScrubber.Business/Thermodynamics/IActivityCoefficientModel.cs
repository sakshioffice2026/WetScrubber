namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// One species in a binary liquid mixture for NRTL. Kept separate
    /// from EosComponentInput — NRTL needs mole fraction + code only,
    /// not critical properties.
    /// </summary>
    public sealed class NrtlComponentInput
    {
        public string Code { get; set; } = "";
        public double MoleFraction { get; set; }
    }

    /// <summary>
    /// Resolved binary interaction parameters for one (A, B) pair,
    /// already oriented to match the A/B order passed into Evaluate —
    /// see LiquidActivityBuilder for the lookup + orientation step.
    /// </summary>
    public sealed class NrtlBinaryInput
    {
        public double Tau_AB { get; set; }
        public double Tau_BA { get; set; }
        public double Alpha { get; set; }
    }

    public sealed class ActivityCoefficientResult
    {
        public double GammaA { get; set; }
        public double GammaB { get; set; }
    }

    /// <summary>
    /// Liquid-phase activity coefficient model. Implementations correct
    /// for non-ideal solution behavior that flat DefaultDensity/DefaultPH
    /// values ignore — see ScrubbingLiquid.cs. This interface is the
    /// seam that lets ScrubberCalculationEngine's Henry's Law /
    /// equilibrium calc swap from ideal-solution (gamma = 1) to a real
    /// NRTL correction without its callers changing, same role
    /// IEquationOfState plays for the gas phase.
    /// </summary>
    public interface IActivityCoefficientModel
    {
        /// <param name="componentA">Typically the solvent (Water).</param>
        /// <param name="componentB">Typically the dissolved pollutant/solute.</param>
        /// <param name="binaryParameters">Tau/alpha for this specific pair,
        /// oriented so Tau_AB means A-in-B and Tau_BA means B-in-A.</param>
        ActivityCoefficientResult Evaluate(
            NrtlComponentInput componentA,
            NrtlComponentInput componentB,
            NrtlBinaryInput binaryParameters);
    }
}