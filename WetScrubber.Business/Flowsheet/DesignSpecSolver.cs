using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.Flowsheet
{
    /// <summary>
    /// Phase D: Design-spec solver for wet scrubbers.
    /// 
    /// Reverse problem: Given target outlet ppm, find liquid flow (or tower height)
    /// that achieves it. Uses root-finding (bisection) to solve:
    ///   f(variable) = outlet_ppm(variable) - target_ppm = 0
    /// 
    /// Example: "Design tower so SO2 outlet = 50 ppm (from 100 ppm inlet)"
    /// → Bisection on liquid flow finds L such that outlet_ppm(L) = 50 ppm
    /// </summary>
    public class DesignSpecSolver
    {
        /// <summary>
        /// What parameter to vary (liquid flow, tower height, tower area).
        /// </summary>
        public enum DesignVariable
        {
            LiquidFlowKgS,
            TowerHeightM,
            TowerAreaM2
        }

        /// <summary>
        /// Constraint on a pollutant outlet concentration.
        /// </summary>
        public class DesignSpec
        {
            /// <summary>Target outlet concentration (ppm).</summary>
            public double TargetOutletPpm { get; set; }

            /// <summary>Which pollutant (e.g., "SO2", "NO2").</summary>
            public string PollutantCode { get; set; }

            /// <summary>Parameter to vary to hit target.</summary>
            public DesignVariable VariableToAdjust { get; set; } = DesignVariable.LiquidFlowKgS;

            /// <summary>Lower bound for binary search (physical limit).</summary>
            public double VariableMin { get; set; }

            /// <summary>Upper bound for binary search (design envelope).</summary>
            public double VariableMax { get; set; }

            /// <summary>Convergence tolerance (ppm).</summary>
            public double TolerancePpm { get; set; } = 0.1;

            /// <summary>Max iterations before giving up.</summary>
            public int MaxIterations { get; set; } = 50;
        }

        /// <summary>
        /// Result of design-spec solving.
        /// </summary>
        public class DesignResult
        {
            /// <summary>Converged successfully.</summary>
            public bool Converged { get; set; }

            /// <summary>Number of iterations used.</summary>
            public int Iterations { get; set; }

            /// <summary>Final value of the variable (liquid flow, tower height, etc).</summary>
            public double FinalVariableValue { get; set; }

            /// <summary>Outlet ppm achieved at final value.</summary>
            public double AchievedOutletPpm { get; set; }

            /// <summary>Error: |achieved - target| (ppm).</summary>
            public double ErrorPpm { get; set; }

            /// <summary>Convergence history (for diagnostics).</summary>
            public List<(int iter, double variable, double outlet_ppm, double error)> History { get; set; } = new();
        }

        /// <summary>
        /// Solve design spec using bisection method on liquid flow.
        /// Finds L_liquid such that outlet_ppm(L_liquid) = target_ppm.
        /// </summary>
        public static DesignResult SolveDesignSpec(
            MultiPollutantOdeSolver.SolverInput baseInput,
            DesignSpec spec)
        {
            if (spec == null)
                throw new ArgumentNullException(nameof(spec));

            if (spec.VariableMin >= spec.VariableMax)
                throw new ArgumentException(
                    $"VariableMin ({spec.VariableMin}) must be < VariableMax ({spec.VariableMax})");

            var result = new DesignResult();
            double lo = spec.VariableMin;
            double hi = spec.VariableMax;

            // Evaluate function at bounds to check bracket
            double fLo = EvaluateOutletPpm(baseInput, lo, spec.VariableToAdjust) - spec.TargetOutletPpm;
            double fHi = EvaluateOutletPpm(baseInput, hi, spec.VariableToAdjust) - spec.TargetOutletPpm;

            if (fLo * fHi > 0)
            {
                result.Converged = false;
                result.Iterations = 0;
                result.ErrorPpm = Math.Min(Math.Abs(fLo), Math.Abs(fHi));
                result.History.Add((0, (lo + hi) / 2, spec.TargetOutletPpm + fLo, Math.Abs(fLo)));
                return result;  // Not bracketed
            }

            // Bisection
            for (int iter = 0; iter < spec.MaxIterations; iter++)
            {
                double mid = (lo + hi) / 2.0;
                double fMid = EvaluateOutletPpm(baseInput, mid, spec.VariableToAdjust) - spec.TargetOutletPpm;

                double outletPpm = EvaluateOutletPpm(baseInput, mid, spec.VariableToAdjust);
                double error = Math.Abs(fMid);

                result.History.Add((iter, mid, outletPpm, error));
                result.FinalVariableValue = mid;
                result.AchievedOutletPpm = outletPpm;
                result.ErrorPpm = error;
                result.Iterations = iter + 1;

                if (error < spec.TolerancePpm)
                {
                    result.Converged = true;
                    break;
                }

                if (fLo * fMid < 0)
                    hi = mid;  // Root in [lo, mid]
                else
                    lo = mid;  // Root in [mid, hi]
            }

            return result;
        }

        /// <summary>
        /// Evaluate outlet concentration for a given variable value.
        /// Solves mass transfer given the variable, returns outlet ppm.
        /// </summary>
        private static double EvaluateOutletPpm(
            MultiPollutantOdeSolver.SolverInput baseInput,
            double variableValue,
            DesignVariable variable)
        {
            // Clone input and adjust the variable
            var input = CloneInput(baseInput);

            switch (variable)
            {
                case DesignVariable.LiquidFlowKgS:
                    input.LiquidMassFlowKgS = variableValue;
                    break;
                case DesignVariable.TowerHeightM:
                    input.TowerHeightM = variableValue;
                    break;
                case DesignVariable.TowerAreaM2:
                    input.TowerAreaM2 = variableValue;
                    break;
            }

            // Solve and get outlet ppm
            var output = MultiPollutantOdeSolver.SolveOde(input);

            // Find the pollutant with the highest outlet concentration
            // (typically the hardest to absorb)
            if (output.OutletConcKgM3 != null && output.OutletConcKgM3.Count > 0)
                return output.OutletConcKgM3.Values.Max();

            return double.MaxValue;  // Fallback: no solution found
        }

        /// <summary>
        /// Deep clone solver input to avoid mutating base case.
        /// </summary>
        private static MultiPollutantOdeSolver.SolverInput CloneInput(
            MultiPollutantOdeSolver.SolverInput original)
        {
            return new MultiPollutantOdeSolver.SolverInput
            {
                Pollutants = new List<MultiPollutantIterativeSolver.PollutantInput>(original.Pollutants),
                GasTemperatureC = original.GasTemperatureC,
                GasMassFlowKgS = original.GasMassFlowKgS,
                GasCompositionMoleFraction = original.GasCompositionMoleFraction,
                LiquidInletTempC = original.LiquidInletTempC,
                LiquidMassFlowKgS = original.LiquidMassFlowKgS,
                LiquidDensityKgM3 = original.LiquidDensityKgM3,
                LegacyGasDensityKgM3 = original.LegacyGasDensityKgM3,
                TowerHeightM = original.TowerHeightM,
                TowerAreaM2 = original.TowerAreaM2,
                PackingSpecificAreaM2M3 = original.PackingSpecificAreaM2M3,
                PackingNominalSizeM = original.PackingNominalSizeM,
                PressureKPa = original.PressureKPa,
                InletLiquidLoadingKgKg = original.InletLiquidLoadingKgKg
            };
        }
    }
}