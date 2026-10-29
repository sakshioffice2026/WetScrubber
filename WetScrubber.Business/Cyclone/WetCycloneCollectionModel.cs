using System;
using System.Collections.Generic;
using WetScrubber.Business.Common;
using WetScrubber.Business.Particulate;

namespace WetScrubber.Business.Cyclone
{
    /// <summary>
    /// Inputs for the wet cyclone calculation (SI units).
    /// </summary>
    public sealed class WetCycloneInput
    {
        public double GasFlowM3s { get; set; }
        public double TemperatureK { get; set; }
        public double PressurePa { get; set; } = 101325.0;
        public double GasViscosityPas { get; set; }
        public double GasMolarMassKgMol { get; set; } = CunninghamSlip.AirMolarMassKgMol;
        public double? GasDensityKgM3 { get; set; }           // when null, ideal gas from P, T, M

        public ParticleSizeDistribution? Distribution { get; set; }
        public double ParticleDensityKgM3 { get; set; }

        public double InletVelocityMs { get; set; }
        public int ParallelUnits { get; set; } = 1;
        public CycloneRatios? Ratios { get; set; }            // default Lapple

        public double LiquidToGasRatioLM3 { get; set; }       // L per m3 of gas
        public double LiquidInjectionPressurePa { get; set; } = 2.0e5;
        public double PumpEfficiency { get; set; } = 0.60;
        public double FanEfficiency { get; set; } = WetCyclonePressureDrop.DefaultFanEfficiency;
        public double WetCorrectionFactor { get; set; } = WetCyclonePressureDrop.DefaultWetCorrectionFactor;

        public bool ApplyCunninghamSlip { get; set; } = false; // Lapple form is Stokes-only by default

        public double? TargetEfficiency { get; set; }          // 0-1
        public double? MaxPressureDropPa { get; set; }

        // Screening limits on inlet velocity (m/s). Unverified defaults; confirm against source.
        public double VelocityWarnMinMs { get; set; } = 15.0;
        public double VelocityWarnMaxMs { get; set; } = 27.0;
        public double VelocityHardMinMs { get; set; } = 8.0;
        public double VelocityHardMaxMs { get; set; } = 35.0;

        // Optional independent fractional-efficiency curve (e.g. Leith-Licht) used only as a cross-check.
        public Func<double, double>? CrossCheckFractionalEfficiency { get; set; }
        public double CrossCheckToleranceAbs { get; set; } = 0.05;
    }

    /// <summary>
    /// Wet cyclone particulate collection.
    /// Cut diameter (Lapple):  d50 = sqrt( 9 * mu * b / (2 * pi * Ne * vi * (rho_p - rho_g)) ).
    /// Fractional efficiency (Lapple):  eta(d) = 1 / (1 + (d50 / d)^2).
    /// With Cunningham slip enabled the particle term d^2 is replaced by C(d) * d^2.
    /// Overall efficiency: mass-weighted sum over the size distribution.
    /// Water rate = (L/G) * Q; pump power = Q_L * dP_inj / eta_pump.
    /// Scope: particulate only. Gas absorption is not modeled.
    /// Status: NotYetValidated. Lapple form is standard; wet-wall or spray enhancement is not encoded.
    /// </summary>
    public static class WetCycloneCollectionModel
    {
        private const double GasConstant = CunninghamSlip.UniversalGasConstant;

        public static WetCycloneResult Calculate(WetCycloneInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            RequirePositive(nameof(input.GasFlowM3s), input.GasFlowM3s);
            RequirePositive(nameof(input.TemperatureK), input.TemperatureK);
            RequirePositive(nameof(input.PressurePa), input.PressurePa);
            RequirePositive(nameof(input.GasViscosityPas), input.GasViscosityPas);
            RequirePositive(nameof(input.GasMolarMassKgMol), input.GasMolarMassKgMol);
            RequirePositive(nameof(input.ParticleDensityKgM3), input.ParticleDensityKgM3);
            RequirePositive(nameof(input.InletVelocityMs), input.InletVelocityMs);
            if (input.Distribution == null)
                throw new ArgumentException("A particle size distribution is required.", nameof(input));
            if (!(input.LiquidToGasRatioLM3 >= 0) || double.IsInfinity(input.LiquidToGasRatioLM3))
                throw new ArgumentOutOfRangeException(nameof(input.LiquidToGasRatioLM3), "L/G must be non-negative and finite.");
            RequirePositive(nameof(input.PumpEfficiency), input.PumpEfficiency);

            var result = new WetCycloneResult();

            double rhoG = input.GasDensityKgM3
                ?? input.PressurePa * input.GasMolarMassKgMol / (GasConstant * input.TemperatureK);
            RequirePositive("GasDensityKgM3", rhoG);

            if (input.ParticleDensityKgM3 <= rhoG)
            {
                result.AddInfeasible("CYC_DENSITY", "Particle density must exceed gas density for centrifugal separation.");
                return result;
            }

            var geometry = WetCycloneGeometry.FromFlowAndInletVelocity(
                input.GasFlowM3s, input.InletVelocityMs, input.ParallelUnits, input.Ratios);

            double lambda = CunninghamSlip.MeanFreePathM(
                input.GasViscosityPas, input.TemperatureK, input.PressurePa, input.GasMolarMassKgMol);

            double stokesCoefficient = StokesCutCoefficient(
                input.GasViscosityPas, geometry, input.InletVelocityMs, input.ParticleDensityKgM3, rhoG);

            Func<double, double> eta = d =>
            {
                double slip = input.ApplyCunninghamSlip ? CunninghamSlip.Factor(d, lambda) : 1.0;
                double ratio = stokesCoefficient / (d * d * slip);   // (d50 / d)^2 equivalent
                return 1.0 / (1.0 + ratio);
            };

            // Cut diameter
            double d50;
            if (!input.ApplyCunninghamSlip)
            {
                d50 = Math.Sqrt(stokesCoefficient);
            }
            else
            {
                double? found = FractionalEfficiency.FindCutDiameterM(eta, 0.5);
                if (found.HasValue)
                {
                    d50 = found.Value;
                }
                else
                {
                    d50 = Math.Sqrt(stokesCoefficient);
                    result.AddWarning("CYC_D50_SLIP", "Cut diameter with slip could not be bracketed; Stokes value reported.");
                }
            }

            // Fractional and overall efficiency
            var frac = FractionalEfficiency.Evaluate(input.Distribution, eta);

            // Pressure drop and fan power
            var dp = WetCyclonePressureDrop.Calculate(
                geometry, input.GasFlowM3s, rhoG, input.WetCorrectionFactor, input.FanEfficiency);

            // Water rate and pump power
            double waterM3s = input.LiquidToGasRatioLM3 * input.GasFlowM3s / 1000.0;
            double pumpPower = waterM3s * input.LiquidInjectionPressurePa / input.PumpEfficiency;

            // Populate result
            result.Geometry = geometry;
            result.ParallelUnits = geometry.ParallelUnits;
            result.BodyDiameterM = geometry.BodyDiameterM;
            result.InletAreaM2 = geometry.InletAreaM2;
            result.GasOutletDiameterM = geometry.GasOutletDiameterM;
            result.TotalHeightM = geometry.TotalHeightM;
            result.EffectiveTurns = geometry.EffectiveTurns;

            result.GasFlowM3s = input.GasFlowM3s;
            result.InletVelocityMs = geometry.InletVelocityMs(input.GasFlowM3s);
            result.GasDensityKgM3 = rhoG;
            result.LiquidToGasRatioLM3 = input.LiquidToGasRatioLM3;

            result.CutDiameterM = d50;
            result.OverallEfficiency = frac.OverallEfficiency;
            result.FractionalEfficiency = frac.Points;

            result.PressureDropPa = dp.PressureDropPa;
            result.DryPressureDropPa = dp.DryPressureDropPa;
            result.FanPowerW = dp.FanPowerW;
            result.WaterRateM3s = waterM3s;
            result.PumpPowerW = pumpPower;

            foreach (var w in frac.Warnings) result.AddWarning("CYC_PSD", w);

            // Scope and status notes
            result.AddInfo("CYC_SCOPE", "Particulate collection only; gas absorption is not modeled.");
           
            if (Math.Abs(input.WetCorrectionFactor - 1.0) < 1.0e-12)
                result.AddInfo("CYC_WET", "No wet-wall or spray correction applied; dry-cyclone correlations used.");

            // Feasibility and range checks
            double vi = result.InletVelocityMs;
            if (vi < input.VelocityHardMinMs)
                result.AddInfeasible("CYC_V_LOW", $"Inlet velocity {vi:F1} m/s is below the minimum {input.VelocityHardMinMs:F0} m/s.");
            else if (vi > input.VelocityHardMaxMs)
                result.AddInfeasible("CYC_V_HIGH", $"Inlet velocity {vi:F1} m/s exceeds the maximum {input.VelocityHardMaxMs:F0} m/s (re-entrainment risk).");
            else if (vi < input.VelocityWarnMinMs || vi > input.VelocityWarnMaxMs)
                result.AddWarning("CYC_V_RANGE", $"Inlet velocity {vi:F1} m/s is outside the usual {input.VelocityWarnMinMs:F0}-{input.VelocityWarnMaxMs:F0} m/s band.");

            if (input.MaxPressureDropPa.HasValue && result.PressureDropPa > input.MaxPressureDropPa.Value)
                result.AddInfeasible("CYC_DP", $"Pressure drop {result.PressureDropPa:F0} Pa exceeds the ceiling {input.MaxPressureDropPa.Value:F0} Pa.");

            if (input.TargetEfficiency.HasValue)
            {
                result.TargetEfficiency = input.TargetEfficiency.Value;
                result.TargetMet = result.OverallEfficiency >= input.TargetEfficiency.Value;
                if (result.TargetMet == false)
                    result.AddInfeasible("CYC_TARGET", $"Target efficiency {input.TargetEfficiency.Value * 100:F1}% is not reachable; calculated {result.OverallEfficiencyPercent:F1}%.");
            }

            // Optional independent cross-check
            if (input.CrossCheckFractionalEfficiency != null)
            {
                var cross = FractionalEfficiency.Evaluate(input.Distribution, input.CrossCheckFractionalEfficiency);
                result.CrossCheckOverallEfficiency = cross.OverallEfficiency;
                result.CrossCheckDeviation = Math.Abs(cross.OverallEfficiency - result.OverallEfficiency);
                if (result.CrossCheckDeviation > input.CrossCheckToleranceAbs)
                    result.AddWarning("CYC_XCHECK", $"Cross-check efficiency differs by {result.CrossCheckDeviation * 100:F1} percentage points.");
            }

            return result;
        }

        /// <summary>
        /// Coefficient K such that d50^2 = K (Stokes, no slip):  K = 9 * mu * b / (2 * pi * Ne * vi * (rho_p - rho_g)).
        /// </summary>
        private static double StokesCutCoefficient(
            double gasViscosityPas,
            WetCycloneGeometry geometry,
            double inletVelocityMs,
            double particleDensityKgM3,
            double gasDensityKgM3)
        {
            return 9.0 * gasViscosityPas * geometry.InletWidthM
                   / (2.0 * Math.PI * geometry.EffectiveTurns * inletVelocityMs * (particleDensityKgM3 - gasDensityKgM3));
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Value must be a positive finite number.");
        }
    }
}
