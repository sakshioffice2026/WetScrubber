using System;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Wilke-Chang (1955) estimation of the liquid-phase diffusion
    /// coefficient of a dilute solute in a solvent:
    ///
    ///   D_AB [cm^2/s] = 7.4e-8 * sqrt(phi * M_solvent) * T
    ///                   / (mu_solvent[cP] * V_A^0.6)
    ///
    /// where phi is the solvent association factor (water = 2.6),
    /// M_solvent is solvent molecular weight [g/mol], T is temperature [K],
    /// mu_solvent is solvent viscosity [cP], and V_A is the solute's molar
    /// volume at its normal boiling point [cm^3/mol].
    ///
    /// Feeds the Sherwood/Schmidt correlation (next Phase 2 step) that
    /// replaces ScrubberCalculationEngine's hardcoded
    /// DefaultLiquidFilmCoeff = 0.01. This class only produces D_AB —
    /// it does not touch kg/kl itself.
    /// </summary>
    public static class WilkeChangDiffusivity
    {
        private const double WilkeChangConstant = 7.4e-8;

        /// <summary>
        /// Result is null when required data is missing — mirrors
        /// GetVanTHoffTempCoeff's contract: caller decides the fallback
        /// (e.g. keep the current hardcoded film coefficient), this
        /// method never invents a value or throws for a data gap.
        /// </summary>
        public static double? TryCalculate(
            DiffusionSpeciesData? solute,
            DiffusionSpeciesData? solvent,
            double solventMolecularWeight,
            double solventViscosityMPas,
            double temperatureK)
        {
            if (solute?.MolarVolumeAtBoilingPointCm3Mol is not double vA || vA <= 0)
                return null;

            if (solvent?.AssociationFactor is not double phi || phi <= 0)
                return null;

            if (solventMolecularWeight <= 0 || solventViscosityMPas <= 0 || temperatureK <= 0)
                return null;

            // mPa·s and cP are numerically identical, so no conversion needed.
            double muSolventCp = solventViscosityMPas;

            double numerator = WilkeChangConstant * Math.Sqrt(phi * solventMolecularWeight) * temperatureK;
            double denominator = muSolventCp * Math.Pow(vA, 0.6);

            double dAbCm2S = numerator / Math.Max(denominator, 1e-12);

            return dAbCm2S > 0 ? dAbCm2S : null;
        }

        /// <summary>Convenience unit conversion: cm^2/s -> m^2/s, the unit
        /// the Sherwood/Reynolds correlation step will want.</summary>
        public static double CentimeterSqPerSecToMeterSqPerSec(double dAbCm2S)
            => dAbCm2S * 1e-4;
    }
}