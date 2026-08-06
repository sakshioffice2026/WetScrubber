using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Flowsheet
{
    /// <summary>
    /// The liquid-side counterpart to ProcessStream — scrubbing liquid
    /// (or condensate/spray water) at a point in the flowsheet. Carries
    /// mass flow, temperature, and dissolved/absorbed pollutant loading
    /// on a mass-fraction basis (kg pollutant / kg liquid), matching
    /// RigourousTowerOdeSolver's LiquidMassFraction convention.
    ///
    /// This is what was missing from v1: liquid flow/temp used to be
    /// fixed parameters on ScrubberUnitOp rather than something that
    /// flows from an upstream unit and can be recycled. Now it's a real
    /// stream a flowsheet can wire and recirculate.
    /// </summary>
    public sealed class LiquidStream
    {
        public double MassFlowKgS { get; set; }
        public double TemperatureC { get; set; } = 25.0;

        public IReadOnlyDictionary<string, double> PollutantLoadingKgKg { get; set; }
            = new Dictionary<string, double>();

        /// <summary>Mass- and enthalpy-weighted mix of two liquid streams
        /// (e.g. fresh makeup + recirculated sump liquid at a mixing
        /// point). Zero-flow streams are handled without dividing by
        /// zero.</summary>
        public static LiquidStream Mix(LiquidStream a, LiquidStream b)
        {
            double totalFlow = a.MassFlowKgS + b.MassFlowKgS;
            if (totalFlow <= 1e-12)
                return new LiquidStream { MassFlowKgS = 0, TemperatureC = a.TemperatureC };

            double mixedTempC = (a.MassFlowKgS * a.TemperatureC + b.MassFlowKgS * b.TemperatureC) / totalFlow;

            var codes = a.PollutantLoadingKgKg.Keys.Union(b.PollutantLoadingKgKg.Keys);
            var blended = new Dictionary<string, double>();
            foreach (var code in codes)
            {
                double la = a.PollutantLoadingKgKg.TryGetValue(code, out var va) ? va : 0.0;
                double lb = b.PollutantLoadingKgKg.TryGetValue(code, out var vb) ? vb : 0.0;
                blended[code] = (a.MassFlowKgS * la + b.MassFlowKgS * lb) / totalFlow;
            }

            return new LiquidStream { MassFlowKgS = totalFlow, TemperatureC = mixedTempC, PollutantLoadingKgKg = blended };
        }

        /// <summary>
        /// Splits fresh liquid feed into (1-recycleFraction) fresh +
        /// recycleFraction recirculated-sump-liquid, then mixes them —
        /// the physical model for "X% of scrubbing liquid is recycled
        /// blowdown instead of fresh makeup". Total mass flow stays at
        /// freshFeed's, matching a real makeup/blowdown balance. Used by
        /// both Flowsheet and FlowsheetTopologicalSolver so their
        /// recycle behavior stays consistent.
        /// </summary>
        public static LiquidStream RecycleBlend(LiquidStream freshFeed, LiquidStream recycled, double recycleFraction)
        {
            if (recycled == null || recycleFraction <= 0) return freshFeed;

            var recycledPortion = new LiquidStream
            {
                MassFlowKgS = freshFeed.MassFlowKgS * recycleFraction,
                TemperatureC = recycled.TemperatureC,
                PollutantLoadingKgKg = recycled.PollutantLoadingKgKg
            };
            var freshPortion = new LiquidStream
            {
                MassFlowKgS = freshFeed.MassFlowKgS * (1 - recycleFraction),
                TemperatureC = freshFeed.TemperatureC,
                PollutantLoadingKgKg = freshFeed.PollutantLoadingKgKg
            };

            return Mix(freshPortion, recycledPortion);
        }
    }

    /// <summary>
    /// The two-phase bundle every unit op now consumes and produces —
    /// gas stream + liquid stream at one point in the flowsheet. Replaces
    /// the old gas-only ProcessStream as IUnitOperation's port type.
    /// </summary>
    public sealed class FlowsheetPorts
    {
        public ProcessStream Gas { get; set; } = null!;
        public LiquidStream Liquid { get; set; } = new();
    }
}