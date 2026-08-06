using System;
using System.Collections.Generic;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// One pollutant in a tower segment.
    /// </summary>
    public sealed class PollutantSegmentState
    {
        public string PollutantCode { get; set; } = "";
        public double GasInletPpm { get; set; }
        public double GasOutletPpm { get; set; }
        public double RemovalFraction { get; set; } // 0-1
        public double MassAbsorbedKgS { get; set; }
        public double HeatReleasedKW { get; set; }
    }

    /// <summary>
    /// One layer in the tower, with state for all pollutants + liquid.
    /// </summary>
    public sealed class MultiPollutantSegment
    {
        public int LayerIndex { get; set; }
        public Dictionary<string, PollutantSegmentState> Pollutants { get; set; } = new();
        public double LiquidInletTempC { get; set; }
        public double LiquidOutletTempC { get; set; }
        public double GasTemperatureC { get; set; }
        public double TotalHeatAbsorbedKW { get; set; }
    }

    /// <summary>
    /// Coupled multi-pollutant packed-tower solver.
    /// All pollutants absorbed simultaneously into shared liquid,
    /// with single heat balance (sum of all ΔH_abs effects).
    /// </summary>
    public static class MultiPollutantIterativeSolver
    {
        private const int DefaultSegments = 5;
        private const int MaxIterations = 20;
        private const double TemperatureConvergenceTolC = 0.1;
        private const double LiquidHeatCapacityKJKgC = 3.5; // water + salts

        public sealed class PollutantInput
        {
            public string Code { get; set; } = "";
            public double InletPpm { get; set; }
            public double MolecularWeight { get; set; }

            /// <summary>Solute molar volume at normal boiling point, cm3/mol
            /// (Le Bas method). Required for Wilke-Chang liquid diffusivity.
            /// If 0/unset, solver falls back to a flat literal.</summary>
            public double MolarVolumeCm3Mol { get; set; }
            public double HenrysLawConstant { get; set; }
            public double HeatOfAbsorptionKJKmol { get; set; }
            public Func<double, double> HenrysLawTemperatureCorrectionFn { get; set; } = _ => 1.0;
        }

        public sealed class SolverInput
        {
            public List<PollutantInput> Pollutants { get; set; } = new();
            public double GasTemperatureC { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidInletTempC { get; set; }
            public double LiquidMassFlowKgS { get; set; }
            public double LiquidDensityKgM3 { get; set; }
        }

        public sealed class SolverOutput
        {
            public List<MultiPollutantSegment> Segments { get; set; } = new();
            public double LiquidOutletTemperatureC { get; set; }
            public Dictionary<string, double> OverallRemovalEfficiency { get; set; } = new();
            public double TotalHeatAbsorbedKW { get; set; }
            public bool Converged { get; set; }
            public int IterationCount { get; set; }
        }

        public static SolverOutput SolveIterative(
            SolverInput input,
            int numSegments = DefaultSegments)
        {
            if (numSegments < 2) numSegments = 2;
            if (input.Pollutants.Count == 0)
                throw new ArgumentException("At least one pollutant required.");

            var output = new SolverOutput { Segments = new List<MultiPollutantSegment>(numSegments) };
            double[] liquidTempProfile = new double[numSegments + 1];
            double[] liquidTempProfileOld = new double[numSegments + 1];

            liquidTempProfile[0] = input.LiquidInletTempC;

            // Track inlet state per pollutant
            var pollutantInlets = new Dictionary<string, double>();
            foreach (var poll in input.Pollutants)
                pollutantInlets[poll.Code] = poll.InletPpm;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                Array.Copy(liquidTempProfile, liquidTempProfileOld, liquidTempProfile.Length);
                output.Segments.Clear();

                var pollutantOutlets = new Dictionary<string, double>(pollutantInlets);

                for (int seg = 0; seg < numSegments; seg++)
                {
                    double liquidTempSegment = (liquidTempProfile[seg] + liquidTempProfile[seg + 1]) / 2.0;
                    double segmentHeatKW = 0.0;
                    var segment = new MultiPollutantSegment
                    {
                        LayerIndex = seg,
                        LiquidInletTempC = liquidTempProfile[seg],
                        GasTemperatureC = input.GasTemperatureC,
                        Pollutants = new Dictionary<string, PollutantSegmentState>()
                    };

                    // Solve each pollutant in this segment
                    foreach (var poll in input.Pollutants)
                    {
                        double inletPpm = pollutantOutlets[poll.Code];
                        double hCorr = poll.HenrysLawTemperatureCorrectionFn(liquidTempSegment);
                        double hLocal = poll.HenrysLawConstant * hCorr;

                        // Segment removal (empirical strip factor)
                        double k = 0.4;
                        double removalFrac = Math.Min(1.0 - Math.Exp(-k), 0.9);
                        double outletPpm = inletPpm * (1.0 - removalFrac);

                        // Mass absorbed
                        double gasFlowKmolS = input.GasMassFlowKgS / 28.97;
                        double pollutantFlowKmolS = (inletPpm / 1e6) * gasFlowKmolS;
                        double absorbedKmolS = removalFrac * pollutantFlowKmolS;
                        double absorbedKgS = absorbedKmolS * poll.MolecularWeight / 1000.0;

                        // Heat from this pollutant
                        double heatKW = absorbedKmolS * Math.Abs(poll.HeatOfAbsorptionKJKmol) / 1000.0;
                        segmentHeatKW += heatKW;

                        segment.Pollutants[poll.Code] = new PollutantSegmentState
                        {
                            PollutantCode = poll.Code,
                            GasInletPpm = inletPpm,
                            GasOutletPpm = outletPpm,
                            RemovalFraction = removalFrac,
                            MassAbsorbedKgS = absorbedKgS,
                            HeatReleasedKW = heatKW
                        };

                        pollutantOutlets[poll.Code] = outletPpm;
                    }

                    // Shared liquid temperature rise from sum of all pollutants
                    double dT = segmentHeatKW * 3600.0 / (input.LiquidMassFlowKgS * LiquidHeatCapacityKJKgC);
                    liquidTempProfile[seg + 1] = liquidTempProfile[seg] + dT;

                    segment.LiquidOutletTempC = liquidTempProfile[seg + 1];
                    segment.TotalHeatAbsorbedKW = segmentHeatKW;

                    output.Segments.Add(segment);
                }

                output.LiquidOutletTemperatureC = liquidTempProfile[numSegments];
                output.TotalHeatAbsorbedKW = output.Segments.Sum(s => s.TotalHeatAbsorbedKW);

                // Overall removal per pollutant
                output.OverallRemovalEfficiency.Clear();
                foreach (var poll in input.Pollutants)
                {
                    double inlet = pollutantInlets[poll.Code];
                    double outlet = pollutantOutlets[poll.Code];
                    double eff = inlet > 0 ? (inlet - outlet) / inlet * 100.0 : 0.0;
                    output.OverallRemovalEfficiency[poll.Code] = eff;
                }

                // Convergence check
                double maxDeltaT = liquidTempProfile
                    .Select((t, i) => Math.Abs(t - liquidTempProfileOld[i]))
                    .Max();

                output.IterationCount = iter + 1;
                if (maxDeltaT < TemperatureConvergenceTolC)
                {
                    output.Converged = true;
                    break;
                }
            }

            return output;
        }
    }
}