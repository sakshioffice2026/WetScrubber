using System;
using System.Collections.Generic;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class OndaResult
    {
        public double WettedAreaM2M3 { get; set; }        // aW
        public double GasFilmCoeffKmolM2SPa { get; set; }  // kG, partial-pressure basis
        public double LiquidFilmCoeffMS { get; set; }      // kL, concentration basis
    }

    public sealed record GasPhaseProperties(
        double TemperatureK,
        double DensityKgM3,
        double ViscosityPas,
        double DiffusivityM2S);

    public sealed record LiquidPhaseProperties(
        double TemperatureK,
        double DensityKgM3,
        double ViscosityPas,
        double DiffusivityM2S,
        double SurfaceTensionNM);

    /// <summary>
    /// Onda, Takeuchi and Okumoto (1968) packed-column correlation (SI form).
    /// Gas properties are evaluated at the gas temperature, liquid properties at
    /// the liquid temperature. No silent clamping: invalid input throws.
    /// </summary>
    public static class OndaMassTransferCorrelation
    {
        private const double GravityAccel = 9.81;              // m/s2
        private const double RPaM3KmolK = 8314.462;            // Pa*m3/(kmol*K)

        public static void RequireComplete(PackingMassTransferInput packing)
        {
            if (packing == null) throw new ArgumentNullException(nameof(packing));

            var missing = new List<string>();
            if (!(packing.SpecificAreaM2M3 > 0)) missing.Add(nameof(packing.SpecificAreaM2M3));
            if (!(packing.NominalSizeM > 0)) missing.Add(nameof(packing.NominalSizeM));
            if (!(packing.CriticalSurfaceTensionNM > 0)) missing.Add(nameof(packing.CriticalSurfaceTensionNM));
            if (!(packing.LiquidSurfaceTensionNM > 0)) missing.Add(nameof(packing.LiquidSurfaceTensionNM));
            if (!(packing.TowerAreaM2 > 0)) missing.Add(nameof(packing.TowerAreaM2));
            if (!(packing.GasMassFlowKgS > 0)) missing.Add(nameof(packing.GasMassFlowKgS));
            if (!(packing.LiquidMassFlowKgS > 0)) missing.Add(nameof(packing.LiquidMassFlowKgS));

            if (missing.Count > 0)
                throw new IncompletePackingInputException(missing);
        }

        public static OndaResult Calculate(
            PackingMassTransferInput packing,
            GasPhaseProperties gas,
            LiquidPhaseProperties liquid)
        {
            RequireComplete(packing);
            if (gas == null) throw new ArgumentNullException(nameof(gas));
            if (liquid == null) throw new ArgumentNullException(nameof(liquid));

            return CalculateCore(
                packing.SpecificAreaM2M3,
                packing.NominalSizeM,
                packing.CriticalSurfaceTensionNM,
                packing.LiquidMassFlowKgS / packing.TowerAreaM2,
                packing.GasMassFlowKgS / packing.TowerAreaM2,
                gas,
                liquid);
        }

        // Legacy signature retained so existing callers still link.
        // A single temperature is applied to both phases; prefer the overload above.
        public static OndaResult Calculate(
            double packingSpecificAreaM2M3,
            double nominalPackingSizeM,
            double criticalSurfaceTensionNM,
            double liquidSurfaceTensionNM,
            double liquidMassVelocityKgM2S,
            double gasMassVelocityKgM2S,
            double liquidDensityKgM3,
            double gasDensityKgM3,
            double liquidViscosityPas,
            double gasViscosityPas,
            double liquidDiffusivityM2S,
            double gasDiffusivityM2S,
            double temperatureK,
            double pressureKPa)
        {
            var missing = new List<string>();
            if (!(packingSpecificAreaM2M3 > 0)) missing.Add(nameof(packingSpecificAreaM2M3));
            if (!(nominalPackingSizeM > 0)) missing.Add(nameof(nominalPackingSizeM));
            if (!(criticalSurfaceTensionNM > 0)) missing.Add(nameof(criticalSurfaceTensionNM));
            if (!(liquidSurfaceTensionNM > 0)) missing.Add(nameof(liquidSurfaceTensionNM));
            if (!(liquidMassVelocityKgM2S > 0)) missing.Add(nameof(liquidMassVelocityKgM2S));
            if (!(gasMassVelocityKgM2S > 0)) missing.Add(nameof(gasMassVelocityKgM2S));
            if (missing.Count > 0)
                throw new IncompletePackingInputException(missing);

            return CalculateCore(
                packingSpecificAreaM2M3,
                nominalPackingSizeM,
                criticalSurfaceTensionNM,
                liquidMassVelocityKgM2S,
                gasMassVelocityKgM2S,
                new GasPhaseProperties(temperatureK, gasDensityKgM3, gasViscosityPas, gasDiffusivityM2S),
                new LiquidPhaseProperties(temperatureK, liquidDensityKgM3, liquidViscosityPas,
                                          liquidDiffusivityM2S, liquidSurfaceTensionNM));
        }

        private static OndaResult CalculateCore(
            double aT,
            double dp,
            double sigmaC,
            double L,
            double G,
            GasPhaseProperties gas,
            LiquidPhaseProperties liquid)
        {
            Require("GasTemperatureK", gas.TemperatureK, 250.0, 1500.0);
            Require("GasDensityKgM3", gas.DensityKgM3, 0.01, 50.0);
            Require("GasViscosityPas", gas.ViscosityPas, 1e-6, 1e-3);
            Require("GasDiffusivityM2S", gas.DiffusivityM2S, 1e-7, 1e-3);

            Require("LiquidTemperatureK", liquid.TemperatureK, 263.15, 393.15);
            Require("LiquidDensityKgM3", liquid.DensityKgM3, 500.0, 2500.0);
            Require("LiquidViscosityPas", liquid.ViscosityPas, 1e-4, 1.0);
            Require("LiquidDiffusivityM2S", liquid.DiffusivityM2S, 1e-11, 1e-7);
            Require("LiquidSurfaceTensionNM", liquid.SurfaceTensionNM, 0.01, 0.2);

            double muL = liquid.ViscosityPas;
            double rhoL = liquid.DensityKgM3;
            double sigmaL = liquid.SurfaceTensionNM;
            double muG = gas.ViscosityPas;
            double rhoG = gas.DensityKgM3;

            // Wetted area: liquid properties at liquid temperature
            double sigmaRatio = Math.Pow(sigmaC / sigmaL, 0.75);
            double reL = L / (aT * muL);
            double frL = (L * L * aT) / (rhoL * rhoL * GravityAccel);
            double weL = (L * L) / (rhoL * sigmaL * aT);

            double exponent = -1.45 * sigmaRatio
                * Math.Pow(reL, 0.1)
                * Math.Pow(frL, -0.05)
                * Math.Pow(weL, 0.2);

            double aWOverAt = 1.0 - Math.Exp(exponent);
            if (!(aWOverAt >= 1e-3) || aWOverAt > 1.0)
                throw new PropertyOutOfBoundsException("WettedAreaRatio", aWOverAt, 1e-3, 1.0);

            double aW = aWOverAt * aT;

            // Liquid film coefficient kL [m/s]: liquid properties at liquid temperature
            double reLForKl = L / (aW * muL);
            double scL = muL / (rhoL * liquid.DiffusivityM2S);
            double gravityTerm = Math.Pow(muL * GravityAccel / rhoL, 1.0 / 3.0);

            double kL = 0.0051
                * Math.Pow(reLForKl, 2.0 / 3.0)
                * Math.Pow(scL, -0.5)
                * Math.Pow(aT * dp, 0.4)
                * gravityTerm;

            // Gas film coefficient kG [kmol/(m2*s*Pa)]: gas properties at gas temperature
            double c = dp >= 0.015 ? 5.23 : 2.0;
            double reG = G / (aT * muG);
            double scG = muG / (rhoG * gas.DiffusivityM2S);

            double kG = c
                * Math.Pow(reG, 0.7)
                * Math.Pow(scG, 1.0 / 3.0)
                * Math.Pow(aT * dp, -2.0)
                * (aT * gas.DiffusivityM2S) / (RPaM3KmolK * gas.TemperatureK);

            if (double.IsNaN(kG) || double.IsInfinity(kG) || kG <= 0 ||
                double.IsNaN(kL) || double.IsInfinity(kL) || kL <= 0)
                throw new PropertyOutOfBoundsException("FilmCoefficient", double.NaN, 0.0, double.MaxValue);

            return new OndaResult
            {
                WettedAreaM2M3 = aW,
                GasFilmCoeffKmolM2SPa = kG,
                LiquidFilmCoeffMS = kL
            };
        }

        private static void Require(string name, double value, double min, double max)
        {
            if (double.IsNaN(value) || value < min || value > max)
                throw new PropertyOutOfBoundsException(name, value, min, max);
        }
    }
}