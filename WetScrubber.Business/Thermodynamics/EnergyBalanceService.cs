using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Phase C: Rigorous energy balance across flowsheet.
    /// Validates that enthalpy in = enthalpy out + heat released/absorbed.
    /// </summary>
    public class EnergyBalanceService
    {
        /// <summary>
        /// Specific heat capacity (kJ/kg·K) for common gases at constant pressure.
        /// Approximate values; could be refined to temperature-dependent correlation.
        /// </summary>
        private static double GetGasSpecificHeatKJKgK(double temperatureC, string gasMixture = "flue")
        {
            return gasMixture.ToLowerInvariant() switch
            {
                "flue" => 1.05,    // Typical flue gas (N2/O2/SO2/etc)
                "air" => 1.005,    // Dry air
                "steam" => 1.87,   // Water vapor
                _ => 1.05           // Default
            };
        }

        /// <summary>
        /// Specific heat capacity for liquid (water + dissolved pollutants).
        /// Weakly temperature-dependent; using constant for this phase.
        /// </summary>
        private const double LiquidSpecificHeatKJKgK = 4.18; // Water

        /// <summary>
        /// Calculate specific enthalpy of a gas stream relative to a reference temperature.
        /// H = m * Cp * (T - T_ref)
        /// Reference: 0 K for absolute zero (common in thermodynamics).
        /// </summary>
        public double CalculateGasSpecificEnthalpyKJKg(double temperatureC, string gasMixture = "flue")
        {
            double cp = GetGasSpecificHeatKJKgK(temperatureC, gasMixture);
            double temperatureK = temperatureC + 273.15;
            // Enthalpy from absolute zero — common reference in process engineering
            return cp * temperatureK;
        }

        /// <summary>
        /// Calculate specific enthalpy of liquid (water + dissolved substances).
        /// H = Cp * (T - T_ref), where T_ref = 0°C (liquid water reference state).
        /// </summary>
        public double CalculateLiquidSpecificEnthalpyKJKg(double temperatureC)
        {
            // Reference: liquid water @ 0°C = 0 kJ/kg (common choice)
            return LiquidSpecificHeatKJKgK * temperatureC;
        }

        /// <summary>
        /// Energy balance across a unit operation (e.g., scrubber).
        /// IN:  gas inlet enthalpy + liquid inlet enthalpy
        /// OUT: gas outlet enthalpy + liquid outlet enthalpy + heat released/absorbed
        /// CLOSED: Energy_in ≈ Energy_out (difference should be ≈ heat exchanged)
        /// </summary>
        public EnergyBalanceResult ValidateEnergyBalance(
            double gasInletMassFlowKgS,
            double gasInletTemperatureC,
            double liquidInletMassFlowKgS,
            double liquidInletTemperatureC,
            double gasOutletMassFlowKgS,
            double gasOutletTemperatureC,
            double liquidOutletMassFlowKgS,
            double liquidOutletTemperatureC,
            double heatReleasedKW,
            string gasMixture = "flue")
        {
            // Specific enthalpies (kJ/kg)
            double hGasIn = CalculateGasSpecificEnthalpyKJKg(gasInletTemperatureC, gasMixture);
            double hLiquidIn = CalculateLiquidSpecificEnthalpyKJKg(liquidInletTemperatureC);

            double hGasOut = CalculateGasSpecificEnthalpyKJKg(gasOutletTemperatureC, gasMixture);
            double hLiquidOut = CalculateLiquidSpecificEnthalpyKJKg(liquidOutletTemperatureC);

            // Total enthalpy (kW = kg/s * kJ/kg / 1 s)
            double enthalpyInKW = gasInletMassFlowKgS * hGasIn + liquidInletMassFlowKgS * hLiquidIn;
            double enthalpyOutKW = gasOutletMassFlowKgS * hGasOut + liquidOutletMassFlowKgS * hLiquidOut;

            // Energy balance: Heat absorbed by liquid = enthalpy increase
            // The scrubber releases heat from absorption (exothermic) into the liquid
            // ΔH_system = H_out - H_in = -Q (negative = heat leaves gas/liquid system)
            double enthalpyChangeKW = enthalpyOutKW - enthalpyInKW;

            // Expected: ΔH ≈ -Q (energy released as heat)
            // Residual: how close is the balance?
            double residualKW = enthalpyChangeKW + heatReleasedKW;
            double residualPercent = Math.Abs(heatReleasedKW) > 1e-9
                ? 100.0 * Math.Abs(residualKW) / heatReleasedKW
                : 0.0;

            return new EnergyBalanceResult
            {
                EnthalpyInletKW = enthalpyInKW,
                EnthalpyOutletKW = enthalpyOutKW,
                EnthalpyChangeKW = enthalpyChangeKW,
                HeatReleasedKW = heatReleasedKW,
                ResidualKW = residualKW,
                ResidualPercent = residualPercent,
                IsClosed = Math.Abs(residualKW) < 0.1  // Within 0.1 kW tolerance
            };
        }

        /// <summary>
        /// Energy balance for a flowsheet port pair (inlet → outlet).
        /// Simplification: assumes perfect mass conservation (in = out).
        /// </summary>
        public EnergyBalanceResult ValidateFlowsheetPorts(
            Flowsheet.FlowsheetPorts inletPorts,
            Flowsheet.FlowsheetPorts outletPorts,
            double heatReleasedKW)
        {
            double gasFlowKgS = 1.0;  // Placeholder — would come from inlet gas flow
            double liquidFlowKgS = outletPorts.Liquid?.MassFlowKgS ?? 0.0;

            return ValidateEnergyBalance(
                gasFlowKgS, inletPorts.Gas.TemperatureC,
                liquidFlowKgS, inletPorts.Liquid.TemperatureC,
                gasFlowKgS, outletPorts.Gas.TemperatureC,
                liquidFlowKgS, outletPorts.Liquid.TemperatureC,
                heatReleasedKW);
        }
    }

    /// <summary>
    /// Result of energy balance validation.
    /// </summary>
    public class EnergyBalanceResult
    {
        /// <summary>Total enthalpy of inlet streams (kW).</summary>
        public double EnthalpyInletKW { get; set; }

        /// <summary>Total enthalpy of outlet streams (kW).</summary>
        public double EnthalpyOutletKW { get; set; }

        /// <summary>Change in enthalpy (outlet - inlet, kW).</summary>
        public double EnthalpyChangeKW { get; set; }

        /// <summary>Heat released (exothermic) or absorbed (endothermic) by the unit (kW).
        /// Positive = heat released into surroundings.</summary>
        public double HeatReleasedKW { get; set; }

        /// <summary>Residual: |ΔH + Q|. Should be ≈ 0 for a closed energy balance.</summary>
        public double ResidualKW { get; set; }

        /// <summary>Relative residual (%).</summary>
        public double ResidualPercent { get; set; }

        /// <summary>Energy balance is closed if residual < 0.1 kW.</summary>
        public bool IsClosed { get; set; }
    }
}