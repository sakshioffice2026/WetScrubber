using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Henry's constant unit handling.
    ///   H_cc : dimensionless Cg/Cl (gas conc. / liquid conc.)
    ///   H_yx : mole-fraction ratio y*/x (used by NTU/HTU and L/G solvers)
    ///   H_yx = H_cc * C_L * R * T / P      (C_L = molar concentration of water)
    ///   H_cc = H_yx * P / (C_L * R * T)
    /// </summary>
    public static class HenrysConstantUnits
    {
        // Bounds for H_yx (mole-fraction ratio). Do NOT apply to H_cc.
        public const double MinimumH = 0.001;
        public const double MaximumH = 1.0e5;

        // Bounds for H_cc (dimensionless Cg/Cl). Very soluble gases such as
        // HCl legitimately sit far below 0.001.
        public const double MinimumHcc = 1.0e-12;
        public const double MaximumHcc = 1.0e12;

        private const double GasConstantJmolK = 8.314462;      // J/(mol*K)
        private const double WaterMolarConcMolM3 = 55555.0;    // mol/m3 (1000 kg/m3 / 18.015 g/mol)

        /// <summary>Clamp for H_yx values.</summary>
        public static double Clamp(double h)
        {
            if (double.IsNaN(h) || double.IsInfinity(h)) return MinimumH;
            return Math.Min(Math.Max(h, MinimumH), MaximumH);
        }

        /// <summary>Clamp for H_cc (Cg/Cl) values.</summary>
        public static double ClampCgOverCl(double h)
        {
            if (double.IsNaN(h) || double.IsInfinity(h)) return MinimumHcc;
            return Math.Min(Math.Max(h, MinimumHcc), MaximumHcc);
        }

        public static double CgOverClToMoleFractionRatio(double hCgCl, double tempK, double pressureKPa)
        {
            double pressurePa = Math.Max(pressureKPa, 1e-6) * 1000.0;
            return hCgCl * WaterMolarConcMolM3 * GasConstantJmolK * Math.Max(tempK, 1e-6) / pressurePa;
        }

        public static double MoleFractionRatioToCgOverCl(double hYX, double tempK, double pressureKPa)
        {
            double pressurePa = Math.Max(pressureKPa, 1e-6) * 1000.0;
            double denom = WaterMolarConcMolM3 * GasConstantJmolK * Math.Max(tempK, 1e-6);
            return hYX * pressurePa / denom;
        }
    }
}