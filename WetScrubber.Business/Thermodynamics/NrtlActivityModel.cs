using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Non-Random Two-Liquid (NRTL) model, binary form (Renon &amp;
    /// Prausnitz, 1968):
    ///
    ///   ln(gammaA) = xB^2 * [ tauBA*(GBA/(xA+xB*GBA))^2
    ///                       + tauAB*GAB/(xB+xA*GAB)^2 ]
    ///   ln(gammaB) = xA^2 * [ tauAB*(GAB/(xB+xA*GAB))^2
    ///                       + tauBA*GBA/(xA+xB*GBA)^2 ]
    ///   where GAB = exp(-alpha*tauAB), GBA = exp(-alpha*tauBA)
    ///
    /// Tau values are treated as temperature-independent constants
    /// within NrtlBinaryParameter.ValidTempMinK/MaxK — the schema
    /// doesn't carry the a+b/T temperature-dependent form some
    /// databanks use, matching what's actually sourced today (see
    /// NrtlBinaryParameter.cs).
    /// </summary>
    public sealed class NrtlActivityModel : IActivityCoefficientModel
    {
        public ActivityCoefficientResult Evaluate(
            NrtlComponentInput componentA,
            NrtlComponentInput componentB,
            NrtlBinaryInput binaryParameters)
        {
            if (componentA == null) throw new ArgumentNullException(nameof(componentA));
            if (componentB == null) throw new ArgumentNullException(nameof(componentB));
            if (binaryParameters == null) throw new ArgumentNullException(nameof(binaryParameters));

            double xA = componentA.MoleFraction;
            double xB = componentB.MoleFraction;

            if (Math.Abs(xA + xB - 1.0) > 0.01)
                throw new ArgumentException(
                    $"NRTL mole fractions must sum to ~1.0 (got {xA + xB:F4}).");

            double tauAB = binaryParameters.Tau_AB;
            double tauBA = binaryParameters.Tau_BA;
            double alpha = binaryParameters.Alpha;

            double gAB = Math.Exp(-alpha * tauAB);
            double gBA = Math.Exp(-alpha * tauBA);

            double denomA = xB + xA * gAB;
            double denomB = xA + xB * gBA;

            double lnGammaA = xB * xB * (
                tauBA * Math.Pow(gBA / denomB, 2)
                + tauAB * gAB / (denomA * denomA));

            double lnGammaB = xA * xA * (
                tauAB * Math.Pow(gAB / denomA, 2)
                + tauBA * gBA / (denomB * denomB));

            return new ActivityCoefficientResult
            {
                GammaA = Math.Exp(lnGammaA),
                GammaB = Math.Exp(lnGammaB)
            };
        }
    }
}