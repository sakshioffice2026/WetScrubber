using System;

namespace WetScrubber.Business.Thermodynamics
{
    public enum EnhancementRegime
    {
        Physical = 0,
        Fast = 1,
        Instantaneous = 2
    }

    public sealed class ReactiveEnhancementInput
    {
        public string PollutantCode { get; set; } = "";
        public ReagentKind Reagent { get; set; } = ReagentKind.None;
        public double ReagentConcentrationEqPerL { get; set; }
        public double LiquidFilmCoeffMS { get; set; }
        public double PollutantLiquidDiffusivityM2S { get; set; }
        public double HenrysDimensionless { get; set; }          // Cg/Cl
        public double GasPartialPressureKPa { get; set; }
        public double TemperatureK { get; set; } = 298.15;

        /// <summary>mol reagent per mol pollutant (ChemicalReaction.StoichiometricRatio). 0 = unknown.</summary>
        public double StoichiometricRatio { get; set; }

        /// <summary>Verified reaction rate constant for EnhancementFactor (Hatta). Null = unknown.</summary>
        public double? ReactionRateConstantS_Inv { get; set; }
        public int ReactionOrder { get; set; } = 1;
    }

    public sealed class ReactiveEnhancementResult
    {
        public double Factor { get; set; } = 1.0;
        public bool ModelAvailable { get; set; }
        public string Note { get; set; } = "";
        public double HattaNumber { get; set; }
        public bool InstantaneousLimitApplied { get; set; }
        public EnhancementRegime Regime { get; set; } = EnhancementRegime.Physical;
    }

    /// <summary>
    /// Combines the existing EnhancementFactor (Hatta) with the
    /// instantaneous-reaction limit:
    ///   Ei = 1 + (D_B * C_B) / (nu * D_A * C_Ai)
    /// No stoichiometry or rate constants are hard-coded here; they must be
    /// supplied in the input. With neither, absorption stays physical (E = 1).
    /// </summary>
    public static class ReactiveEnhancementService
    {
        private const double GasConstantKPaM3KmolK = 8.314;
        private const double DiffusivityOHm2sAt298 = 5.27e-9;
        private const double DiffusivityHm2sAt298 = 9.31e-9;
        private const double MaxFactor = 1000.0;

        public static ReactiveEnhancementResult Compute(ReactiveEnhancementInput input)
        {
            var result = new ReactiveEnhancementResult();

            if (input == null || input.Reagent == ReagentKind.None || input.ReagentConcentrationEqPerL <= 0)
            {
                result.Note = "No reactive reagent specified; physical absorption only.";
                return result;
            }

            if (input.LiquidFilmCoeffMS <= 0 || input.PollutantLiquidDiffusivityM2S <= 0 || input.TemperatureK <= 0)
            {
                result.Note = "Insufficient data (kL, diffusivity, or temperature) for reactive enhancement.";
                return result;
            }

            double? hattaFactor = null;
            double? instantFactor = null;
            var regime = EnhancementRegime.Physical;

            if (input.ReactionRateConstantS_Inv.HasValue && input.ReactionRateConstantS_Inv.Value > 0)
            {
                var ha = EnhancementFactor.CalculateEnhancementFactor(
                    input.ReactionRateConstantS_Inv.Value,
                    input.ReagentConcentrationEqPerL,
                    input.PollutantLiquidDiffusivityM2S,
                    input.LiquidFilmCoeffMS,
                    input.ReactionOrder);

                result.HattaNumber = ha.HattaNumber;
                hattaFactor = ha.Factor;
                regime = ha.Regime == ReactionRegime.PhysicalAbsorption
                    ? EnhancementRegime.Physical
                    : EnhancementRegime.Fast;
            }

            if (input.StoichiometricRatio > 0
                && input.HenrysDimensionless > 0
                && input.GasPartialPressureKPa > 0)
            {
                double dB = (input.Reagent == ReagentKind.Caustic ? DiffusivityOHm2sAt298 : DiffusivityHm2sAt298)
                            * (input.TemperatureK / 298.15);
                double cB = input.ReagentConcentrationEqPerL;                                              // kmol/m3
                double cGas = input.GasPartialPressureKPa / (GasConstantKPaM3KmolK * input.TemperatureK);  // kmol/m3
                double cAi = Math.Max(cGas / input.HenrysDimensionless, 1e-12);                            // kmol/m3

                instantFactor = Math.Min(
                    1.0 + (dB * cB) / (input.StoichiometricRatio * input.PollutantLiquidDiffusivityM2S * cAi),
                    MaxFactor);
            }

            if (!hattaFactor.HasValue && !instantFactor.HasValue)
            {
                result.Note = "No rate constant or stoichiometry supplied; physical absorption only.";
                return result;
            }

            double factor;
            if (hattaFactor.HasValue && instantFactor.HasValue)
            {
                factor = Math.Min(hattaFactor.Value, instantFactor.Value);
                if (instantFactor.Value <= hattaFactor.Value)
                {
                    result.InstantaneousLimitApplied = true;
                    regime = EnhancementRegime.Instantaneous;
                }
            }
            else if (hattaFactor.HasValue)
            {
                factor = hattaFactor.Value;
            }
            else
            {
                factor = instantFactor!.Value;
                result.InstantaneousLimitApplied = true;
                regime = EnhancementRegime.Instantaneous;
            }

            result.ModelAvailable = true;
            result.Factor = Math.Max(factor, 1.0);
            result.Regime = regime;
            result.Note = hattaFactor.HasValue && instantFactor.HasValue
                ? "Hatta enhancement capped by the instantaneous-reaction limit."
                : hattaFactor.HasValue
                    ? "Hatta enhancement (no stoichiometry supplied for the instantaneous cap)."
                    : "Instantaneous-reaction limit (no rate constant supplied; Hatta number not evaluated).";
            return result;
        }
    }
}