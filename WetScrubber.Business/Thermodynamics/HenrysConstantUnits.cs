using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Henry's constant unit handling.
    /// H_cc : dimensionless Cg/Cl (gas conc. / liquid conc.)
    /// H_yx : mole-fraction ratio y*/x (used by NTU/HTU and L/G solvers)
    /// H_yx = H_cc * C_L * R * T / P   (C_L = molar concentration of water)
    /// </summary>
    public static class HenrysConstantUnits
    {
        public const double MinimumH = 0.001;
        public const double MaximumH = 1.0e5;

        private const double GasConstantJmolK = 8.314462;      // J/(mol*K)
        private const double WaterMolarConcMolM3 = 55555.0;    // mol/m3 (1000 kg/m3 / 18.015 g/mol)

        public static double Clamp(double h)
        {
            if (double.IsNaN(h) || double.IsInfinity(h)) return MinimumH;
            return Math.Min(Math.Max(h, MinimumH), MaximumH);
        }

        public static double CgOverClToMoleFractionRatio(double hCgCl, double tempK, double pressureKPa)
        {
            double pressurePa = Math.Max(pressureKPa, 1e-6) * 1000.0;
            return hCgCl * WaterMolarConcMolM3 * GasConstantJmolK * tempK / pressurePa;
        }

        public static double MoleFractionRatioToCgOverCl(double hYX, double tempK, double pressureKPa)
        {
            double pressurePa = Math.Max(pressureKPa, 1e-6) * 1000.0;
            double denom = WaterMolarConcMolM3 * GasConstantJmolK * Math.Max(tempK, 1e-6);
            return hYX * pressurePa / denom;
        }
    }
}