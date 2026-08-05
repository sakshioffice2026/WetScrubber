using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Peng-Robinson (1976) cubic equation of state, with van der Waals
    /// one-fluid mixing rules (kij = 0 — no binary interaction
    /// correction between gas-phase species yet; that would be a further
    /// refinement once real kij data is sourced, same caveat as the
    /// NRTL tau/alpha table in Phase 0 — don't fabricate kij values).
    ///
    ///   P = RT/(Vm-b) - a(T) / (Vm(Vm+b) + b(Vm-b))
    ///
    /// Solved in its cubic-in-Z form:
    ///   Z^3 - (1-B)Z^2 + (A - 2B - 3B^2)Z - (AB - B^2 - B^3) = 0
    /// The largest real, positive root is taken as the vapor-phase Z —
    /// correct for a gas stream in a scrubber, which is never expected
    /// to be near its dew point at the tower's operating conditions.
    /// </summary>
    public sealed class PengRobinsonEos : IEquationOfState
    {
        private const double R = 8.314; // J/(mol*K)

        public EosResult Evaluate(
            IReadOnlyList<EosComponentInput> components,
            double temperatureK,
            double pressureKPa)
        {
            if (components == null || components.Count == 0)
                throw new ArgumentException("At least one component is required.", nameof(components));

            double moleFractionSum = components.Sum(c => c.MoleFraction);
            if (Math.Abs(moleFractionSum - 1.0) > 0.01)
                throw new ArgumentException(
                    $"Component mole fractions must sum to ~1.0 (got {moleFractionSum:F4}). " +
                    "Build the mixture with GasMixtureBuilder rather than passing raw fractions.");

            double pressurePa = pressureKPa * 1000.0;

            // ── Per-component a_i, b_i ──────────────────────────────
            var pure = components.Select(c =>
            {
                double pcPa = c.CriticalPressureKPa * 1000.0;
                double kappa = 0.37464 + 1.54226 * c.AcentricFactor - 0.26992 * c.AcentricFactor * c.AcentricFactor;
                double alpha = Math.Pow(1 + kappa * (1 - Math.Sqrt(temperatureK / c.CriticalTemperatureK)), 2);
                double a_i = 0.45724 * R * R * c.CriticalTemperatureK * c.CriticalTemperatureK / pcPa * alpha;
                double b_i = 0.07780 * R * c.CriticalTemperatureK / pcPa;
                return (c.MoleFraction, a_i, b_i, c.MolecularWeight);
            }).ToList();

            // ── van der Waals one-fluid mixing rules (kij = 0) ──────
            double aMix = 0.0;
            foreach (var i in pure)
                foreach (var j in pure)
                    aMix += i.MoleFraction * j.MoleFraction * Math.Sqrt(i.a_i * j.a_i);

            double bMix = pure.Sum(p => p.MoleFraction * p.b_i);
            double mwMix = pure.Sum(p => p.MoleFraction * p.MolecularWeight);

            double A = aMix * pressurePa / (R * R * temperatureK * temperatureK);
            double B = bMix * pressurePa / (R * temperatureK);

            // Cubic: Z^3 + c2*Z^2 + c1*Z + c0 = 0
            double c2 = -(1 - B);
            double c1 = A - 2 * B - 3 * B * B;
            double c0 = -(A * B - B * B - B * B * B);

            var roots = SolveCubicRealRoots(c2, c1, c0);
            var positiveRoots = roots.Where(r => r > 0).OrderByDescending(r => r).ToList();

            if (positiveRoots.Count == 0)
                throw new InvalidOperationException(
                    "Peng-Robinson solver produced no physically valid (positive) root — " +
                    "check that temperature/pressure/critical-property inputs are sane.");

            double Z = positiveRoots[0]; // vapor root — largest positive root

            double molarVolume = Z * R * temperatureK / pressurePa; // m3/mol
            double densityKgM3 = (pressurePa * mwMix) / (Z * R * temperatureK) / 1000.0; // MW in g/mol -> /1000 for kg

            return new EosResult
            {
                CompressibilityFactor = Z,
                MolarVolumeM3PerMol = molarVolume,
                DensityKgM3 = densityKgM3,
                MixtureMolecularWeight = mwMix,
                A = A,
                B = B,
                AllRealPositiveRoots = positiveRoots
            };
        }

        /// <summary>
        /// Real roots of a depressed cubic t^3 + p*t + q = 0, derived
        /// from Z^3 + b*Z^2 + c*Z + d = 0 via the standard depression
        /// Z = t - b/3. Handles all three discriminant cases (one real
        /// root; a repeated root; three distinct real roots via the
        /// trigonometric method) — validated against NumPy's
        /// polynomial root solver before use here.
        /// </summary>
        private static List<double> SolveCubicRealRoots(double b, double c, double d)
        {
            double p = c - b * b / 3.0;
            double q = 2 * b * b * b / 27.0 - b * c / 3.0 + d;
            double discriminant = (q / 2.0) * (q / 2.0) + (p / 3.0) * (p / 3.0) * (p / 3.0);

            var depressedRoots = new List<double>();

            if (discriminant > 1e-12)
            {
                double sqrtDisc = Math.Sqrt(discriminant);
                double u = -q / 2.0 + sqrtDisc;
                double v = -q / 2.0 - sqrtDisc;
                double cbrtU = Math.Sign(u) * Math.Pow(Math.Abs(u), 1.0 / 3.0);
                double cbrtV = Math.Sign(v) * Math.Pow(Math.Abs(v), 1.0 / 3.0);
                depressedRoots.Add(cbrtU + cbrtV);
            }
            else if (Math.Abs(discriminant) <= 1e-12)
            {
                if (Math.Abs(p) < 1e-12)
                {
                    depressedRoots.Add(0.0);
                }
                else
                {
                    depressedRoots.Add(3 * q / p);
                    depressedRoots.Add(-1.5 * q / p);
                }
            }
            else
            {
                // Three distinct real roots — trigonometric method.
                double m = 2 * Math.Sqrt(-p / 3.0);
                double theta = Math.Acos(Clamp(3 * q / (p * m), -1.0, 1.0)) / 3.0;
                for (int k = 0; k < 3; k++)
                    depressedRoots.Add(m * Math.Cos(theta - 2 * Math.PI * k / 3.0));
            }

            return depressedRoots.Select(t => t - b / 3.0).ToList();
        }

        private static double Clamp(double value, double min, double max)
            => Math.Max(min, Math.Min(max, value));
    }
}