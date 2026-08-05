using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Minimal lookup contract for NrtlBinaryParameter, same "plain
    /// dictionary-testable, no EF dependency" reasoning as
    /// IComponentPropertyLookup in GasMixtureBuilder.cs.
    ///
    /// Implementations must return the pair already oriented to
    /// (componentACode, componentBCode) — i.e. if the underlying table
    /// row was stored as (B, A), the implementation swaps Tau_AB/Tau_BA
    /// before returning, so callers never have to reason about storage
    /// order.
    /// </summary>
    public interface INrtlBinaryParameterLookup
    {
        NrtlBinaryInput? GetPair(string componentACode, string componentBCode);
    }

    /// <summary>
    /// Builds a [Solvent, Solute] binary liquid mixture for
    /// IActivityCoefficientModel. Solvent is always the scrubbing
    /// liquid's base (Water, "H2O") and solute is the dissolved
    /// pollutant — same single-pollutant, single-solvent assumption
    /// GasMixtureBuilder makes on the gas side, for the same reason
    /// (ScrubberCalculationEngine's current single-pollutant design).
    ///
    /// LIMITATION (flagged, not hidden): NrtlBinaryParameter ships with
    /// NO seed data (see NrtlBinaryParameter.cs — fabricating tau/alpha
    /// is explicitly disallowed). TryBuildWaterSoluteBinary returns
    /// null whenever the pair isn't found, and the caller
    /// (ScrubberCalculationEngine) must fall back to ideal-solution
    /// behavior (gamma = 1) rather than throw — this mirrors
    /// GetActualGasDensity's fallback-on-miss pattern for the EOS.
    /// </summary>
    public static class LiquidActivityBuilder
    {
        public const string WaterCode = "H2O";

        public static bool TryBuildWaterSoluteBinary(
            string soluteCode,
            double soluteMoleFraction,
            INrtlBinaryParameterLookup lookup,
            out NrtlComponentInput water,
            out NrtlComponentInput solute,
            out NrtlBinaryInput binary)
        {
            if (lookup == null) throw new ArgumentNullException(nameof(lookup));

            water = new NrtlComponentInput { Code = WaterCode, MoleFraction = 0 };
            solute = new NrtlComponentInput { Code = soluteCode, MoleFraction = 0 };
            binary = null!;

            if (string.IsNullOrWhiteSpace(soluteCode) || soluteCode == WaterCode)
                return false;

            var pair = lookup.GetPair(WaterCode, soluteCode);
            if (pair == null)
                return false;

            double xSolute = Math.Max(soluteMoleFraction, 0.0);
            double xWater = 1.0 - xSolute;

            water = new NrtlComponentInput { Code = WaterCode, MoleFraction = xWater };
            solute = new NrtlComponentInput { Code = soluteCode, MoleFraction = xSolute };
            binary = pair;
            return true;
        }
    }
}