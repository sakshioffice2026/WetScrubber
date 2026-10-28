using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Venturi
{
    public sealed class VenturiCollectionResult
    {
        public double DropletDiameterM { get; set; }
        public double CunninghamFactor { get; set; }
        public double MeanFreePathM { get; set; }
        public double ImpactionParameter { get; set; }
        public double GeometricStdDev { get; set; }
        public double CollectionEfficiencyPercent { get; set; }
        public double PressureDropPa { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Venturi particulate collection.
    /// Efficiency: Johnstone (1954)  eta = 1 - exp(-k * (QL/QG) * sqrt(psi)),
    ///   psi = C * rho_p * v_t * dp^2 / (ImpactionParameterDivisor * mu_g * d_d).
    ///   ImpactionParameterDivisor = 18 (Stokes-number form). The factor 9 versus 18
    ///   convention for k = 0.15 per (gal/1000 acf) is NOT confirmed against a primary source.
    ///   k = 0.15 per (gal/1000 acf) = 1.12 per (L/m3) in SI.
    /// Size distribution: when geometricStdDev &gt; 1 the particle mass distribution is taken
    ///   as log-normal with the supplied mass-median diameter, and the overall efficiency is the
    ///   mass-weighted integral of the single-size efficiency.
    /// Cunningham slip: mean free path from gas viscosity, temperature and pressure.
    /// Droplet size: Nukiyama-Tanasawa (SI form, Perry's).
    /// Pressure drop: Calvert (1972)  dP[cmH2O] = 8.24e-4 * v_t[cm/s]^2 * (QL/QG).
    ///   The Calvert constant and the QL/QG unit basis are NOT confirmed against a primary source.
    /// Applies to particulate only. Not valid for gaseous pollutants.
    /// </summary>
    public static class VenturiCollectionModel
    {
        public const double JohnstoneKPerLitrePerM3 = 1.12;
        public const double ImpactionParameterDivisor = 18.0;
        public const double CalvertCoefficient = 8.24e-4;
        public const double DefaultTemperatureK = 293.15;
        public const double DefaultPressurePa = 101325.0;

        private const double DefaultMeanFreePathM = 0.066e-6;
        private const double CmH2OToPa = 98.0665;
        private const double AirMolarMassKgMol = 0.028964;
        private const double GasConstantJmolK = 8.314462;
        private const int QuadraturePoints = 81;
        private const double QuadratureHalfRangeSigma = 4.0;

        public static VenturiCollectionResult Calculate(
            double throatVelocityMs,
            double liquidToGasRatioLM3,
            double particleDiameterM,
            double particleDensityKgM3,
            double gasViscosityPas,
            double liquidDensityKgM3 = 1000.0,
            double liquidViscosityPas = 1.0e-3,
            double surfaceTensionNm = 0.072,
            double johnstoneK = JohnstoneKPerLitrePerM3,
            double gasTemperatureK = DefaultTemperatureK,
            double gasPressurePa = DefaultPressurePa,
            double geometricStdDev = 1.0)
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
            RequirePositive(nameof(gasTemperatureK), gasTemperatureK);
            RequirePositive(nameof(gasPressurePa), gasPressurePa);

            if (!double.IsFinite(geometricStdDev) || geometricStdDev < 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(geometricStdDev), geometricStdDev,
                    "Geometric standard deviation must be finite and at least 1.0.");

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

            double meanFreePath = MeanFreePathM(gasViscosityPas, gasTemperatureK, gasPressurePa);

            double EtaAt(double dpM)
            {
                double c = CunninghamSlip(dpM, meanFreePath);
                double psiAt = ImpactionParameter(
                    c, particleDensityKgM3, throatVelocityMs, dpM, gasViscosityPas, dropletM);
                return 1.0 - Math.Exp(-johnstoneK * liquidToGasRatioLM3 * Math.Sqrt(psiAt));
            }

            double eta = geometricStdDev <= 1.0 + 1e-9
                ? EtaAt(particleDiameterM)
                : IntegrateLogNormal(particleDiameterM, geometricStdDev, EtaAt);

            double cunningham = CunninghamSlip(particleDiameterM, meanFreePath);
            double psi = ImpactionParameter(
                cunningham, particleDensityKgM3, throatVelocityMs,
                particleDiameterM, gasViscosityPas, dropletM);

            double qlOverQg = liquidToGasRatioLM3 / 1000.0;
            double vtCmS = throatVelocityMs * 100.0;
            double dpCmH2O = CalvertCoefficient * vtCmS * vtCmS * qlOverQg;

            result.DropletDiameterM = dropletM;
            result.CunninghamFactor = cunningham;
            result.MeanFreePathM = meanFreePath;
            result.ImpactionParameter = psi;
            result.GeometricStdDev = geometricStdDev;
            result.CollectionEfficiencyPercent = eta * 100.0;
            result.PressureDropPa = dpCmH2O * CmH2OToPa;

            return result;
        }

        public static double ImpactionParameter(
            double cunninghamFactor,
            double particleDensityKgM3,
            double throatVelocityMs,
            double particleDiameterM,
            double gasViscosityPas,
            double dropletDiameterM)
        {
            RequirePositive(nameof(cunninghamFactor), cunninghamFactor);
            RequirePositive(nameof(particleDensityKgM3), particleDensityKgM3);
            RequirePositive(nameof(throatVelocityMs), throatVelocityMs);
            RequirePositive(nameof(particleDiameterM), particleDiameterM);
            RequirePositive(nameof(gasViscosityPas), gasViscosityPas);
            RequirePositive(nameof(dropletDiameterM), dropletDiameterM);

            return cunninghamFactor *
                   particleDensityKgM3 *
                   throatVelocityMs *
                   particleDiameterM *
                   particleDiameterM /
                   (ImpactionParameterDivisor * gasViscosityPas * dropletDiameterM);
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

        /// <summary>
        /// Gas mean free path from kinetic theory for air:
        /// lambda = mu / (0.499 * P * sqrt(8 M / (pi R T))).
        /// </summary>
        public static double MeanFreePathM(
            double gasViscosityPas,
            double temperatureK,
            double pressurePa)
        {
            RequirePositive(nameof(gasViscosityPas), gasViscosityPas);
            RequirePositive(nameof(temperatureK), temperatureK);
            RequirePositive(nameof(pressurePa), pressurePa);

            double meanSpeedFactor = Math.Sqrt(
                8.0 * AirMolarMassKgMol / (Math.PI * GasConstantJmolK * temperatureK));

            return gasViscosityPas / (0.499 * pressurePa * meanSpeedFactor);
        }

        public static double CunninghamSlip(double particleDiameterM)
            => CunninghamSlip(particleDiameterM, DefaultMeanFreePathM);

        public static double CunninghamSlip(double particleDiameterM, double meanFreePathM)
        {
            RequirePositive(nameof(particleDiameterM), particleDiameterM);
            RequirePositive(nameof(meanFreePathM), meanFreePathM);

            double kn2 = 2.0 * meanFreePathM / particleDiameterM;
            return 1.0 + kn2 * (1.257 + 0.4 * Math.Exp(-0.55 * particleDiameterM / meanFreePathM));
        }

        private static double IntegrateLogNormal(
            double medianDiameterM,
            double geometricStdDev,
            Func<double, double> efficiencyAt)
        {
            double lnSigma = Math.Log(geometricStdDev);
            double step = 2.0 * QuadratureHalfRangeSigma / (QuadraturePoints - 1);

            double weightSum = 0.0;
            double weightedEta = 0.0;

            for (int i = 0; i < QuadraturePoints; i++)
            {
                double z = -QuadratureHalfRangeSigma + i * step;
                double weight = Math.Exp(-0.5 * z * z);
                double dpM = medianDiameterM * Math.Exp(z * lnSigma);

                weightSum += weight;
                weightedEta += weight * efficiencyAt(dpM);
            }

            return weightedEta / weightSum;
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        }
    }
}