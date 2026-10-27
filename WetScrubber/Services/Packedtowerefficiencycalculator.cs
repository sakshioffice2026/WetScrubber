using System;

namespace WetScrubber.Services
{
    /// <summary>
    /// Removal efficiency of a counter-current packed tower at a given packing
    /// height, from the Colburn relation for a linear equilibrium line y* = m·x:
    ///
    ///   N_OG = Z / H_OG
    ///   (y1 - m·x2) / (y2 - m·x2) = [ exp(N_OG·(1 - 1/A)) - 1/A ] / (1 - 1/A)
    ///   A = L / (m·G)
    ///
    /// With s = m·x2 / y1 (equilibrium gas fraction of the inlet liquid, 0 for
    /// fresh solvent):
    ///
    ///   y2 / y1 = s + (1 - s) / ratio
    ///   efficiency = (1 - s) · (1 - 1/ratio)
    ///
    /// The A -> 1 limit (ratio = 1 + N_OG) is used only when |A - 1| &lt;= 1e-6,
    /// matching CalculateNtuHtuRateBased. There are no efficiency caps and no
    /// forcing of intermediate values: invalid input throws. For A &lt; 1 the
    /// result tends to A·(1 - s)·100 % as the height grows, which is the
    /// physical limit.
    /// </summary>
    public static class PackedTowerEfficiencyCalculator
    {
        private const double AbsorptionFactorUnityTolerance = 1e-6;

        /// <param name="packingHeightM">Packed height Z (m). Zero returns 0 %.</param>
        /// <param name="htuM">Overall gas-phase transfer unit height H_OG (m), &gt; 0.</param>
        /// <param name="absorptionFactor">A = L / (m·G), &gt; 0.</param>
        /// <param name="equilibriumFractionOfInletLiquid">
        /// s = m·x2 / y1 in [0, 1). Leave at 0 for fresh (solute-free) solvent.
        /// </param>
        /// <returns>Removal efficiency in percent.</returns>
        public static double AtHeight(
            double packingHeightM,
            double htuM,
            double absorptionFactor,
            double equilibriumFractionOfInletLiquid = 0.0)
        {
            if (double.IsNaN(packingHeightM) || double.IsInfinity(packingHeightM) || packingHeightM < 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(packingHeightM), packingHeightM,
                    "Packing height must be finite and non-negative.");

            if (!(htuM > 0.0) || double.IsInfinity(htuM))
                throw new ArgumentOutOfRangeException(
                    nameof(htuM), htuM,
                    "HTU must be positive and finite.");

            if (!(absorptionFactor > 0.0) || double.IsInfinity(absorptionFactor))
                throw new ArgumentOutOfRangeException(
                    nameof(absorptionFactor), absorptionFactor,
                    "Absorption factor must be positive and finite.");

            if (double.IsNaN(equilibriumFractionOfInletLiquid) ||
                equilibriumFractionOfInletLiquid < 0.0 ||
                equilibriumFractionOfInletLiquid >= 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(equilibriumFractionOfInletLiquid), equilibriumFractionOfInletLiquid,
                    "Equilibrium fraction of the inlet liquid (m·x2/y1) must be in [0, 1).");

            if (packingHeightM == 0.0)
                return 0.0;

            double ntu = packingHeightM / htuM;
            if (double.IsNaN(ntu) || double.IsInfinity(ntu))
                throw new ArgumentException(
                    "NTU (packing height / HTU) is not finite.", nameof(packingHeightM));

            double a = absorptionFactor;
            double invRatio; // y_excess_out / y_excess_in = 1 / ratio

            if (Math.Abs(a - 1.0) <= AbsorptionFactorUnityTolerance)
            {
                // Limit of the Colburn expression as A -> 1.
                invRatio = 1.0 / (1.0 + ntu);
            }
            else
            {
                double oneMinusInvA = 1.0 - 1.0 / a;
                double e = ntu * oneMinusInvA;

                if (e > 0.0)
                {
                    // A > 1: rewrite with exp(-e) so large NTU cannot overflow.
                    //   1/ratio = (1 - 1/A) / (exp(e) - 1/A)
                    //           = (1 - 1/A)·exp(-e) / (1 - exp(-e)/A)
                    double decay = Math.Exp(-e);
                    invRatio = oneMinusInvA * decay / (1.0 - decay / a);
                }
                else
                {
                    // A < 1: e <= 0, exp(e) is in (0, 1], no overflow.
                    double ratio = (Math.Exp(e) - 1.0 / a) / oneMinusInvA;
                    invRatio = 1.0 / ratio;
                }
            }

            double efficiency =
                (1.0 - equilibriumFractionOfInletLiquid) *
                (1.0 - invRatio) *
                100.0;

            if (double.IsNaN(efficiency) || double.IsInfinity(efficiency))
                throw new InvalidOperationException(
                    "Removal efficiency evaluation produced a non-finite value.");

            return efficiency;
        }
    }
}