using System;
using System.Collections.Generic;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Fuller-Schettler-Giddings (1966) estimation of the binary
    /// gas-phase diffusion coefficient:
    ///   D_AB [cm2/s] = 0.00143*T^1.75 / (P[bar]*MAB^0.5*(SumV_A^(1/3)+SumV_B^(1/3))^2)
    ///   MAB = 2 / (1/M_A + 1/M_B)
    /// DiffusionVolumes below are Fuller's published group-contribution
    /// values (Poling, Prausnitz &amp; O'Connell, "Properties of Gases
    /// and Liquids", 5th ed.) for Air/H2O/NH3/SO2; HCl/H2S/Cl2 are
    /// estimated from atomic increments and NOT independently
    /// cross-checked — flagged the same way Phase 0/1 unvalidated
    /// constants are.
    /// </summary>
    public static class FullerGasDiffusivity
    {
        private static readonly Dictionary<string, double> DiffusionVolumes = new()
        {
            ["Air"] = 19.7,
            ["H2O"] = 13.1,
            ["NH3"] = 14.9,
            ["SO2"] = 41.1,
            ["HCl"] = 21.0,  // estimated: H(1.98) + Cl(19.5)
            ["H2S"] = 20.96, // estimated: 2*H(1.98) + S(17.0)
            ["Cl2"] = 39.0   // estimated: 2*Cl(19.5)
        };

        public static bool TryGetDiffusionVolume(string code, out double volume)
            => DiffusionVolumes.TryGetValue(code, out volume);

        public static double Calculate(
            string codeA, double molecularWeightA,
            string codeB, double molecularWeightB,
            double temperatureK, double pressureKPa)
        {
            if (!TryGetDiffusionVolume(codeA, out var vA))
                throw new InvalidOperationException($"No Fuller diffusion volume for '{codeA}'.");
            if (!TryGetDiffusionVolume(codeB, out var vB))
                throw new InvalidOperationException($"No Fuller diffusion volume for '{codeB}'.");

            double mAB = 2.0 / (1.0 / molecularWeightA + 1.0 / molecularWeightB);
            double pBar = pressureKPa / 100.0;

            double dAbCm2S = 0.00143 * Math.Pow(temperatureK, 1.75)
                / (pBar * Math.Sqrt(mAB) * Math.Pow(Math.Pow(vA, 1.0 / 3.0) + Math.Pow(vB, 1.0 / 3.0), 2));

            return dAbCm2S * 1e-4; // cm2/s -> m2/s
        }
    }
}