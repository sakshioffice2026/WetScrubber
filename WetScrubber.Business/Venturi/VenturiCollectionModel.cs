using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Venturi
{
    public sealed class VenturiCollectionResult
    {
        public double DropletDiameterM { get; set; }
        public double CunninghamFactor { get; set; }
        public double ImpactionParameter { get; set; }
        public double CollectionEfficiencyPercent { get; set; }
        public double PressureDropPa { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Venturi particulate collection.
    /// Efficiency: Johnstone (1954)  eta = 1 - exp(-k * (QL/QG) * sqrt(psi)),
    ///   psi = C * rho_p * v_t * dp^2 / (9 * mu_g * d_d)   (inertial impaction parameter).
    ///   k = 0.15 per (gal/1000 acf) = 1.12 per (L/m3) in SI.
    /// Droplet size: Nukiyama-Tanasawa (SI form, Perry's).
    /// Pressure drop: Calvert (1972)  dP[cmH2O] = 8.24e-4 * v_t[cm/s]^2 * (QL/QG).
    /// Applies to particulate only. Not valid for gaseous pollutants.
    /// </summary>
    public static class VenturiCollectionModel
    {
        public const double JohnstoneKPerLitrePerM3 = 1.12;
        private const double MeanFreePathAirM = 0.066e-6;
        private const double CmH2OToPa = 98.0665;

        public static VenturiCollectionResult Calculate(
            double throatVelocityMs,
            double liquidToGasRatioLM3,
            double particleDiameterM,
            double particleDensityKgM3,
            double gasViscosityPas,
            double liquidDensityKgM3 = 1000.0,
            double liquidViscosityPas = 1.0e-3,
            double surfaceTensionNm = 0.072,
            double johnstoneK = JohnstoneKPerLitrePerM3)
        {
            RequirePositive(nameof(throatVelocityMs), throatVelocityMs);
            RequirePositive(nameof(liquidToGasRatioLM3), liquidToGasRatioLM3);
            RequirePositive(nameof(particleDiameterM), particleDiameterM);
            RequirePositive(nameof(particleDensityKgM3), particleDensityKgM3);
            RequirePositive(nameof(gasViscosityPas), gasViscosityPas);
            RequirePositive(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositive(nameof(liquidViscosityPas), liquidViscosityPas);
            RequirePositive(nameof(surfaceTensionNm), surfaceTensionNm);
            RequirePositive(nameof(johnstoneK), johnstoneK);

            var result = new VenturiCollectionResult();

            if (throatVelocityMs < 60.0 || throatVelocityMs > 120.0)
                result.Warnings.Add("Throat velocity outside the usual 60-120 m/s venturi range.");
            if (liquidToGasRatioLM3 < 0.67 || liquidToGasRatioLM3 > 2.7)
                result.Warnings.Add("L/G outside the usual 0.67-2.7 L/m3 range for Johnstone correlation.");

            double dropletM = NukiyamaTanasawaDiameterM(
                throatVelocityMs,
                liquidToGasRatioLM3,
                liquidDensityKgM3,
                liquidViscosityPas,
                surfaceTensionNm);

            double cunningham = CunninghamSlip(particleDiameterM);

            double psi =
                cunningham *
                particleDensityKgM3 *
                throatVelocityMs *
                particleDiameterM *
                particleDiameterM /
                (9.0 * gasViscosityPas * dropletM);

            double eta =
                1.0 - Math.Exp(-johnstoneK * liquidToGasRatioLM3 * Math.Sqrt(psi));

            double qlOverQg = liquidToGasRatioLM3 / 1000.0;
            double vtCmS = throatVelocityMs * 100.0;
            double dpCmH2O = 8.24e-4 * vtCmS * vtCmS * qlOverQg;

            result.DropletDiameterM = dropletM;
            result.CunninghamFactor = cunningham;
            result.ImpactionParameter = psi;
            result.CollectionEfficiencyPercent = eta * 100.0;
            result.PressureDropPa = dpCmH2O * CmH2OToPa;

            return result;
        }

        public static double NukiyamaTanasawaDiameterM(
            double relativeVelocityMs,
            double liquidToGasRatioLM3,
            double liquidDensityKgM3,
            double liquidViscosityPas,
            double surfaceTensionNm)
        {
            RequirePositive(nameof(relativeVelocityMs), relativeVelocityMs);
            RequirePositive(nameof(liquidToGasRatioLM3), liquidToGasRatioLM3);
            RequirePositive(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositive(nameof(liquidViscosityPas), liquidViscosityPas);
            RequirePositive(nameof(surfaceTensionNm), surfaceTensionNm);

            double term1 =
                0.585 / relativeVelocityMs *
                Math.Sqrt(surfaceTensionNm / liquidDensityKgM3);

            double term2 =
                1.683e-3 *
                Math.Pow(
                    liquidViscosityPas / Math.Sqrt(surfaceTensionNm * liquidDensityKgM3),
                    0.45) *
                Math.Pow(liquidToGasRatioLM3, 1.5);

            return term1 + term2;
        }

        public static double CunninghamSlip(double particleDiameterM)
        {
            RequirePositive(nameof(particleDiameterM), particleDiameterM);

            double kn2 = 2.0 * MeanFreePathAirM / particleDiameterM;
            return 1.0 + kn2 * (1.257 + 0.4 * Math.Exp(-0.55 * particleDiameterM / MeanFreePathAirM));
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        }
    }
}
