using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Side-by-side comparison: Richardson 5-segment (4a iterative) vs RK45 ODE (4b).
    /// </summary>
    public sealed class Example_4a_4b_Comparison
    {
        public static void Main()
        {
            // ── Input (multi-pollutant): SO₂ + H₂S + NH₃ ──────────
            var pollutants = new List<MultiPollutantIterativeSolver.PollutantInput>
            {
                new()
                {
                    Code = "SO2",
                    InletPpm = 500,
                    MolecularWeight = 64,
                    HenrysLawConstant = 1.5e5,  // Pa/mol fraction
                    HeatOfAbsorptionKJKmol = -40000,
                    HenrysLawTemperatureCorrectionFn = T => 1.0 + 0.01 * (T - 25)
                },
                new()
                {
                    Code = "H2S",
                    InletPpm = 200,
                    MolecularWeight = 34,
                    HenrysLawConstant = 9.7e4,
                    HeatOfAbsorptionKJKmol = -45000,
                    HenrysLawTemperatureCorrectionFn = T => 1.0 + 0.012 * (T - 25)
                },
                new()
                {
                    Code = "NH3",
                    InletPpm = 100,
                    MolecularWeight = 17,
                    HenrysLawConstant = 58,
                    HeatOfAbsorptionKJKmol = -38000,
                    HenrysLawTemperatureCorrectionFn = T => 1.0 + 0.008 * (T - 25)
                }
            };

            var baseInput = new MultiPollutantIterativeSolver.SolverInput
            {
                Pollutants = pollutants,
                GasTemperatureC = 50,
                GasMassFlowKgS = 100,
                LiquidInletTempC = 25,
                LiquidMassFlowKgS = 50,
                LiquidDensityKgM3 = 1000
            };

            // ── 4a: Richardson 5-segment (EXISTING) ──────────
            Console.WriteLine("═══ PHASE 4a: MultiPollutant Iterative (5-segment Richardson) ═══\n");
            var iter5 = MultiPollutantIterativeSolver.SolveIterative(baseInput, numSegments: 5);
            PrintResults_4a(iter5, "5-segment");

            // ── 4b: RK45 ODE (NEW) ──────────
            Console.WriteLine("\n═══ PHASE 4b: RK45 ODE Solver ═══\n");
            var odeInput = new MultiPollutantOdeSolver.SolverInput
            {
                Pollutants = baseInput.Pollutants,
                GasTemperatureC = baseInput.GasTemperatureC,
                GasMassFlowKgS = baseInput.GasMassFlowKgS,
                LiquidInletTempC = baseInput.LiquidInletTempC,
                LiquidMassFlowKgS = baseInput.LiquidMassFlowKgS,
                LiquidDensityKgM3 = baseInput.LiquidDensityKgM3,
                GasDensityKgM3 = 1.2,             // kg/m³
                TowerHeightM = 5.0,               // m
                TowerAreaM2 = 2.0,                // m²
                PackingSpecificAreaM2M3 = 250,    // m²/m³ (Pall Ring 25mm)
                PackingNominalSizeM = 0.025       // m
            };

            var odeResult = MultiPollutantOdeSolver.SolveOde(odeInput);
            PrintResults_4b(odeResult, "RK45 ODE");

            // ── Comparison ──────────
            Console.WriteLine("\n═══ COMPARISON: 4a vs 4b ═══\n");
            foreach (var poll in pollutants)
            {
                Console.WriteLine($"{poll.Code}:");
                Console.WriteLine($"  4a removal: {iter5.OverallRemovalEfficiency[poll.Code]:F1}%");
                Console.WriteLine($"  4b removal: {odeResult.OverallRemovalEfficiency[poll.Code]:F1}%");
            }
            Console.WriteLine($"\n4a liquid outlet: {iter5.LiquidOutletTemperatureC:F1}°C");
            Console.WriteLine($"4b liquid outlet: {odeResult.LiquidOutletTemperatureC:F1}°C");
        }

        private static void PrintResults_4a(MultiPollutantIterativeSolver.SolverOutput output, string label)
        {
            Console.WriteLine($"Method: {label}");
            Console.WriteLine($"Converged: {output.Converged} (iterations: {output.IterationCount})");
            Console.WriteLine($"Liquid outlet: {output.LiquidOutletTemperatureC:F1}°C");
            Console.WriteLine($"Total heat: {output.TotalHeatAbsorbedKW:F1} kW\n");

            Console.WriteLine("Pollutant removals:");
            foreach (var (code, eff) in output.OverallRemovalEfficiency)
                Console.WriteLine($"  {code}: {eff:F1}%");

            Console.WriteLine("\nSegment profile:");
            foreach (var seg in output.Segments.Take(3))
            {
                Console.WriteLine($"  Layer {seg.LayerIndex}: T_liq={seg.LiquidOutletTempC:F1}°C, " +
                    $"Q={seg.TotalHeatAbsorbedKW:F2}kW");
            }
        }

        private static void PrintResults_4b(MultiPollutantOdeSolver.SolverOutput output, string label)
        {
            Console.WriteLine($"Method: {label}");
            Console.WriteLine($"Converged: {output.Converged} (ODE nodes: {output.NodeCount})");
            Console.WriteLine($"Liquid outlet: {output.LiquidOutletTemperatureC:F1}°C\n");

            Console.WriteLine("Pollutant removals:");
            foreach (var (code, eff) in output.OverallRemovalEfficiency)
                Console.WriteLine($"  {code}: {eff:F1}%");

            Console.WriteLine("\nODE profile (sample):");
            int stride = Math.Max(1, output.Segments.Count / 3);
            foreach (var seg in output.Segments.Where((s, i) => i % stride == 0).Take(3))
            {
                Console.WriteLine($"  Node {seg.LayerIndex}: T_gas={seg.GasTemperatureC:F1}°C, " +
                    $"T_liq={seg.LiquidInletTempC:F1}°C");
            }
        }
    }
}