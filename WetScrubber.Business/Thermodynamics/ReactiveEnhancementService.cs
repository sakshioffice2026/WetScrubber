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

        /// <summary>Reagent concentration, equivalents per litre (= kmol/m3 of reactive sites).</summary>
        public double ReagentConcentrationEqPerL { get; set; }

        public double LiquidFilmCoeffMS { get; set; }
        public double PollutantLiquidDiffusivityM2S { get; set; }

        /// <summary>Dimensionless Henry's constant, Cg/Cl.</summary>
        public double HenrysDimensionless { get; set; }

        public double GasPartialPressureKPa { get; set; }
        public double TemperatureK { get; set; } = 298.15;

        /// <summary>Equivalents of reagent consumed per mole of pollutant (SO2 + 2NaOH: 2). 0 = unknown.</summary>
        public double StoichiometricRatio { get; set; }

        /// <summary>Verified rate constant, rate = k*C_A*C_B^n, in (m3/kmol)^n/s. Null = unknown.</summary>
        public double? ReactionRateConstantS_Inv { get; set; }
        public int ReactionOrder { get; set; } = 1;
    }

    public sealed class ReactiveEnhancementResult
    {
        public double Factor { get; set; } = 1.0;
        public bool ModelAvailable { get; set; }
        public string Note { get; set; } = "";
        public double HattaNumber { get; set; }
        public double InstantaneousFactor { get; set; }
        public bool InstantaneousLimitApplied { get; set; }
        public EnhancementRegime Regime { get; set; } = EnhancementRegime.Physical;
    }

    /// <summary>
    /// Enhancement factor for gas absorption with chemical reaction.
    ///   Instantaneous limit:  Ei = 1 + (D_B * C_B) / (nu * D_A * C_Ai)
    ///   Hatta (pseudo-first-order):  Ha = sqrt(k * C_B^n * D_A) / kL
    ///   Combined (DeCoursey 1974):
    ///     E = -Ha^2/(2(Ei-1)) + sqrt( Ha^4/(4(Ei-1)^2) + Ei*Ha^2/(Ei-1) + 1 )
    /// Without both a rate constant and stoichiometry the result stays physical (E = 1)
    /// unless only one of the two limits can be evaluated.
    /// </summary>
    public static class ReactiveEnhancementService
    {
        private const double GasConstantKPaM3KmolK = 8.314462;
        private const double DiffusivityOHm2sAt298 = 5.27e-9;
        private const double DiffusivityHm2sAt298 = 9.31e-9;
        private const double MaxFactor = EnhancementFactor.MaxFactor;

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

            double? ha = null;
            double? hattaFactor = null;
            double? instantFactor = null;

            if (input.ReactionRateConstantS_Inv.HasValue && input.ReactionRateConstantS_Inv.Value > 0)
            {
                var h = EnhancementFactor.CalculateEnhancementFactor(
                    input.ReactionRateConstantS_Inv.Value,
                    input.ReagentConcentrationEqPerL,
                    input.PollutantLiquidDiffusivityM2S,
                    input.LiquidFilmCoeffMS,
                    input.ReactionOrder);

                ha = h.HattaNumber;
                hattaFactor = h.Factor;
                result.HattaNumber = h.HattaNumber;
            }

            if (input.StoichiometricRatio > 0
                && input.HenrysDimensionless > 0
                && input.GasPartialPressureKPa > 0)
            {
                double dB = (input.Reagent == ReagentKind.Caustic ? DiffusivityOHm2sAt298 : DiffusivityHm2sAt298)
                            * (input.TemperatureK / 298.15);
                double cB = input.ReagentConcentrationEqPerL;                                              // kmol/m3
                double cGas = input.GasPartialPressureKPa / (GasConstantKPaM3KmolK * input.TemperatureK);  // kmol/m3
                double cAi = Math.Max(cGas / input.HenrysDimensionless, 1e-12);                            // kmol/m3 (Cl* = Cg/H)

                instantFactor = Math.Min(
                    1.0 + (dB * cB) / (input.StoichiometricRatio * input.PollutantLiquidDiffusivityM2S * cAi),
                    MaxFactor);
                result.InstantaneousFactor = instantFactor.Value;
            }

            if (!hattaFactor.HasValue && !instantFactor.HasValue)
            {
                result.Note = "No rate constant or stoichiometry supplied; physical absorption only.";
                return result;
            }

            double factor;
            EnhancementRegime regime;

            if (ha.HasValue && instantFactor.HasValue)
            {
                double ei = instantFactor.Value;
                double haVal = ha.Value;

                if (ei <= 1.0 + 1e-9)
                {
                    factor = 1.0;
                }
                else
                {
                    double ha2 = haVal * haVal;
                    double a = ha2 / (2.0 * (ei - 1.0));
                    factor = -a + Math.Sqrt(a * a + ei * ha2 / (ei - 1.0) + 1.0);
                }

                factor = Math.Min(Math.Max(factor, 1.0), Math.Min(ei, MaxFactor));

                if (haVal >= 5.0 * ei)
                {
                    result.InstantaneousLimitApplied = true;
                    regime = EnhancementRegime.Instantaneous;
                }
                else
                {
                    regime = haVal < 0.1 ? EnhancementRegime.Physical : EnhancementRegime.Fast;
                }

                result.Note = "Hatta number combined with the instantaneous-reaction limit (DeCoursey).";
            }
            else if (hattaFactor.HasValue)
            {
                factor = hattaFactor.Value;
                regime = ha.Value < 0.1 ? EnhancementRegime.Physical : EnhancementRegime.Fast;
                result.Note = "Hatta enhancement (no stoichiometry supplied for the instantaneous cap).";
            }
            else
            {
                factor = instantFactor.Value;
                result.InstantaneousLimitApplied = true;
                regime = EnhancementRegime.Instantaneous;
                result.Note = "Instantaneous-reaction limit (no rate constant supplied; Hatta number not evaluated).";
            }

            result.ModelAvailable = true;
            result.Factor = Math.Max(factor, 1.0);
            result.Regime = regime;
            return result;
        }
    }
}