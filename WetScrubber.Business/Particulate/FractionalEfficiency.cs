using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Particulate
{
    /// <summary>
    /// One row of a fractional (grade) efficiency table.
    /// </summary>
    public sealed class FractionalEfficiencyPoint
    {
        public double DiameterM { get; set; }
        public double MassFraction { get; set; }
        public double Efficiency { get; set; }          // 0-1
        public double Penetration => 1.0 - Efficiency;  // 0-1
    }

    public sealed class FractionalEfficiencyResult
    {
        public List<FractionalEfficiencyPoint> Points { get; set; } = new();
        public double OverallEfficiency { get; set; }   // 0-1
        public double OverallPenetration => 1.0 - OverallEfficiency;
        public double OverallEfficiencyPercent => OverallEfficiency * 100.0;
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Fractional-to-overall efficiency and cut-diameter helpers.
    /// Overall efficiency: eta_overall = sum(w_i * eta_i) over mass-fraction bins.
    /// Cut diameter: diameter at which the fractional efficiency equals a target (default 0.5),
    ///   found by bisection on a log-diameter axis; the efficiency function must be non-decreasing in d.
    /// No empirical constants are used here.
    /// </summary>
    public static class FractionalEfficiency
    {
        private const int MaxBisectionIterations = 200;
        private const double DefaultRelativeTolerance = 1.0e-9;

        /// <summary>
        /// Evaluates a per-size efficiency function over a distribution and integrates to overall.
        /// Efficiency values are clamped to 0-1; a warning is added when clamping occurs.
        /// </summary>
        public static FractionalEfficiencyResult Evaluate(
            ParticleSizeDistribution distribution,
            Func<double, double> efficiencyAtDiameter)
        {
            if (distribution == null) throw new ArgumentNullException(nameof(distribution));
            if (efficiencyAtDiameter == null) throw new ArgumentNullException(nameof(efficiencyAtDiameter));

            var result = new FractionalEfficiencyResult();
            double overall = 0.0;
            bool clamped = false;

            foreach (var bin in distribution.Bins)
            {
                double eta = efficiencyAtDiameter(bin.DiameterM);

                if (double.IsNaN(eta) || double.IsInfinity(eta))
                    throw new InvalidOperationException(
                        $"Efficiency function returned a non-finite value at d = {bin.DiameterM:E3} m.");

                if (eta < 0.0) { eta = 0.0; clamped = true; }
                else if (eta > 1.0) { eta = 1.0; clamped = true; }

                result.Points.Add(new FractionalEfficiencyPoint
                {
                    DiameterM = bin.DiameterM,
                    MassFraction = bin.MassFraction,
                    Efficiency = eta
                });

                overall += bin.MassFraction * eta;
            }

            result.OverallEfficiency = Math.Min(1.0, Math.Max(0.0, overall));

            if (clamped)
                result.Warnings.Add("Fractional efficiency was clamped to the 0-1 range for one or more size bins.");
            if (distribution.WasRenormalised)
                result.Warnings.Add("Particle size mass fractions did not sum to 1 and were renormalised.");

            return result;
        }

        /// <summary>
        /// Outlet mass loading from inlet loading and overall efficiency (0-1).
        /// </summary>
        public static double OutletLoading(double inletLoading, double overallEfficiency)
        {
            if (!(inletLoading >= 0) || double.IsInfinity(inletLoading))
                throw new ArgumentOutOfRangeException(nameof(inletLoading), "Inlet loading must be non-negative and finite.");
            if (!(overallEfficiency >= 0 && overallEfficiency <= 1))
                throw new ArgumentOutOfRangeException(nameof(overallEfficiency), "Efficiency must be within 0-1.");

            return inletLoading * (1.0 - overallEfficiency);
        }

        /// <summary>
        /// Efficiency required of the collector to reach a target outlet loading.
        /// Returns 0 when the inlet loading is already at or below the target.
        /// </summary>
        public static double RequiredOverallEfficiency(double inletLoading, double targetOutletLoading)
        {
            if (!(inletLoading > 0) || double.IsInfinity(inletLoading))
                throw new ArgumentOutOfRangeException(nameof(inletLoading), "Inlet loading must be positive and finite.");
            if (!(targetOutletLoading >= 0) || double.IsInfinity(targetOutletLoading))
                throw new ArgumentOutOfRangeException(nameof(targetOutletLoading), "Target loading must be non-negative and finite.");

            if (targetOutletLoading >= inletLoading) return 0.0;
            return 1.0 - targetOutletLoading / inletLoading;
        }

        /// <summary>
        /// Finds the diameter (m) at which the fractional efficiency equals targetEfficiency (default 0.5).
        /// Returns null when the target is not bracketed inside [minDiameterM, maxDiameterM].
        /// </summary>
        public static double? FindCutDiameterM(
            Func<double, double> efficiencyAtDiameter,
            double targetEfficiency = 0.5,
            double minDiameterM = 1.0e-9,
            double maxDiameterM = 1.0e-2,
            double relativeTolerance = DefaultRelativeTolerance)
        {
            if (efficiencyAtDiameter == null) throw new ArgumentNullException(nameof(efficiencyAtDiameter));
            if (!(targetEfficiency > 0 && targetEfficiency < 1))
                throw new ArgumentOutOfRangeException(nameof(targetEfficiency), "Target must be strictly between 0 and 1.");
            if (!(minDiameterM > 0) || !(maxDiameterM > minDiameterM))
                throw new ArgumentOutOfRangeException(nameof(minDiameterM), "Diameter bounds must satisfy 0 < min < max.");
            if (!(relativeTolerance > 0))
                throw new ArgumentOutOfRangeException(nameof(relativeTolerance), "Tolerance must be positive.");

            double lo = Math.Log(minDiameterM);
            double hi = Math.Log(maxDiameterM);

            double fLo = efficiencyAtDiameter(Math.Exp(lo)) - targetEfficiency;
            double fHi = efficiencyAtDiameter(Math.Exp(hi)) - targetEfficiency;

            if (double.IsNaN(fLo) || double.IsNaN(fHi)) return null;
            if (fLo > 0 || fHi < 0) return null;

            for (int i = 0; i < MaxBisectionIterations; i++)
            {
                double mid = 0.5 * (lo + hi);
                double fMid = efficiencyAtDiameter(Math.Exp(mid)) - targetEfficiency;

                if (double.IsNaN(fMid)) return null;

                if (fMid < 0) lo = mid; else hi = mid;

                if ((hi - lo) < relativeTolerance) break;
            }

            return Math.Exp(0.5 * (lo + hi));
        }
    }
}
