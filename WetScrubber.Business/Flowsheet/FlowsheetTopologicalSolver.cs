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
                // With recycle: Wegstein-accelerated successive
                // substitution on the liquid tear stream. Plain
                // successive substitution (recycledLiquid = whatever
                // came out of the last pass, unmodified) converges
                // slowly — sometimes very slowly — whenever the
                // recycle loop is tightly coupled (strong feedback
                // between outlet temperature/loading and what gets fed
                // back in). Wegstein (1958) extrapolates a better next
                // guess from the last two (guess, result) pairs instead
                // of just taking the latest result outright; see
                // WegsteinScalar below for the per-variable math.
                //
                // "guess" is the tear-stream estimate fed into
                // RecycleBlend this pass; "computed" is what the
                // flowsheet actually produced from that guess. Their
                // difference is the fixed-point residual used for both
                // the convergence check and the acceleration itself.
                LiquidStream guess = null;
                LiquidStream prevGuess = null;
                LiquidStream prevComputed = null;

                for (int iter = 0; iter < input.MaxTearIterations; iter++)
                {
                    var liquidFeed = LiquidStream.RecycleBlend(input.FeedPorts.Liquid, guess, input.LiquidRecycleFraction);
                    var ports = new FlowsheetPorts { Gas = input.FeedPorts.Gas, Liquid = liquidFeed };

                    foreach (var unitName in order)
                    {
                        var unit = input.Units.First(u => u.Name == unitName);
                        ports = unit.Operation.Process(ports);
                        output.UnitOutlets[unitName] = ports;
                    }

                    var computed = ports.Liquid;

                    // Fixed-point residual: how far the guess that went
                    // in differs from what came out. First pass has no
                    // guess yet (guess == null, pure fresh feed), so
                    // there's nothing to compare — force at least one
                    // more pass before checking, same as before.
                    double maxShift = double.MaxValue;
                    if (guess != null)
                    {
                        maxShift = Math.Abs(computed.TemperatureC - guess.TemperatureC);
                        foreach (var kv in computed.PollutantLoadingKgKg)
                        {
                            guess.PollutantLoadingKgKg.TryGetValue(kv.Key, out var oldVal);
                            maxShift = Math.Max(maxShift, Math.Abs(kv.Value - oldVal));
                        }
                    }

                    output.FinalOutlet = ports;
                    output.TearIterations = iter + 1;

                    if (guess != null && maxShift < input.TearConvergenceTol)
                    {
                        output.TearConverged = true;
                        break;
                    }

                    // Need two full (guess, computed) pairs before
                    // there's a slope to extrapolate from — the first
                    // usable pass (guess == null) and the second
                    // (prevGuess == null) both fall back to plain
                    // substitution, exactly matching pre-Wegstein
                    // behavior until there's real history to work with.
                    LiquidStream nextGuess = (guess != null && prevGuess != null)
                        ? WegsteinAccelerateLiquid(prevGuess, prevComputed, guess, computed)
                        : computed;

                    prevGuess = guess;
                    prevComputed = computed;
                    guess = nextGuess;
                }
            }

            return output;
        }

        /// <summary>
        /// Wegstein-accelerated next guess for the recycle liquid tear
        /// stream, applied independently to TemperatureC and each
        /// PollutantLoadingKgKg entry (mass flow isn't a tear variable —
        /// RecycleBlend always fixes total flow at the fresh feed's,
        /// see LiquidStream.RecycleBlend — so it's carried straight
        /// through from the latest computed value).
        /// </summary>
        private static LiquidStream WegsteinAccelerateLiquid(
            LiquidStream prevGuess, LiquidStream prevComputed,
            LiquidStream currGuess, LiquidStream currComputed)
        {
            double temperatureC = WegsteinScalar(
                prevGuess.TemperatureC, prevComputed.TemperatureC,
                currGuess.TemperatureC, currComputed.TemperatureC);

            var loading = new Dictionary<string, double>();
            var codes = currComputed.PollutantLoadingKgKg.Keys
                .Union(currGuess.PollutantLoadingKgKg.Keys);

            foreach (var code in codes)
            {
                double xPrev = prevGuess.PollutantLoadingKgKg.TryGetValue(code, out var pg) ? pg : 0.0;
                double yPrev = prevComputed.PollutantLoadingKgKg.TryGetValue(code, out var pc) ? pc : 0.0;
                double xCurr = currGuess.PollutantLoadingKgKg.TryGetValue(code, out var cg) ? cg : 0.0;
                double yCurr = currComputed.PollutantLoadingKgKg.TryGetValue(code, out var cc) ? cc : 0.0;

                loading[code] = Math.Max(WegsteinScalar(xPrev, yPrev, xCurr, yCurr), 0.0);
            }

            return new LiquidStream
            {
                MassFlowKgS = currComputed.MassFlowKgS,
                TemperatureC = temperatureC,
                PollutantLoadingKgKg = loading
            };
        }

        /// <summary>
        /// Single-variable Wegstein extrapolation (Wegstein, 1958):
        /// treats one round of successive substitution as a secant step
        /// on the fixed-point residual g(x) = f(x) - x, using the last
        /// two (guess x, result y = f(x)) pairs to estimate the local
        /// slope s = (yCurr-yPrev)/(xCurr-xPrev), then extrapolates
        ///
        ///   xNext = q*xCurr + (1-q)*yCurr,   q = s/(s-1)
        ///
        /// instead of plain substitution's xNext = yCurr (q = 0). q is
        /// clamped to [-5, 0] — the standard bound process simulators
        /// use to keep the extrapolation a damped correction rather
        /// than letting a near-1 slope send q to +-infinity and blow up
        /// the next guess.
        /// </summary>
        private static double WegsteinScalar(double xPrev, double yPrev, double xCurr, double yCurr)
        {
            double dx = xCurr - xPrev;
            if (Math.Abs(dx) < 1e-12)
                return yCurr; // guess didn't move between passes — nothing to extrapolate from

            double slope = (yCurr - yPrev) / dx;
            if (Math.Abs(slope - 1.0) < 1e-9)
                return yCurr; // s=1 makes q blow up (0/0-adjacent) — fall back to plain substitution

            double q = slope / (slope - 1.0);
            q = Math.Max(-5.0, Math.Min(0.0, q));

            return q * xCurr + (1.0 - q) * yCurr;
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