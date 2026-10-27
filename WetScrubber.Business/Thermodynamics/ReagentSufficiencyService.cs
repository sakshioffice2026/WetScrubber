using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Thermodynamics
{
    public sealed class ReagentSufficiencyInput
    {
        public ReagentKind Reagent { get; set; } = ReagentKind.None;

        /// <summary>Fresh reagent concentration, equivalents per litre.</summary>
        public double ReagentEquivalentsPerL { get; set; }

        /// <summary>Equivalents of reagent consumed per mole of pollutant.</summary>
        public double StoichiometricRatio { get; set; }

        public double InletGasMoleFraction { get; set; }
        public double OutletGasMoleFraction { get; set; }

        /// <summary>Inlet gas, kmol per m3 of gas.</summary>
        public double GasKmolPerM3 { get; set; }

        /// <summary>Liquid flow, litres per m3 of gas.</summary>
        public double LiquidToGasLPerM3 { get; set; }

        /// <summary>Reactive enhancement input evaluated at the gas inlet (bottom) with fresh reagent.
        /// Partial pressure in this template must correspond to InletGasMoleFraction.</summary>
        public ReactiveEnhancementInput? EnhancementTemplate { get; set; }

        /// <summary>Required liquid-rate margin over the stoichiometric minimum.</summary>
        public double Margin { get; set; } = ReagentSufficiencyService.DefaultMargin;

        /// <summary>Minimum outlet-liquid pH for a strong-base reagent. NaN disables the check.</summary>
        public double MinOutletPH { get; set; } = ReagentSufficiencyService.DefaultMinOutletPH;
    }

    public sealed class ReagentSufficiencyResult
    {
        public bool Sufficient { get; set; }
        public double MinimumLiquidToGasLPerM3 { get; set; }
        public double RequiredLiquidToGasLPerM3 { get; set; }

        /// <summary>Fraction of the fresh reagent consumed between liquid inlet and outlet.</summary>
        public double ReagentUtilization { get; set; }

        public double ResidualReagentEqPerL { get; set; }
        public double OutletPH { get; set; } = double.NaN;
        public bool BackPressureRisk { get; set; }

        /// <summary>Enhancement at the liquid inlet (top: fresh reagent, outlet gas).</summary>
        public double EnhancementTop { get; set; } = 1.0;

        /// <summary>Enhancement at the liquid outlet (bottom: depleted reagent, inlet gas).</summary>
        public double EnhancementBottom { get; set; } = 1.0;

        /// <summary>Conservative single value for design: the smaller of the two ends.</summary>
        public double EnhancementDesign { get; set; } = 1.0;

        public bool EnhancementIsUpperBound { get; set; }
        public List<string> Notes { get; } = new();
    }

    public static class ReagentSufficiencyService
    {
        public const double DefaultMargin = 1.10;
        public const double DefaultMinOutletPH = 6.0;
        private const double MinimumResidualFraction = 0.05;

        public static ReagentSufficiencyResult Evaluate(ReagentSufficiencyInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var r = new ReagentSufficiencyResult();

            if (input.Reagent == ReagentKind.None
                || input.ReagentEquivalentsPerL <= 0
                || input.StoichiometricRatio <= 0
                || input.GasKmolPerM3 <= 0
                || input.LiquidToGasLPerM3 <= 0)
            {
                r.Notes.Add("Reagent sufficiency not evaluated: missing reagent, stoichiometry, or flow data.");
                return r;
            }

            double dy = Math.Max(input.InletGasMoleFraction - input.OutletGasMoleFraction, 0.0);

            // kmol of reagent equivalents required per m3 of gas.
            double requiredEqPerM3Gas = input.StoichiometricRatio * dy * input.GasKmolPerM3;

            r.MinimumLiquidToGasLPerM3 = requiredEqPerM3Gas / input.ReagentEquivalentsPerL * 1000.0;
            r.RequiredLiquidToGasLPerM3 = r.MinimumLiquidToGasLPerM3 * Math.Max(input.Margin, 1.0);

            double suppliedEqPerM3Gas = input.ReagentEquivalentsPerL * input.LiquidToGasLPerM3 / 1000.0;
            r.ReagentUtilization = suppliedEqPerM3Gas > 0 ? requiredEqPerM3Gas / suppliedEqPerM3Gas : double.PositiveInfinity;
            r.ResidualReagentEqPerL = input.ReagentEquivalentsPerL * Math.Max(1.0 - r.ReagentUtilization, 0.0);

            bool flowOk = input.LiquidToGasLPerM3 >= r.RequiredLiquidToGasLPerM3;
            if (!flowOk)
                r.Notes.Add(
                    $"REAGENT: liquid rate {input.LiquidToGasLPerM3:F3} L/m3 is below the required "
                    + $"{r.RequiredLiquidToGasLPerM3:F3} L/m3 (stoichiometric minimum "
                    + $"{r.MinimumLiquidToGasLPerM3:F3} x margin {input.Margin:F2}).");

            // pH and back-pressure check at the liquid outlet (bottom).
            bool residualLow = r.ResidualReagentEqPerL < MinimumResidualFraction * input.ReagentEquivalentsPerL;

            if (input.Reagent == ReagentKind.Caustic)
            {
                double ohMolL = Math.Max(r.ResidualReagentEqPerL, 1e-14);
                r.OutletPH = 14.0 + Math.Log10(ohMolL);
                if (!double.IsNaN(input.MinOutletPH) && r.OutletPH < input.MinOutletPH)
                    residualLow = true;
            }

            r.BackPressureRisk = residualLow;
            if (residualLow)
                r.Notes.Add(
                    "REAGENT: outlet liquid is close to depletion; the equilibrium back-pressure of the "
                    + "pollutant can no longer be neglected at the bottom of the tower.");

            r.Sufficient = flowOk && !r.BackPressureRisk;

            // Enhancement at both ends of the tower.
            if (input.EnhancementTemplate != null)
            {
                var template = input.EnhancementTemplate;

                double ratioTop = input.InletGasMoleFraction > 0
                    ? input.OutletGasMoleFraction / input.InletGasMoleFraction
                    : 1.0;

                var top = Clone(template, template.ReagentConcentrationEqPerL,
                    template.GasPartialPressureKPa * Math.Max(ratioTop, 1e-12));

                var bottom = Clone(template, Math.Max(r.ResidualReagentEqPerL, 0.0),
                    template.GasPartialPressureKPa);

                var eTop = ReactiveEnhancementService.Compute(top);
                var eBottom = ReactiveEnhancementService.Compute(bottom);

                r.EnhancementTop = eTop.Factor;
                r.EnhancementBottom = eBottom.Factor;
                r.EnhancementDesign = Math.Min(eTop.Factor, eBottom.Factor);
                r.EnhancementIsUpperBound = eTop.IsUpperBound || eBottom.IsUpperBound;
            }

            return r;
        }

        private static ReactiveEnhancementInput Clone(
            ReactiveEnhancementInput s, double reagentEqPerL, double partialPressureKPa)
        {
            return new ReactiveEnhancementInput
            {
                PollutantCode = s.PollutantCode,
                Reagent = s.Reagent,
                ReagentConcentrationEqPerL = reagentEqPerL,
                LiquidFilmCoeffMS = s.LiquidFilmCoeffMS,
                PollutantLiquidDiffusivityM2S = s.PollutantLiquidDiffusivityM2S,
                HenrysDimensionless = s.HenrysDimensionless,
                GasPartialPressureKPa = partialPressureKPa,
                TemperatureK = s.TemperatureK,
                StoichiometricRatio = s.StoichiometricRatio,
                ReactionRateConstantS_Inv = s.ReactionRateConstantS_Inv,
                ReactionOrder = s.ReactionOrder,
                RequireVerifiedRateConstant = s.RequireVerifiedRateConstant
            };
        }
    }
}
