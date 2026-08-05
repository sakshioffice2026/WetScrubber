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
            public ProcessStream FeedStream { get; set; }
            public List<string> TearStreamNames { get; set; } = new(); // names of recycle streams
            public int MaxTearIterations { get; set; } = 15;
            public double TearConvergenceTol { get; set; } = 1e-4;
        }

        public sealed class SolveOutput
        {
            public Dictionary<string, ProcessStream> UnitOutlets { get; set; } = new();
            public ProcessStream FinalOutlet { get; set; }
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
                var stream = input.FeedStream;
                foreach (var unitName in order)
                {
                    var unit = input.Units.First(u => u.Name == unitName);
                    stream = unit.Operation.Process(stream);
                    output.UnitOutlets[unitName] = stream;
                }
                output.FinalOutlet = stream;
                output.TearConverged = true;
                output.TearIterations = 1;
            }
            else
            {
                // With recycle: Wegstein/successive substitution
                var tearValues = new Dictionary<string, Dictionary<string, double>>();
                for (int iter = 0; iter < input.MaxTearIterations; iter++)
                {
                    var stream = input.FeedStream;

                    // Inject tear stream guesses if available
                    if (iter > 0 && tearValues.TryGetValue("_tear", out var prevComposition))
                    {
                        var blended = new Dictionary<string, double>(stream.PollutantPpmByCode.ToDictionary(kv => kv.Key, kv => kv.Value));
                        foreach (var (code, conc) in prevComposition)
                            if (blended.ContainsKey(code))
                                blended[code] = blended[code] * 0.8 + conc * 0.2; // damping

                        stream = new ProcessStream
                        {
                            ActualFlowM3Hr = stream.ActualFlowM3Hr,
                            TemperatureC = stream.TemperatureC,
                            PressurePa = stream.PressurePa,
                            PollutantPpmByCode = blended
                        };
                    }

                    foreach (var unitName in order)
                    {
                        var unit = input.Units.First(u => u.Name == unitName);
                        stream = unit.Operation.Process(stream);
                        output.UnitOutlets[unitName] = stream;
                    }

                    var newTearComp = stream.PollutantPpmByCode.ToDictionary(kv => kv.Key, kv => kv.Value);

                    double maxShift = 0.0;
                    if (tearValues.TryGetValue("_tear", out var oldTear))
                    {
                        foreach (var (code, newVal) in newTearComp)
                        {
                            oldTear.TryGetValue(code, out var oldVal);
                            maxShift = Math.Max(maxShift, Math.Abs(newVal - oldVal));
                        }
                    }

                    tearValues["_tear"] = newTearComp;
                    output.FinalOutlet = stream;
                    output.TearIterations = iter + 1;

                    if (maxShift < input.TearConvergenceTol)
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