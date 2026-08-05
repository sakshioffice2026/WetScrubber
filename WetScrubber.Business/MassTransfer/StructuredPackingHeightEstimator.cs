using System;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Converts an absorption-factor requirement into an equivalent number of
    /// theoretical stages using the Kremser equation, then applies a vendor
    /// HETP. It reports infeasibility instead of returning a finite height
    /// when the requested removal cannot be achieved at the selected factor.
    /// </summary>
    public static class StructuredPackingHeightEstimator
    {
        public static StructuredPackingSizing Calculate(
            double absorptionFactor,
            double inletGasMoleFraction,
            double outletGasMoleFraction,
            double hetpM)
        {
            if (absorptionFactor <= 0) throw new ArgumentOutOfRangeException(nameof(absorptionFactor));
            if (inletGasMoleFraction <= 0 || outletGasMoleFraction <= 0 || outletGasMoleFraction >= inletGasMoleFraction)
                throw new ArgumentOutOfRangeException(nameof(outletGasMoleFraction));
            if (hetpM <= 0) throw new ArgumentOutOfRangeException(nameof(hetpM));

            double ratio = outletGasMoleFraction / inletGasMoleFraction;
            double stages;
            if (Math.Abs(absorptionFactor - 1.0) < 1e-8)
            {
                stages = 1.0 / ratio - 1.0;
            }
            else
            {
                double argument = 1.0 + (absorptionFactor - 1.0) / ratio;
                if (argument <= 0) return new StructuredPackingSizing { IsFeasible = false };
                stages = Math.Log(argument) / Math.Log(absorptionFactor) - 1.0;
            }

            if (double.IsNaN(stages) || double.IsInfinity(stages) || stages < 0)
                return new StructuredPackingSizing { IsFeasible = false };

            return new StructuredPackingSizing
            {
                IsFeasible = true,
                TheoreticalStages = stages,
                PackingHeightM = stages * hetpM
            };
        }
    }

    public sealed class StructuredPackingSizing
    {
        public bool IsFeasible { get; init; }
        public double TheoreticalStages { get; init; }
        public double PackingHeightM { get; init; }
    }
}
