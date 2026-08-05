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

        public ProcessStream Process(ProcessStream inlet)
        {
            double tInK = inlet.TemperatureC + 273.15;
            double tOutK = _outletTemperatureC + 273.15;
            double scaledFlow = inlet.ActualFlowM3Hr * (tOutK / Math.Max(tInK, 1e-6));

            return new ProcessStream
            {
                ActualFlowM3Hr = scaledFlow,
                TemperatureC = _outletTemperatureC,
                PressurePa = inlet.PressurePa,
                PollutantPpmByCode = inlet.PollutantPpmByCode
            };
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

        public ProcessStream Process(ProcessStream inlet)
        {
            return new ProcessStream
            {
                ActualFlowM3Hr = inlet.ActualFlowM3Hr,
                TemperatureC = inlet.TemperatureC,
                PressurePa = Math.Max(inlet.PressurePa - _pressureDropPa, 0.0),
                PollutantPpmByCode = inlet.PollutantPpmByCode
            };
        }
    }
}