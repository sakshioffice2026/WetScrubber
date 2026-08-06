using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Wrapper: RigourousTowerOdeSolver + MultiPollutantIterativeSolver interface.
    /// Accepts same input as MultiPollutantIterativeSolver, solves via RK45 ODE.
    /// </summary>
    public static class MultiPollutantOdeSolver
    {
        public sealed class SolverInput
        {
            public List<MultiPollutantIterativeSolver.PollutantInput> Pollutants { get; set; } = new();
            public double GasTemperatureC { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidInletTempC { get; set; }
            public double LiquidMassFlowKgS { get; set; }
            public double LiquidDensityKgM3 { get; set; }
            public double GasDensityKgM3 { get; set; }
            public double TowerHeightM { get; set; }
            public double TowerAreaM2 { get; set; }
            public double PackingSpecificAreaM2M3 { get; set; }
            public double PackingNominalSizeM { get; set; }
        }

        public sealed class SolverOutput
        {
            public List<MultiPollutantSegment> Segments { get; set; } = new();
            public double LiquidOutletTemperatureC { get; set; }
            public Dictionary<string, double> OverallRemovalEfficiency { get; set; } = new();
            public double TotalHeatAbsorbedKW { get; set; }
            public bool Converged { get; set; }
            public int NodeCount { get; set; }
            public double OutletGasTemperatureK { get; set; }
            public IReadOnlyDictionary<string, double> OutletConcKgM3 { get; set; }
        }

        public static SolverOutput SolveOde(SolverInput input)
        {
            var odeInput = new RigourousTowerOdeSolver.SolverInput
            {
                PollutantCodes = input.Pollutants.Select(p => p.Code).ToList(),
                InletConcKgM3 = input.Pollutants.ToDictionary(p => p.Code, p => p.InletPpm),
                InitialLiquidFraction = input.Pollutants.ToDictionary(p => p.Code, p => 0.0001),
                GasTemperatureK = input.GasTemperatureC + 273.15,
                LiquidInletTemperatureK = input.LiquidInletTempC + 273.15,
                TowerHeightM = input.TowerHeightM,
                TowerAreaM2 = input.TowerAreaM2,
                GasMassFlowKgS = input.GasMassFlowKgS,
                LiquidMassFlowKgS = input.LiquidMassFlowKgS,
                LiquidDensityKgM3 = input.LiquidDensityKgM3,
                GasDensityKgM3 = input.GasDensityKgM3,

                OndaLookup = (code, Tg, Tl) => OndaMassTransferCorrelation.Calculate(
                    input.PackingSpecificAreaM2M3,
                    input.PackingNominalSizeM,
                    72.0,  // sigma_c (water on plastic)
                    72.0,  // sigma_L (water surface tension)
                    input.LiquidMassFlowKgS / input.TowerAreaM2,
                    input.GasMassFlowKgS / input.TowerAreaM2,
                    input.LiquidDensityKgM3,
                    input.GasDensityKgM3,
                    1e-3, 1e-5,  // mu_L, mu_G (placeholders)
                    2e-9, 2e-5,  // D_L, D_G
                    (Tg + Tl) / 2.0, 101.325),

                HenrysLawFn = (code, T) =>
                {
                    var poll = input.Pollutants.First(p => p.Code == code);
                    double corr = poll.HenrysLawTemperatureCorrectionFn(T - 273.15);
                    return poll.HenrysLawConstant * corr;
                },

                MolWeightFn = (code) => input.Pollutants.First(p => p.Code == code).MolecularWeight,
                HeatOfAbsorptionFn = (code) => input.Pollutants.First(p => p.Code == code).HeatOfAbsorptionKJKmol
            };

            var odeSolver = new RigourousTowerOdeSolver();
            var odeOutput = odeSolver.Solve(odeInput);

            // Convert ODE profile → multi-segment output (for compatibility)
            var output = new SolverOutput
            {
                Converged = odeOutput.Converged,
                NodeCount = odeOutput.Profile.Count,
                LiquidOutletTemperatureC = odeOutput.OutletLiquidTemperatureK - 273.15,
                OverallRemovalEfficiency = odeOutput.RemovalEfficiency,
                Segments = new List<MultiPollutantSegment>(),

                OutletGasTemperatureK = odeOutput.Profile.Last().GasTemperatureK,

                OutletConcKgM3 = odeOutput.Profile.Last().PollutantConcKgM3
            };

            // Create synthetic segments from ODE nodes (every 5th node)
            int stride = Math.Max(1, odeOutput.Profile.Count / 5);
            for (int i = 0; i < odeOutput.Profile.Count; i += stride)
            {
                var node = odeOutput.Profile[i];
                var seg = new MultiPollutantSegment
                {
                    LayerIndex = i / stride,
                    LiquidInletTempC = node.LiquidTemperatureK - 273.15,
                    GasTemperatureC = node.GasTemperatureK - 273.15,
                    Pollutants = new Dictionary<string, PollutantSegmentState>()
                };

                foreach (var code in input.Pollutants.Select(p => p.Code))
                {
                    seg.Pollutants[code] = new PollutantSegmentState
                    {
                        PollutantCode = code,
                        GasInletPpm = node.PollutantConcKgM3[code],
                        RemovalFraction = 0.0 // TODO: compute from inlet/outlet
                    };
                }

                output.Segments.Add(seg);
            }

            output.TotalHeatAbsorbedKW = 0.0; // Could compute from profiles
            return output;
        }
    }
}