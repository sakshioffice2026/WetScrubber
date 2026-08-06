using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Flowsheet
{
    public sealed class FlowsheetResult
    {
        public List<(string UnitName, FlowsheetPorts Outlet)> StageOutlets { get; } = new();
        public FlowsheetPorts FinalOutlet { get; set; } = null!;
        public bool RecycleConverged { get; set; } = true; // true (trivially) when there's no recycle
        public int RecycleIterations { get; set; } = 1;
    }

    /// <summary>
    /// Chains unit ops with a shared FlowsheetPorts (gas + liquid stream)
    /// (pre-cooler -> scrubber -> mist eliminator, per the roadmap's
    /// Phase 4 example). Pure sequencing — no DB/IO, mirrors
    /// PackedTowerLayerSolver's "pure math, callers own the wiring"
    /// stance.
    /// </summary>
    public sealed class Flowsheet
    {
        private readonly List<IUnitOperation> _units;

        public Flowsheet(IEnumerable<IUnitOperation> units) => _units = units.ToList();

        /// <summary>Single pass, no recycle — the common case.</summary>
        public FlowsheetResult Run(FlowsheetPorts feed)
        {
            var result = new FlowsheetResult();
            var ports = feed;
            foreach (var unit in _units)
            {
                ports = unit.Process(ports);
                result.StageOutlets.Add((unit.Name, ports));
            }
            result.FinalOutlet = ports;
            return result;
        }

        /// <summary>
        /// Physical liquid recycle: a fraction of the fresh liquid feed's
        /// mass flow is replaced by the previous pass's final-outlet
        /// liquid stream (its temperature and accumulated pollutant
        /// loading intact) — e.g. mist-eliminator drain / sump liquid
        /// recirculated as pre-cooler spray or scrubber liquid, which
        /// re-strips less pollutant on the next pass because it's
        /// already partially loaded. Converges when the recycled
        /// stream's temperature and loading stop moving between passes.
        /// This replaces the old gas-ppm-blend approximation with an
        /// actual liquid-stream tear.
        /// </summary>
        public FlowsheetResult RunWithRecycle(
            FlowsheetPorts feed,
            double liquidRecycleFraction,
            int maxIterations = 15,
            double convergenceTolerance = 1e-4)
        {
            var result = new FlowsheetResult { RecycleConverged = false };
            LiquidStream recycledLiquid = null;

            for (int iter = 1; iter <= maxIterations; iter++)
            {
                result.RecycleIterations = iter;

                var mixedLiquidFeed = LiquidStream.RecycleBlend(feed.Liquid, recycledLiquid, liquidRecycleFraction);
                var portsIn = new FlowsheetPorts { Gas = feed.Gas, Liquid = mixedLiquidFeed };

                var stageOutlets = new List<(string, FlowsheetPorts)>();
                var ports = portsIn;
                foreach (var unit in _units)
                {
                    ports = unit.Process(ports);
                    stageOutlets.Add((unit.Name, ports));
                }

                var newRecycledLiquid = ports.Liquid;

                double maxShift = double.MaxValue; // force at least 2 passes before checking convergence
                if (recycledLiquid != null)
                {
                    maxShift = Math.Abs(newRecycledLiquid.TemperatureC - recycledLiquid.TemperatureC);
                    foreach (var kv in newRecycledLiquid.PollutantLoadingKgKg)
                    {
                        recycledLiquid.PollutantLoadingKgKg.TryGetValue(kv.Key, out var prev);
                        maxShift = Math.Max(maxShift, Math.Abs(kv.Value - prev));
                    }
                }

                recycledLiquid = newRecycledLiquid;
                result.StageOutlets.Clear();
                result.StageOutlets.AddRange(stageOutlets);
                result.FinalOutlet = ports;

                if (iter > 1 && maxShift < convergenceTolerance)
                {
                    result.RecycleConverged = true;
                    break;
                }
            }

            return result;
        }
    }
}