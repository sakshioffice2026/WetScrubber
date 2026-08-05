using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Conservation
{
    /// <summary>
    /// A phase composition (gas y_i or liquid x_i) as component-code ->
    /// mole-fraction pairs, with Σx_i = 1 enforced at construction rather
    /// than trusted from callers. Replaces the ad-hoc "single pollutant +
    /// implicit air/water balance" pattern GasMixtureBuilder /
    /// LiquidActivityBuilder use today — this is the Phase 3 type those
    /// two will eventually build on top of, once multi-pollutant streams
    /// exist upstream (PollutantInputViewModel is still single-row).
    ///
    /// Deliberately a thin, allocation-light wrapper: normalization
    /// happens once at construction, not on every read.
    /// </summary>
    public sealed class Composition
    {
        private readonly IReadOnlyDictionary<string, double> _moleFractions;

        public IReadOnlyDictionary<string, double> MoleFractions => _moleFractions;

        private Composition(IReadOnlyDictionary<string, double> moleFractions)
        {
            _moleFractions = moleFractions;
        }

        /// <summary>
        /// Builds a Composition from raw (code, moles-or-fraction) pairs.
        /// Values do not need to already sum to 1 — they are normalized
        /// here (e.g. pass in molar flow rates directly). Throws rather
        /// than silently producing a nonsense composition on bad input,
        /// same "fail loud on a real data problem" stance as
        /// PengRobinsonEos's mole-fraction-sum check.
        /// </summary>
        public static Composition FromValues(IEnumerable<(string Code, double Value)> values)
        {
            var list = values?.ToList() ?? throw new ArgumentNullException(nameof(values));

            if (list.Count == 0)
                throw new ArgumentException("Composition requires at least one component.", nameof(values));

            if (list.Any(v => v.Value < 0))
                throw new ArgumentException("Composition values cannot be negative.", nameof(values));

            if (list.Select(v => v.Code).Distinct().Count() != list.Count)
                throw new ArgumentException("Duplicate component code in composition input.", nameof(values));

            double total = list.Sum(v => v.Value);
            if (total <= 0)
                throw new ArgumentException("Composition values must sum to a positive total.", nameof(values));

            var normalized = list.ToDictionary(v => v.Code, v => v.Value / total);
            return new Composition(normalized);
        }

        /// <summary>
        /// Asserts Σx_i = 1 within tolerance. FromValues already guarantees
        /// this by construction, so this is for compositions built by
        /// hand elsewhere (e.g. an iterative solver adjusting fractions
        /// between layers, Phase 3's next piece) to self-check before
        /// handing off to PengRobinsonEos / NrtlActivityModel, which both
        /// already do their own Σ≈1 check but give a less specific error.
        /// </summary>
        public void Validate(double tolerance = 0.001)
        {
            double sum = _moleFractions.Values.Sum();
            if (Math.Abs(sum - 1.0) > tolerance)
                throw new InvalidOperationException(
                    $"Composition does not sum to 1.0 (got {sum:F6}, tolerance {tolerance}).");
        }

        public double MoleFractionOf(string code)
            => _moleFractions.TryGetValue(code, out var x) ? x : 0.0;

        public bool Contains(string code) => _moleFractions.ContainsKey(code);
    }
}