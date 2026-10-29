// PATH IN REPO: WetScrubber.Business/Thermodynamics/AcidBaseSpeciation.cs  (new file)
using System;

namespace WetScrubber.Business.Thermodynamics
{
    public static class AcidBaseSpeciation
    {
        // Literature constants, ideal solution (gamma = 1). VERIFY before design use.
        public const double PKa1SulfurousAcid = 1.86;
        public const double PKa2SulfurousAcid = 7.2;
        public const double PKa1CarbonicAcid = 6.35;
        public const double PKa2CarbonicAcid = 10.33;
        public const double PKw = 14.0;

        /// <summary>
        /// Base equivalents consumed per mole of a diprotic acid H2A absorbed
        /// (alpha_HA- + 2 * alpha_A2-), valid over the full pH range.
        /// </summary>
        public static double DiproticBaseEquivalentsPerMol(double pH, double pKa1, double pKa2)
        {
            if (double.IsNaN(pH) || pH < 0.0 || pH > 14.0)
                throw new ArgumentOutOfRangeException(nameof(pH), pH, "pH must be in 0-14.");

            double h = Math.Pow(10.0, -pH);
            double k1 = Math.Pow(10.0, -pKa1);
            double k2 = Math.Pow(10.0, -pKa2);
            double d = h * h + k1 * h + k1 * k2;
            return (k1 * h + 2.0 * k1 * k2) / d;
        }

        /// <summary>
        /// Charge-balance pH (ideal solution). naMolPerL, sIV, cIV, sVI in mol/L.
        /// sIV = total S(IV), cIV = total C(IV), sVI = sulfate.
        /// Returns NaN if the solution lies outside the ideal 0-14 bracket.
        /// </summary>
        public static double SolvePh(
            double naMolPerL, double sIV, double cIV, double sVI,
            double pKa1S = PKa1SulfurousAcid, double pKa2S = PKa2SulfurousAcid,
            double pKa1C = PKa1CarbonicAcid, double pKa2C = PKa2CarbonicAcid,
            double pKw = PKw)
        {
            double k1s = Math.Pow(10.0, -pKa1S), k2s = Math.Pow(10.0, -pKa2S);
            double k1c = Math.Pow(10.0, -pKa1C), k2c = Math.Pow(10.0, -pKa2C);

            double Residual(double pH)
            {
                double h = Math.Pow(10.0, -pH);
                double oh = Math.Pow(10.0, pH - pKw);
                double dS = h * h + k1s * h + k1s * k2s;
                double dC = h * h + k1c * h + k1c * k2c;
                double anS = sIV * (k1s * h + 2.0 * k1s * k2s) / dS;
                double anC = cIV * (k1c * h + 2.0 * k1c * k2c) / dC;
                return naMolPerL + h - oh - anS - anC - 2.0 * sVI;
            }

            double lo = 0.0, hi = 14.0;
            if (Residual(lo) <= 0.0 || Residual(hi) >= 0.0) return double.NaN;

            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Residual(mid) > 0.0) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }
    }
}
