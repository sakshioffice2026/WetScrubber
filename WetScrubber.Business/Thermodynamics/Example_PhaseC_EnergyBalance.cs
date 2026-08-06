using System;
using System.Collections.Generic;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Phase C Example: Energy balance validation.
    /// Demonstrates enthalpy tracking from inlet → outlet and heat released.
    /// </summary>
    public static class Example_PhaseC_EnergyBalance
    {
        public static void Main()
        {
            Console.WriteLine("═══ PHASE C: Rigorous Energy Balance ═══\n");

            // ── Scenario: SO2 scrubber, flue gas + water ────────────────────────
            var energyService = new EnergyBalanceService();

            // Inlet conditions
            double gasFlowKgS = 5.0;          // 5 kg/s flue gas
            double gasInletTempC = 40.0;      // Hot flue gas
            double liquidFlowKgS = 10.0;      // 10 kg/s scrubbing liquid
            double liquidInletTempC = 25.0;   // Fresh makeup water

            // Outlet conditions (from scrubber solver)
            double gasOutletTempC = 35.0;     // Cooled by liquid (heat transfer) + adiabatic cooling
            double liquidOutletTempC = 35.0;  // Heated by exothermic absorption

            // Heat released by SO2 absorption (exothermic)
            // SO2 dissolved: ~0.5 kg/s, ΔH_abs ≈ -67 kJ/mol = -67000 J/mol
            // MW_SO2 = 64 g/mol → 0.5 kg/s = 7.8 kmol/s
            // Q = 7.8 * 67 ≈ 522 kW (very high for this small example, but illustrative)
            // Using more realistic: ~50 ppm SO2 removal → 0.02 kg/s absorbed
            double so2AbsorbedKgS = 0.02;     // Very small amount
            double so2MolWeight = 64.07e-3;   // kg/mol
            double so2DeltaHabsKJMol = 67.0;  // kJ/mol, exothermic
            double heatReleasedKW = (so2AbsorbedKgS / so2MolWeight) * so2DeltaHabsKJMol; // kW

            Console.WriteLine($"Inlet conditions:");
            Console.WriteLine($"  Gas:   {gasFlowKgS} kg/s @ {gasInletTempC}°C");
            Console.WriteLine($"  Liquid: {liquidFlowKgS} kg/s @ {liquidInletTempC}°C\n");

            Console.WriteLine($"Outlet conditions:");
            Console.WriteLine($"  Gas:   {gasFlowKgS} kg/s @ {gasOutletTempC}°C");
            Console.WriteLine($"  Liquid: {liquidFlowKgS} kg/s @ {liquidOutletTempC}°C\n");

            Console.WriteLine($"Absorption heat:");
            Console.WriteLine($"  SO2 absorbed: {so2AbsorbedKgS:F4} kg/s");
            Console.WriteLine($"  ΔH_abs: {so2DeltaHabsKJMol:F1} kJ/mol");
            Console.WriteLine($"  Heat released: {heatReleasedKW:F2} kW\n");

            // ── (1) Energy balance validation ─────────────────────────────────────
            var balance = energyService.ValidateEnergyBalance(
                gasFlowKgS, gasInletTempC,
                liquidFlowKgS, liquidInletTempC,
                gasFlowKgS, gasOutletTempC,
                liquidFlowKgS, liquidOutletTempC,
                heatReleasedKW);

            Console.WriteLine($"Energy balance result:");
            Console.WriteLine($"  H_inlet:  {balance.EnthalpyInletKW:F2} kW");
            Console.WriteLine($"  H_outlet: {balance.EnthalpyOutletKW:F2} kW");
            Console.WriteLine($"  ΔH:       {balance.EnthalpyChangeKW:F2} kW");
            Console.WriteLine($"  Q:        {balance.HeatReleasedKW:F2} kW (absorbed by liquid)");
            Console.WriteLine($"  Residual: {balance.ResidualKW:F3} kW ({balance.ResidualPercent:F2}%)");
            Console.WriteLine($"  Closed:   {(balance.IsClosed ? "✓ YES" : "✗ NO")}\n");

            // ── (2) Enthalpy breakdown ───────────────────────────────────────────
            Console.WriteLine($"Specific enthalpies (reference: 0°C for liquid, 0 K for gas):");
            Console.WriteLine($"  Gas @ {gasInletTempC}°C:  h = {energyService.CalculateGasSpecificEnthalpyKJKg(gasInletTempC):F2} kJ/kg");
            Console.WriteLine($"  Gas @ {gasOutletTempC}°C:  h = {energyService.CalculateGasSpecificEnthalpyKJKg(gasOutletTempC):F2} kJ/kg");
            Console.WriteLine($"  Liq @ {liquidInletTempC}°C:  h = {energyService.CalculateLiquidSpecificEnthalpyKJKg(liquidInletTempC):F2} kJ/kg");
            Console.WriteLine($"  Liq @ {liquidOutletTempC}°C:  h = {energyService.CalculateLiquidSpecificEnthalpyKJKg(liquidOutletTempC):F2} kJ/kg\n");

            // ── (3) Physical interpretation ──────────────────────────────────────
            Console.WriteLine($"Physical interpretation:");
            Console.WriteLine($"  Gas cooled by contact with liquid: ΔT = {gasInletTempC - gasOutletTempC:F1}°C");
            Console.WriteLine($"  Liquid heated by absorption + gas cooling: ΔT = {liquidOutletTempC - liquidInletTempC:F1}°C");
            Console.WriteLine($"  Absorption releases {heatReleasedKW:F2} kW → sensible heat to liquid");
            Console.WriteLine($"  Energy balance closure: {(balance.IsClosed ? "PASS" : "FAIL")} (residual = {Math.Abs(balance.ResidualKW):F3} kW)\n");

            // ── (4) Demonstrate impact of temperature on mass transfer ───────────
            Console.WriteLine($"Impact on mass transfer (Henry's law):");
            var henrysCalc = new HenrysLawCalculator();
            double so2HenryRef = 0.83;
            double so2HeatSoln = -28.0; // kJ/mol
            double hInlet = henrysCalc.GetTemperatureCorrectedHenrysConstant(so2HenryRef, so2HeatSoln, gasInletTempC, -1700 * 8.314);
            double hOutlet = henrysCalc.GetTemperatureCorrectedHenrysConstant(so2HenryRef, so2HeatSoln, gasOutletTempC, -1700 * 8.314);
            Console.WriteLine($"  Henry const @ inlet ({gasInletTempC}°C): {hInlet:F4} mol/(L·atm)");
            Console.WriteLine($"  Henry const @ outlet ({gasOutletTempC}°C): {hOutlet:F4} mol/(L·atm)");
            Console.WriteLine($"  Δ: {((hOutlet - hInlet) / hInlet * 100):F1}% (higher T → lower solubility)\n");

            Console.WriteLine("═══ PHASE C: Energy balance + thermodynamics coupled ═══");
        }
    }
}