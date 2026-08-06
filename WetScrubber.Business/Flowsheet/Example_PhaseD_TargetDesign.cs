using System;
using System.Collections.Generic;
using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.Flowsheet
{
    /// <summary>
    /// Phase D Example: Design-spec solver (target-driven design).
    /// 
    /// Problem: "Design a wet scrubber to reduce SO2 from 100 ppm inlet to 50 ppm outlet"
    /// 
    /// Approach:
    ///   1. Forward problem (Phase B-C): given design → solve for outlet ppm
    ///   2. Inverse problem (Phase D): given target ppm → find design
    ///   
    /// Uses bisection on liquid flow: find L such that outlet_SO2(L) = 50 ppm
    /// </summary>
    public static class Example_PhaseD_TargetDesign
    {
        public static void Main()
        {
            Console.WriteLine("═══ PHASE D: Design-Spec Solver (Target-Driven Design) ═══\n");

            // ── Base design (fixed geometry) ────────────────────────────────────
            var baseInput = new MultiPollutantOdeSolver.SolverInput
            {
                Pollutants = new List<MultiPollutantIterativeSolver.PollutantInput>
                {
                    new MultiPollutantIterativeSolver.PollutantInput
                    {
                        Code = "SO2",
                        InletPpm = 100.0,                      // 100 ppm inlet
                        HenrysLawConstant = 0.83,
                        HeatOfAbsorptionKJKmol = -67.0,
                        MolecularWeight = 64.07,
                        HenrysLawTemperatureCorrectionFn = T => 1.0 + 0.005 * (T - 25)
                    }
                },
                GasTemperatureC = 40.0,
                GasMassFlowKgS = 5.0,
                GasCompositionMoleFraction = new Dictionary<string, double>
                {
                    { "N2", 0.79 },
                    { "O2", 0.21 },
                    { "SO2", 0.0001 }
                },
                LiquidInletTempC = 25.0,
                LiquidDensityKgM3 = 1000.0,

                // Fixed tower geometry (what we're designing)
                TowerHeightM = 8.0,
                TowerAreaM2 = 3.0,
                PackingSpecificAreaM2M3 = 250.0,
                PackingNominalSizeM = 0.025,
                PressureKPa = 101.3
            };

            // ── (1) Quick forward problem: what's outlet ppm with L = 10 kg/s? ──
            Console.WriteLine("Step 1: Forward problem (forward simulation)");
            Console.WriteLine($"  Fixed tower: H={baseInput.TowerHeightM}m, A={baseInput.TowerAreaM2}m²");
            var inputTest = (MultiPollutantOdeSolver.SolverInput)baseInput.GetType()
                .GetMethod("MemberwiseClone",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(baseInput, null);
            if (inputTest == null) inputTest = baseInput;
            inputTest.LiquidMassFlowKgS = 10.0;

            var output10 = MultiPollutantOdeSolver.SolveOde(inputTest);
            Console.WriteLine($"  L = {inputTest.LiquidMassFlowKgS} kg/s → outlet SO2 = {output10.OutletConcKgM3?["SO2"]:F2} ppm");
            Console.WriteLine();

            // ── (2) Inverse problem: find L such that outlet = 50 ppm ──────────
            Console.WriteLine("Step 2: Inverse problem (design-spec solve)");
            Console.WriteLine($"  Target: outlet SO2 = 50 ppm\n");

            var spec = new DesignSpecSolver.DesignSpec
            {
                PollutantCode = "SO2",
                TargetOutletPpm = 50.0,              // Design to 50 ppm outlet
                VariableToAdjust = DesignSpecSolver.DesignVariable.LiquidFlowKgS,
                VariableMin = 2.0,                   // Minimum practical liquid flow
                VariableMax = 30.0,                  // Maximum practical liquid flow
                TolerancePpm = 0.5,                  // ±0.5 ppm accuracy
                MaxIterations = 50
            };

            var result = DesignSpecSolver.SolveDesignSpec(baseInput, spec);

            Console.WriteLine($"Design-spec solver result:");
            Console.WriteLine($"  Converged: {(result.Converged ? "✓ YES" : "✗ NO")}");
            Console.WriteLine($"  Iterations: {result.Iterations}");
            Console.WriteLine($"  Final liquid flow: {result.FinalVariableValue:F2} kg/s");
            Console.WriteLine($"  Achieved outlet: {result.AchievedOutletPpm:F2} ppm");
            Console.WriteLine($"  Target outlet: {spec.TargetOutletPpm:F2} ppm");
            Console.WriteLine($"  Error: {result.ErrorPpm:F3} ppm\n");

            // ── (3) Convergence history ────────────────────────────────────────
            Console.WriteLine("Convergence history (bisection):");
            Console.WriteLine("Iter  Liquid Flow (kg/s)  Outlet SO2 (ppm)  Error (ppm)");
            Console.WriteLine("────  ───────────────────  ────────────────  ──────────");
            foreach (var (iter, liquid_flow, outlet_ppm, error) in result.History)
            {
                Console.WriteLine($"{iter,3}   {liquid_flow,18:F3}  {outlet_ppm,15:F2}  {error,9:F3}");
            }
            Console.WriteLine();

            // ── (4) Verify with forward simulation ──────────────────────────────
            Console.WriteLine("Step 3: Verification (forward simulation with designed L)");
            var inputDesigned = (MultiPollutantOdeSolver.SolverInput)baseInput.GetType()
                .GetMethod("MemberwiseClone",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(baseInput, null);
            if (inputDesigned == null) inputDesigned = baseInput;
            inputDesigned.LiquidMassFlowKgS = result.FinalVariableValue;

            var outputDesigned = MultiPollutantOdeSolver.SolveOde(inputDesigned);
            Console.WriteLine($"  Liquid flow: {result.FinalVariableValue:F2} kg/s");
            Console.WriteLine($"  Outlet SO2: {outputDesigned.OutletConcKgM3?["SO2"]:F2} ppm");
            Console.WriteLine($"  Removal efficiency: {outputDesigned.OverallRemovalEfficiency["SO2"] * 100:F1}%");
            Console.WriteLine($"  Liquid outlet temp: {outputDesigned.LiquidOutletTemperatureC:F2}°C");
            Console.WriteLine();

            // ── (5) Design sensitivity ─────────────────────────────────────────
            Console.WriteLine("Step 4: Design sensitivity (outlet ppm vs liquid flow)");
            Console.WriteLine("Liquid (kg/s)  Outlet SO2 (ppm)");
            Console.WriteLine("─────────────  ────────────────");
            for (double L = 2.0; L <= 30.0; L += 3.0)
            {
                var input_sens = (MultiPollutantOdeSolver.SolverInput)baseInput.GetType()
                    .GetMethod("MemberwiseClone",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(baseInput, null);
                if (input_sens == null) input_sens = baseInput;
                input_sens.LiquidMassFlowKgS = L;

                var out_sens = MultiPollutantOdeSolver.SolveOde(input_sens);
                double outlppm = out_sens.OutletConcKgM3?["SO2"] ?? double.NaN;
                Console.WriteLine($"{L,13:F1}  {outlppm,15:F2}");
            }
            Console.WriteLine();

            Console.WriteLine("═══ PHASE D: Design-spec solver enables Aspen-like 'target mode' ═══");
        }
    }
}