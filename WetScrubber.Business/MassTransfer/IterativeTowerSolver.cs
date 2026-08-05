using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// One layer in the discretized tower. Models a segment with
    /// inlet/outlet compositions, temperatures, and removal.
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
    /// Iterative packed-tower solver using Richardson discretization:
    /// divide the tower into N segments, solve mass + energy balance
    /// per segment, iterate until temperature convergence.
    /// 
    /// NOT a rigorous differential equation solver — this is an
    /// engineering approximation sufficient for preliminary design.
    /// Real industrial use needs rigorous ODE integration or collocation.
    /// </summary>
    public static class IterativeTowerSolver
    {
        private const int DefaultSegments = 5;
        private const int MaxIterations = 20;
        private const double TemperatureConvergenceTolC = 0.1;
        private const double LiquidHeatCapacityKJKgC = 3.5; // water + dissolved salts

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
        }

        public static SolverOutput SolveIterative(
            SolverInput input,
            int numSegments = DefaultSegments)
        {
            if (numSegments < 2) numSegments = 2;

            var output = new SolverOutput { Segments = new List<TowerSegment>(numSegments) };
            double[] liquidTempProfile = new double[numSegments + 1];
            double[] liquidTempProfileOld = new double[numSegments + 1];

            liquidTempProfile[0] = input.LiquidInletTempC;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                Array.Copy(liquidTempProfile, liquidTempProfileOld, liquidTempProfile.Length);
                output.Segments.Clear();

                double gasInletLocal = input.GasInletPpm;

                for (int seg = 0; seg < numSegments; seg++)
                {
                    double liquidTempSegment = (liquidTempProfile[seg] + liquidTempProfile[seg + 1]) / 2.0;
                    double hCorr = input.HenrysLawTemperatureCorrectionFn(liquidTempSegment);
                    double hLocal = input.HenrysLawConstantReference * hCorr;

                    // Approximate segment removal via simplified NTU: 
                    // each segment removes ~(1 - exp(-k*HTU)) fraction
                    double k = 0.4; // empirical strip factor
                    double removalFrac = Math.Min(1.0 - Math.Exp(-k), 0.9);
                    double gasOutletLocal = gasInletLocal * (1.0 - removalFrac);

                    // Mass of pollutant absorbed in this segment (kmol)
                    double gasFlowKmolS = input.GasMassFlowKgS / 28.97; // air MW
                    double pollutantFlowInKmolS = (gasInletLocal / 1e6) * gasFlowKmolS;
                    double pollutantAbsorbedKmolS = removalFrac * pollutantFlowInKmolS;

                    // Heat released (kJ/s)
                    double heatKW = pollutantAbsorbedKmolS * Math.Abs(input.HeatOfAbsorptionKJKmol) / 1000.0;

                    // Temperature rise in liquid (assume no heat removal, only absorption)
                    double dT = heatKW * 3600.0 / (input.LiquidMassFlowKgS * LiquidHeatCapacityKJKgC);
                    liquidTempProfile[seg + 1] = liquidTempProfile[seg] + dT;

                    output.Segments.Add(new TowerSegment
                    {
                        LayerIndex = seg,
                        GasInletPpm = gasInletLocal,
                        GasOutletPpm = gasOutletLocal,
                        LiquidInletTempC = liquidTempProfile[seg],
                        LiquidOutletTempC = liquidTempProfile[seg + 1],
                        GasTemperatureC = input.GasTemperatureC,
                        SegmentRemovalFraction = removalFrac,
                        HeatAbsorbedKW = heatKW
                    });

                    gasInletLocal = gasOutletLocal;
                }

                output.LiquidOutletTemperatureC = liquidTempProfile[numSegments];
                output.TotalHeatAbsorbedKW = output.Segments.Sum(s => s.HeatAbsorbedKW);
                output.OverallRemovalEfficiency = (input.GasInletPpm - gasInletLocal) / input.GasInletPpm * 100.0;

                // Check convergence
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