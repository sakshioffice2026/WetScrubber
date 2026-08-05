using System;

namespace WetScrubber.Business.Conservation
{
    public sealed class ThermalCouplingResult
    {
        public double LiquidTemperatureRiseK { get; set; }
        public double OutletLiquidTemperatureK { get; set; }
    }

    /// <summary>
    /// Heat released by absorption raises the liquid temperature, which
    /// then shifts the temperature-dependent Henry's constant
    /// (GetHenrysLawConstant) for the next tower section — the coupling
    /// step the roadmap's Phase 3 calls out ("fed back into the
    /// temperature-dependent Henry's constant and NRTL calc").
    ///
    ///   Q [kW]  = n_absorbed [kmol/s] * 1000 [mol/kmol] * dH_soln [kJ/mol]
    ///   dT [K]  = Q / (mdot_liquid [kg/s] * Cp_liquid [kJ/kg-K])
    ///
    /// Reuses HenrysLawData.HeatOfSolutionKJmol (already in the DB,
    /// currently unsourced/null for every pollutant — see HenrysLawData.cs)
    /// and ComponentProperty.SpecificHeatKJKgK (already populated for
    /// water) rather than adding new columns.
    ///
    /// Single-section calculator only: this is the per-layer building
    /// block for Phase 3's discretized solver, not the solver itself.
    /// A future layer-by-layer loop calls this once per slice, feeding
    /// OutletLiquidTemperatureK back in as the next slice's inlet T.
    /// </summary>
    public static class HeatOfAbsorptionModel
    {
        /// <summary>
        /// Null when heat-of-solution data isn't sourced yet — caller
        /// (the future discretized solver) must fall back to isothermal
        /// liquid temperature for that pollutant, same "missing data
        /// never breaks a design" contract as every Phase 1/2 lookup.
        /// </summary>
        public static ThermalCouplingResult? TryCalculate(
            double molesAbsorbedKmolPerS,
            double? heatOfSolutionKJmol,
            double liquidMassFlowKgS,
            double liquidSpecificHeatKJKgK,
            double inletLiquidTemperatureK)
        {
            if (heatOfSolutionKJmol == null) return null;
            if (molesAbsorbedKmolPerS < 0) return null;
            if (liquidMassFlowKgS <= 0 || liquidSpecificHeatKJKgK <= 0) return null;

            // Absorption is exothermic; ΔH_soln is stored/used as the
            // Van't Hoff sign convention (see GetVanTHoffTempCoeff), so
            // take the magnitude here — this method only ever heats the
            // liquid, never cools it.
            double heatReleasedKw = molesAbsorbedKmolPerS * 1000.0 * Math.Abs(heatOfSolutionKJmol.Value);

            double deltaT = heatReleasedKw / (liquidMassFlowKgS * liquidSpecificHeatKJKgK);

            return new ThermalCouplingResult
            {
                LiquidTemperatureRiseK = deltaT,
                OutletLiquidTemperatureK = inletLiquidTemperatureK + deltaT
            };
        }
    }
}