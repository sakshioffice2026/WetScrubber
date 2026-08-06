using System;

namespace WetScrubber.Business.Flowsheet
{
    /// <summary>
    /// Direct-contact quench to a fixed outlet temperature. No
    /// condensation/moisture-balance model yet — actual volumetric flow
    /// is scaled ideal-gas-basis (constant P), same T&lt;-&gt;flow
    /// assumption ScrubberCalculationEngine already makes elsewhere.
    /// Pollutant loading passes through unchanged (no absorption here).
    /// </summary>
    public sealed class PreCoolerUnit : IUnitOperation
    {
        public string Name => "Pre-Cooler";

        private readonly double _outletTemperatureC;

        public PreCoolerUnit(double outletTemperatureC) => _outletTemperatureC = outletTemperatureC;

        public FlowsheetPorts Process(FlowsheetPorts inlet)
        {
            var gasIn = inlet.Gas;
            double tInK = gasIn.TemperatureC + 273.15;
            double tOutK = _outletTemperatureC + 273.15;
            double scaledFlow = gasIn.ActualFlowM3Hr * (tOutK / Math.Max(tInK, 1e-6));

            var gasOut = new ProcessStream
            {
                ActualFlowM3Hr = scaledFlow,
                TemperatureC = _outletTemperatureC,
                PressurePa = gasIn.PressurePa,
                PollutantPpmByCode = gasIn.PollutantPpmByCode
            };

            // Direct-contact quench does wet the gas, but spray-water
            // pickup isn't modeled yet — liquid passes through unchanged.
            return new FlowsheetPorts { Gas = gasOut, Liquid = inlet.Liquid };
        }
    }

    /// <summary>
    /// Pressure-drop-only device downstream of the scrubber. Droplet
    /// carryover of entrained (liquid-phase) pollutant is modeled as
    /// pass-through for v1 — a real carryover fraction needs droplet
    /// size data this codebase doesn't source yet, same
    /// unsourced-data-never-breaks-a-design stance as HenrysLawData.
    /// </summary>
    public sealed class MistEliminatorUnit : IUnitOperation
    {
        public string Name => "Mist Eliminator";

        private readonly double _pressureDropPa;

        public MistEliminatorUnit(double pressureDropPa = 250.0) => _pressureDropPa = pressureDropPa;

        public FlowsheetPorts Process(FlowsheetPorts inlet)
        {
            var gasIn = inlet.Gas;
            var gasOut = new ProcessStream
            {
                ActualFlowM3Hr = gasIn.ActualFlowM3Hr,
                TemperatureC = gasIn.TemperatureC,
                PressurePa = Math.Max(gasIn.PressurePa - _pressureDropPa, 0.0),
                PollutantPpmByCode = gasIn.PollutantPpmByCode
            };

            return new FlowsheetPorts { Gas = gasOut, Liquid = inlet.Liquid };
        }
    }
}