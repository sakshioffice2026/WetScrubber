using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Flowsheet
{
    /// Topological ordering for flowsheets with optional tear streams (recycles).
    public sealed class FlowsheetTopologicalSolver
    {
        public sealed class UnitNode
        {
            public string Name { get; set; }
            public IUnitOperation Operation { get; set; }
            public List<string> InletConnections { get; set; } = new();  // source unit names
            public List<string> OutletConnections { get; set; } = new(); // sink unit names
            public bool IsTearSource { get; set; }
        }

        public sealed class SolveInput
        {
            public List<UnitNode> Units { get; set; } = new();
            public FlowsheetPorts FeedPorts { get; set; }
            public List<string> TearStreamNames { get; set; } = new(); // names of recycle streams
            public int MaxTearIterations { get; set; } = 15;
            public double TearConvergenceTol { get; set; } = 1e-4;

            /// <summary>Fraction of the liquid feed's mass flow that gets
            /// replaced by recirculated final-outlet liquid on each tear
            /// iteration. Only used when TearStreamNames is non-empty.</summary>
            public double LiquidRecycleFraction { get; set; } = 0.2;
        }

        public sealed class SolveOutput
        {
            public Dictionary<string, FlowsheetPorts> UnitOutlets { get; set; } = new();
            public FlowsheetPorts FinalOutlet { get; set; }
            public bool TearConverged { get; set; }
            public int TearIterations { get; set; }
        }

        public static SolveOutput Solve(SolveInput input)
        {
            var order = TopologicalSort(input.Units);
            var output = new SolveOutput();

            if (input.TearStreamNames.Count == 0)
            {
                // No recycle: single pass
                var ports = input.FeedPorts;
                foreach (var unitName in order)
                {
                    var unit = input.Units.First(u => u.Name == unitName);
                    ports = unit.Operation.Process(ports);
                    output.UnitOutlets[unitName] = ports;
                }
                output.FinalOutlet = ports;
                output.TearConverged = true;
                output.TearIterations = 1;
            }
            else
            {
                // With recycle: successive substitution on the liquid
                // stream — a fraction of the feed's liquid mass is
                // replaced by the previous pass's outlet liquid (its
                // temperature and pollutant loading intact) each round.
                LiquidStream recycledLiquid = null;

                for (int iter = 0; iter < input.MaxTearIterations; iter++)
                {
                    var liquidFeed = LiquidStream.RecycleBlend(input.FeedPorts.Liquid, recycledLiquid, input.LiquidRecycleFraction);
                    var ports = new FlowsheetPorts { Gas = input.FeedPorts.Gas, Liquid = liquidFeed };

                    foreach (var unitName in order)
                    {
                        var unit = input.Units.First(u => u.Name == unitName);
                        ports = unit.Operation.Process(ports);
                        output.UnitOutlets[unitName] = ports;
                    }

                    var newRecycledLiquid = ports.Liquid;

                    double maxShift = double.MaxValue; // force at least 2 passes before checking convergence
                    if (recycledLiquid != null)
                    {
                        maxShift = Math.Abs(newRecycledLiquid.TemperatureC - recycledLiquid.TemperatureC);
                        foreach (var kv in newRecycledLiquid.PollutantLoadingKgKg)
                        {
                            recycledLiquid.PollutantLoadingKgKg.TryGetValue(kv.Key, out var oldVal);
                            maxShift = Math.Max(maxShift, Math.Abs(kv.Value - oldVal));
                        }
                    }

                    recycledLiquid = newRecycledLiquid;
                    output.FinalOutlet = ports;
                    output.TearIterations = iter + 1;

                    if (iter > 0 && maxShift < input.TearConvergenceTol)
                    {
                        output.TearConverged = true;
                        break;
                    }
                }
            }

            return output;
        }

        private static List<string> TopologicalSort(List<UnitNode> units)
        {
            var inDegree = new Dictionary<string, int>();
            var adj = new Dictionary<string, List<string>>();

            foreach (var u in units)
            {
                inDegree[u.Name] = u.InletConnections.Count;
                adj[u.Name] = u.OutletConnections.ToList();
            }

            var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var order = new List<string>();

            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                order.Add(u);

                foreach (var v in adj[u])
                {
                    inDegree[v]--;
                    if (inDegree[v] == 0) queue.Enqueue(v);
                }
            }

            if (order.Count != units.Count)
                throw new InvalidOperationException("Flowsheet has cycles (without explicit tear stream handling)");

            return order;
        }
    }
}