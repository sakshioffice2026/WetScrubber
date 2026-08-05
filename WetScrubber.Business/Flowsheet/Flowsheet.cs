using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Flowsheet
{
    public sealed class FlowsheetResult
    {
        public List<(string UnitName, ProcessStream Outlet)> StageOutlets { get; } = new();
        public ProcessStream FinalOutlet { get; set; } = null!;
        public bool RecycleConverged { get; set; } = true; // true (trivially) when there's no recycle
        public int RecycleIterations { get; set; } = 1;
    }

    /// <summary>
    /// Chains unit ops with a shared ProcessStream (pre-cooler -> scrubber
    /// -> mist eliminator, per the roadmap's Phase 4 example). Pure
    /// sequencing — no DB/IO, mirrors PackedTowerLayerSolver's "pure
    /// math, callers own the wiring" stance.
    /// </summary>
    public sealed class Flowsheet
    {
        private readonly List<IUnitOperation> _units;

        public Flowsheet(IEnumerable<IUnitOperation> units) => _units = units.ToList();

        /// <summary>Single pass, no recycle — the common case.</summary>
        public FlowsheetResult Run(ProcessStream feed)
        {
            var result = new FlowsheetResult();
            var stream = feed;
            foreach (var unit in _units)
            {
                stream = unit.Process(stream);
                result.StageOutlets.Add((unit.Name, stream));
            }
            result.FinalOutlet = stream;
            return result;
        }

        /// <summary>
        /// Tear-stream successive substitution: a fraction of the final
        /// outlet's pollutant loading is blended back into the feed
        /// before the next pass — e.g. mist-eliminator drain recirculated
        /// as pre-cooler spray water, which re-strips some pollutant back
        /// into the gas. Converges when the recycled loading stops moving
        /// between passes. This is the "even a simple successive-
        /// substitution solver" step the roadmap calls out for Phase 4.
        /// </summary>
        public FlowsheetResult RunWithRecycle(
            ProcessStream feed,
            double recycleFraction,
            int maxIterations = 15,
            double convergenceTolerance = 1e-4)
        {
            var result = new FlowsheetResult { RecycleConverged = false };
            var recycleLoadPpm = new Dictionary<string, double>(); // empty on pass 1 = no recycle yet

            for (int iter = 1; iter <= maxIterations; iter++)
            {
                result.RecycleIterations = iter;

                var mixedFeed = MixInRecycle(feed, recycleLoadPpm, recycleFraction);

                var stageOutlets = new List<(string, ProcessStream)>();
                var stream = mixedFeed;
                foreach (var unit in _units)
                {
                    stream = unit.Process(stream);
                    stageOutlets.Add((unit.Name, stream));
                }

                var newRecycleLoadPpm = stream.PollutantPpmByCode.ToDictionary(kv => kv.Key, kv => kv.Value);

                double maxShift = 0.0;
                foreach (var kv in newRecycleLoadPpm)
                {
                    recycleLoadPpm.TryGetValue(kv.Key, out var prev);
                    maxShift = Math.Max(maxShift, Math.Abs(kv.Value - prev));
                }

                recycleLoadPpm = newRecycleLoadPpm;
                result.StageOutlets.Clear();
                result.StageOutlets.AddRange(stageOutlets);
                result.FinalOutlet = stream;

                if (maxShift < convergenceTolerance)
                {
                    result.RecycleConverged = true;
                    break;
                }
            }

            return result;
        }

        private static ProcessStream MixInRecycle(
            ProcessStream feed, Dictionary<string, double> recycleLoadPpm, double recycleFraction)
        {
            if (recycleLoadPpm.Count == 0 || recycleFraction <= 0) return feed;

            var blended = new Dictionary<string, double>();
            foreach (var kv in feed.PollutantPpmByCode)
            {
                double recycled = recycleLoadPpm.TryGetValue(kv.Key, out var r) ? r : 0.0;
                blended[kv.Key] = kv.Value * (1 - recycleFraction) + recycled * recycleFraction;
            }

            return new ProcessStream
            {
                ActualFlowM3Hr = feed.ActualFlowM3Hr,
                TemperatureC = feed.TemperatureC,
                PressurePa = feed.PressurePa,
                PollutantPpmByCode = blended
            };
        }
    }
}