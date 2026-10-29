using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Henry's constant unit handling. No silent clamping: invalid input throws.
    ///   H_cc : dimensionless Cg/Cl
    ///   H_yx : mole-fraction ratio y*/x
    ///   H_yx = H_cc * C_L * R * T / P
    /// MinimumH is the reactive-equilibrium sentinel compared by equality in the engine.
    /// </summary>
    public static class HenrysConstantUnits
    {
        public const double MinimumH = 0.001;
        public const double MaximumH = 1.0e5;
        public const double MinimumPhysicalH = 1.0e-12;
        public const double MinimumHcc = 1.0e-12;
        public const double MaximumHcc = 1.0e12;

        private const double GasConstantJmolK = 8.314462;
        private const double WaterMolarMassKgKmol = 18.01528;

        /// <summary>Molar concentration of liquid water (mol/m3), Kell (1975) density, 0-150 C.</summary>
        public static double WaterMolarConcMolM3At(double tempK)
        {
            if (double.IsNaN(tempK) || tempK < 273.15 || tempK > 423.15)
                throw new ArgumentOutOfRangeException(
                    nameof(tempK), tempK, "Water density correlation is valid for 273.15-423.15 K.");

            double t = tempK - 273.15;
            double rho = (999.83952
                          + 16.945176 * t
                          - 7.9870401e-3 * t * t
                          - 46.170461e-6 * t * t * t
                          + 105.56302e-9 * Math.Pow(t, 4)
                          - 280.54253e-12 * Math.Pow(t, 5))
                         / (1.0 + 16.879850e-3 * t);

            return rho / WaterMolarMassKgKmol * 1000.0;
        }

        private static void RequirePositiveFinite(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                throw new ArgumentOutOfRangeException(name, value, name + " must be positive and finite.");
        }

        public static double CgOverClToMoleFractionRatio(
            double hCgCl,
            double tempK,
            double pressureKPa,
            double? solventMolarConcMolM3 = null)
        {
            RequirePositiveFinite(nameof(hCgCl), hCgCl);
            RequirePositiveFinite(nameof(tempK), tempK);
            RequirePositiveFinite(nameof(pressureKPa), pressureKPa);

            double cL = solventMolarConcMolM3 ?? WaterMolarConcMolM3At(tempK);
            RequirePositiveFinite(nameof(solventMolarConcMolM3), cL);

            return hCgCl * cL * GasConstantJmolK * tempK / (pressureKPa * 1000.0);
        }

        public static double MoleFractionRatioToCgOverCl(
            double hYX,
            double tempK,
            double pressureKPa,
            double? solventMolarConcMolM3 = null)
        {
            RequirePositiveFinite(nameof(hYX), hYX);
            RequirePositiveFinite(nameof(tempK), tempK);
            RequirePositiveFinite(nameof(pressureKPa), pressureKPa);

            double cL = solventMolarConcMolM3 ?? WaterMolarConcMolM3At(tempK);
            RequirePositiveFinite(nameof(solventMolarConcMolM3), cL);

            return hYX * pressureKPa * 1000.0 / (cL * GasConstantJmolK * tempK);
        }
    }
}