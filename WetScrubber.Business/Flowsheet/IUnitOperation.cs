using System.Collections.Generic;

namespace WetScrubber.Business.Flowsheet
{
    /// <summary>
    /// A gas stream at a point in the flowsheet — the shared-stream
    /// contract Phase 4 chains unit ops through. Gas-side only for v1;
    /// liquid-stream stitching (blowdown, makeup) is the natural next
    /// increment once this skeleton is proven out.
    /// </summary>
    public sealed class ProcessStream
    {
        public double ActualFlowM3Hr { get; set; }
        public double TemperatureC { get; set; }
        public double PressurePa { get; set; }

        /// <summary>Pollutant loading by species code, ppm(v) basis —
        /// same convention CalculationEngine already uses throughout.</summary>
        public IReadOnlyDictionary<string, double> PollutantPpmByCode { get; set; }
            = new Dictionary<string, double>();
    }

    /// <summary>
    /// One node in the flowsheet chain (pre-cooler, scrubber, mist
    /// eliminator...). Pure transform: inlet ports in, outlet ports
    /// out — no shared mutable state between calls, so a unit op is
    /// safe to re-invoke across recycle iterations. Takes both gas and
    /// liquid streams (FlowsheetPorts) so liquid-side recycle (e.g.
    /// scrubbing liquid recirculation) is a real wired connection
    /// instead of a fixed per-unit parameter.
    /// </summary>
    public interface IUnitOperation
    {
        string Name { get; }
        FlowsheetPorts Process(FlowsheetPorts inlet);
    }
}