using System;
using System.Collections.Generic;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// One layer in the discretized tower.
    /// </summary>
    public sealed class TowerSegment
    {
        public int LayerIndex { get; set; }
        public double GasInletPpm { get; set; }
        public double GasOutletPpm { get; set; }
        public double LiquidInletTempC { get; set; }
        public double LiquidOutletTempC { get; set; }
        public double GasTemperatureC { get; set; }
        public double SegmentRemovalFraction { get; set; } // 0 to 1
        public double HeatAbsorbedKW { get; set; }
    }

    /// <summary>
    /// RETIRED for design-grade execution. The previous implementation applied a fixed
    /// per-segment removal of 1 - exp(-0.4) with no mass-transfer physics. Types are kept
    /// so existing references compile; SolveIterative now refuses to run.
    /// Use MultiPollutantOdeSolver.SolveOde instead.
    /// </summary>
    public static class IterativeTowerSolver
    {
        public const bool IsDesignGrade = false;

        public sealed class SolverInput
        {
            public double GasInletPpm { get; set; }
            public double GasOutletTargetPpm { get; set; }
            public double GasTemperatureC { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidInletTempC { get; set; }
            public double LiquidMassFlowKgS { get; set; }
            public double LiquidDensityKgM3 { get; set; }
            public double HenrysLawConstantReference { get; set; }
            public double HeatOfAbsorptionKJKmol { get; set; }
            public double PollutantMolecularWeight { get; set; }
            public Func<double, double> HenrysLawTemperatureCorrectionFn { get; set; } = _ => 1.0;
        }

        public sealed class SolverOutput
        {
            public List<TowerSegment> Segments { get; set; } = new();
            public double LiquidOutletTemperatureC { get; set; }
            public double OverallRemovalEfficiency { get; set; }
            public double TotalHeatAbsorbedKW { get; set; }
            public bool Converged { get; set; }
            public int IterationCount { get; set; }
            public bool IsValid { get; set; }
        }

        public static SolverOutput SolveIterative(SolverInput input, int numSegments = 5)
        {
            throw new NotSupportedException(
                "IterativeTowerSolver is not design-grade (fixed-removal approximation) and is disabled. " +
                "Use MultiPollutantOdeSolver.SolveOde with complete packing and property inputs.");
        }
    }
}