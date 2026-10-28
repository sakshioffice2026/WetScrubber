using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Particulate
{
    /// <summary>
    /// One size bin: representative diameter (m) and mass fraction (0-1).
    /// </summary>
    public sealed class ParticleSizeBin
    {
        public double DiameterM { get; }
        public double MassFraction { get; }

        public ParticleSizeBin(double diameterM, double massFraction)
        {
            if (!(diameterM > 0) || double.IsInfinity(diameterM))
                throw new ArgumentOutOfRangeException(nameof(diameterM), "Diameter must be a positive finite number (m).");
            if (!(massFraction >= 0) || double.IsInfinity(massFraction))
                throw new ArgumentOutOfRangeException(nameof(massFraction), "Mass fraction must be a non-negative finite number.");

            DiameterM = diameterM;
            MassFraction = massFraction;
        }
    }

    /// <summary>
    /// Discrete particle size distribution (mass basis).
    /// Bins are stored sorted by ascending diameter; mass fractions are normalised to sum to 1.
    /// Source: standard discrete-bin representation used with fractional-efficiency integration
    /// (e.g. overall eta = sum(w_i * eta_i)). No empirical constants are used here.
    /// </summary>
    public sealed class ParticleSizeDistribution
    {
        private const double NormalisationTolerance = 1.0e-6;

        public IReadOnlyList<ParticleSizeBin> Bins { get; }

        /// <summary>
        /// True when the supplied fractions did not sum to 1 within tolerance and were rescaled.
        /// </summary>
        public bool WasRenormalised { get; }

        /// <summary>
        /// Sum of the fractions as supplied, before normalisation.
        /// </summary>
        public double SuppliedFractionSum { get; }

        public ParticleSizeDistribution(IEnumerable<ParticleSizeBin> bins)
        {
            if (bins == null) throw new ArgumentNullException(nameof(bins));

            var list = bins.Where(b => b != null).OrderBy(b => b.DiameterM).ToList();
            if (list.Count == 0)
                throw new ArgumentException("At least one size bin is required.", nameof(bins));

            double sum = list.Sum(b => b.MassFraction);
            if (!(sum > 0))
                throw new ArgumentException("Mass fractions must sum to a positive value.", nameof(bins));

            SuppliedFractionSum = sum;
            WasRenormalised = Math.Abs(sum - 1.0) > NormalisationTolerance;

            Bins = list
                .Select(b => new ParticleSizeBin(b.DiameterM, b.MassFraction / sum))
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Single-size (monodisperse) distribution.
        /// </summary>
        public static ParticleSizeDistribution Monodisperse(double diameterM)
            => new ParticleSizeDistribution(new[] { new ParticleSizeBin(diameterM, 1.0) });

        /// <summary>
        /// Builds a discrete distribution from a log-normal description.
        /// Mass median diameter (MMD) and geometric standard deviation (sigmaG >= 1).
        /// Bin weights are the mass-CDF differences between geometric bin edges,
        /// using the standard normal CDF of ln(d / MMD) / ln(sigmaG).
        /// </summary>
        public static ParticleSizeDistribution LogNormal(
            double massMedianDiameterM,
            double geometricStdDev,
            int binCount = 20,
            double spanSigmas = 3.0)
        {
            if (!(massMedianDiameterM > 0))
                throw new ArgumentOutOfRangeException(nameof(massMedianDiameterM), "MMD must be positive (m).");
            if (!(geometricStdDev >= 1.0))
                throw new ArgumentOutOfRangeException(nameof(geometricStdDev), "Geometric standard deviation must be >= 1.");
            if (binCount < 1)
                throw new ArgumentOutOfRangeException(nameof(binCount), "At least one bin is required.");
            if (!(spanSigmas > 0))
                throw new ArgumentOutOfRangeException(nameof(spanSigmas), "Span must be positive.");

            if (geometricStdDev - 1.0 < 1.0e-9 || binCount == 1)
                return Monodisperse(massMedianDiameterM);

            double lnSigma = Math.Log(geometricStdDev);
            double zLow = -spanSigmas;
            double zHigh = spanSigmas;
            double dz = (zHigh - zLow) / binCount;

            var bins = new List<ParticleSizeBin>(binCount);
            for (int i = 0; i < binCount; i++)
            {
                double z0 = zLow + i * dz;
                double z1 = z0 + dz;
                double zMid = 0.5 * (z0 + z1);

                double weight = NormalCdf(z1) - NormalCdf(z0);
                double diameter = massMedianDiameterM * Math.Exp(zMid * lnSigma);
                bins.Add(new ParticleSizeBin(diameter, weight));
            }

            return new ParticleSizeDistribution(bins);
        }

        /// <summary>
        /// Mass-weighted mean diameter (m).
        /// </summary>
        public double MassMeanDiameterM => Bins.Sum(b => b.MassFraction * b.DiameterM);

        public double MinDiameterM => Bins[0].DiameterM;
        public double MaxDiameterM => Bins[Bins.Count - 1].DiameterM;

        /// <summary>
        /// Overall value of a per-size quantity (typically efficiency, 0-1): sum(w_i * f(d_i)).
        /// </summary>
        public double MassWeightedAverage(Func<double, double> perSizeValue)
        {
            if (perSizeValue == null) throw new ArgumentNullException(nameof(perSizeValue));
            return Bins.Sum(b => b.MassFraction * perSizeValue(b.DiameterM));
        }

        /// <summary>
        /// Diameter (m) below which the given cumulative mass fraction (0-1) lies,
        /// by linear interpolation between bin diameters.
        /// </summary>
        public double DiameterAtCumulativeFraction(double cumulativeFraction)
        {
            if (!(cumulativeFraction >= 0 && cumulativeFraction <= 1))
                throw new ArgumentOutOfRangeException(nameof(cumulativeFraction), "Fraction must be within 0-1.");

            double cumulative = 0.0;
            for (int i = 0; i < Bins.Count; i++)
            {
                double next = cumulative + Bins[i].MassFraction;
                if (cumulativeFraction <= next)
                {
                    if (i == 0 || Bins[i].MassFraction <= 0)
                        return Bins[i].DiameterM;

                    double t = (cumulativeFraction - cumulative) / Bins[i].MassFraction;
                    return Bins[i - 1].DiameterM + t * (Bins[i].DiameterM - Bins[i - 1].DiameterM);
                }
                cumulative = next;
            }
            return MaxDiameterM;
        }

        // Standard normal CDF via the complementary error function.
        private static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2.0));

        // Complementary error function, Numerical Recipes erfc Chebyshev fit (fractional error < 1.2e-7).
        private static double Erfc(double x)
        {
            double z = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.5 * z);
            double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                       t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
                       t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? r : 2.0 - r;
        }
    }
}
