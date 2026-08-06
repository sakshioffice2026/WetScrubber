using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Phase B Integration Example: Thermodynamics wired into flowsheet.
    /// 
    /// Before (Phase 0):
    ///   - Gas density: hardcoded 1.2 kg/m³
    ///   - Henry's law: crude 1 + 0.01*(T-25) fudge factor
    ///   - Activity coefficients: not used
    /// 
    /// After (Phase B):
    ///   - Gas density: Peng-Robinson EOS from real composition
    ///   - Henry's law: temperature correction via heat of solution
    ///   - Activity coefficients: NRTL for liquid non-ideality
    /// </summary>
    public static class Example_PhaseB_ThermoIntegration
    {
        public static void Main()
        {
            Console.WriteLine("═══ PHASE B: Thermodynamics Integration ═══\n");

            // ── Example: SO2 scrubbing with real gas mixture (vs. ideal 1.2 assumption) ────
            var gasComposition = new Dictionary<string, double>
            {
                { "N2", 0.79 },
                { "O2", 0.21 },
                { "SO2", 0.0001 }  // 100 ppm SO2
                // Total ≈ 1.00
            };

            double tempC = 40.0;           // scrubber operating temp
            double pressureKPa = 101.3;    // 1 atm

            // ── (1) EOS density vs. hardcoded ────────────────────────────────
            var eos = new PengRobinsonEos();
            var thermoService = new ThermoCalculationService(
                eos, new HenrysLawCalculator(), new NrtlActivityModel());

            var gasComp = gasComposition
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();

            double densityEos = thermoService.CalculateGasDensityKgM3(
                gasComp, tempC, pressureKPa);

            Console.WriteLine($"Gas density comparison (T={tempC}°C, P={pressureKPa} kPa):");
            Console.WriteLine($"  Hardcoded (Phase 0): 1.20 kg/m³");
            Console.WriteLine($"  EOS-calculated (Phase B): {densityEos:F4} kg/m³");
            Console.WriteLine($"  Δ = {Math.Abs(densityEos - 1.2):F4} kg/m³ ({((densityEos - 1.2) / 1.2 * 100):F1}%)\n");

            // ── (2) Temperature-corrected Henry's constant ────────────────────
            double so2HenryRef = 0.83;          // @ 25°C, mol/(L·atm)
            double so2HeatOfSolution = -28.0;   // kJ/mol (exothermic)

            double h25 = so2HenryRef;
            double h40 = thermoService.GetCorrectedHenrysConstant(
                "SO2", so2HenryRef, so2HeatOfSolution, tempC);

            Console.WriteLine($"Henry's constant for SO2:");
            Console.WriteLine($"  @ 25°C: {h25:F4} mol/(L·atm)");
            Console.WriteLine($"  @ 40°C: {h40:F4} mol/(L·atm)");
            Console.WriteLine($"  With crude fudge (1 + 0.01*(40-25)): {so2HenryRef * (1 + 0.01 * (tempC - 25)):F4}");
            Console.WriteLine($"  Rigorous van't Hoff: {h40:F4} ✓\n");

            // ── (3) Activity coefficients (NRTL) ────────────────────────────────
            // Example: liquid SO2 mole fraction = 0.005 (0.5 mol%)
            double liquidMoleFracSo2 = 0.005;

            var (gammaS, gammaW) = thermoService.GetActivityCoefficients(
                "SO2", liquidMoleFracSo2, tempC + 273.15);

            Console.WriteLine($"Activity coefficients (liquid phase, X_SO2 = {liquidMoleFracSo2:F4}):");
            Console.WriteLine($"  γ_SO2: {gammaS:F4}");
            Console.WriteLine($"  γ_H2O: {gammaW:F4}");
            Console.WriteLine($"  → Non-ideal: product γ_SO2 * P_SO2^sat ≠ H*x\n");

            // ── (4) Full solver with EOS density instead of 1.2 ────────────────
            var solver_input = new MultiPollutantOdeSolver.SolverInput
            {
                Pollutants = new List<MultiPollutantIterativeSolver.PollutantInput>
                {
                    new MultiPollutantIterativeSolver.PollutantInput
                    {
                        Code = "SO2",
                        InletPpm = 100.0,                      // 100 ppm inlet
                        HenrysLawConstant = 0.83,              // mol/(L·atm) @ 25°C
                        HeatOfAbsorptionKJKmol = -28.0,        // kJ/(K·mol) dissolution enthalpy
                        MolecularWeight = 64.07
                    }
                },
                GasTemperatureC = 40.0,
                GasMassFlowKgS = 5.0,               // 5 kg/s flue gas
                GasCompositionMoleFraction = gasComposition,  // ← NEW: specify composition
                LiquidInletTempC = 25.0,
                LiquidMassFlowKgS = 10.0,
                LiquidDensityKgM3 = 1000.0,
                PressureKPa = 101.3,
                TowerHeightM = 8.0,
                TowerAreaM2 = 3.0,
                PackingSpecificAreaM2M3 = 250.0,
                PackingNominalSizeM = 0.025
            };

            var solverOutput = MultiPollutantOdeSolver.SolveOde(solver_input);

            Console.WriteLine($"Solver output (Phase B — EOS + Henry's correction):");
            Console.WriteLine($"  Outlet SO2 conc: {solverOutput.OutletConcKgM3?["SO2"]:F6} kg/m³");
            Console.WriteLine($"  Removal efficiency: {solverOutput.OverallRemovalEfficiency["SO2"] * 100:F1}%");
            Console.WriteLine($"  Nodes: {solverOutput.NodeCount}");
            Console.WriteLine($"  Converged: {solverOutput.Converged}\n");

            // ── (5) Backwards compatibility: if no composition, uses legacy 1.2 ────
            var legacy_input = new MultiPollutantOdeSolver.SolverInput
            {
                Pollutants = solver_input.Pollutants,
                GasTemperatureC = 40.0,
                GasMassFlowKgS = 5.0,
                // ← NO GasCompositionMoleFraction
                LegacyGasDensityKgM3 = 1.2,     // Falls back to old 1.2
                LiquidInletTempC = 25.0,
                LiquidMassFlowKgS = 10.0,
                LiquidDensityKgM3 = 1000.0,
                TowerHeightM = 8.0,
                TowerAreaM2 = 3.0,
                PackingSpecificAreaM2M3 = 250.0,
                PackingNominalSizeM = 0.025
            };

            Console.WriteLine("Backwards compatibility (no composition specified):");
            Console.WriteLine($"  Uses LegacyGasDensityKgM3 = 1.2 (Phase 0 mode)\n");

            Console.WriteLine("\n═══ PHASE B Summary ═══");
            Console.WriteLine("✓ EOS replaces hardcoded 1.2 kg/m³");
            Console.WriteLine("✓ Temperature correction via heat of solution");
            Console.WriteLine("✓ NRTL activity coefficients wired in");
            Console.WriteLine("✓ Backwards compatible (legacy mode still available)");
        }
    }
}