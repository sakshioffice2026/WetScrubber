using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Diagnostics
{
    public enum FindingSeverity
    {
        Info,
        Warning,
        Critical
    }

    /// <summary>
    /// One row of the doctor's "diagnosis + recommendation" — always the
    /// output of a deterministic rule, never something an LLM invented.
    /// </summary>
    public sealed class DesignFinding
    {
        public string Code { get; init; } = string.Empty;
        public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
        public string Symptom { get; init; } = string.Empty;
        public string Diagnosis { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;

        /// <summary>
        /// Names of the input fields on CreateDesignViewModel /
        /// EditDesignViewModel this finding is actually about (e.g.
        /// "LiquidToGasRatio"). Lets the Edit form highlight, scroll to,
        /// and offer "fill suggested value" on the exact field a
        /// non-expert would otherwise have to guess at from prose alone.
        /// Empty when a finding isn't about a specific input (e.g. the
        /// "recalculate, this data is stale" finding).
        /// </summary>
        public IReadOnlyList<string> AffectedFields { get; init; } = Array.Empty<string>();

        /// <summary>
        /// A concrete target value for AffectedFields[0], when the engine
        /// can compute one from numbers it already has (e.g. "raise L/G
        /// to at least the value that clears the minimum-margin check").
        /// Null when only a direction ("increase X") is knowable, not a
        /// magnitude — never guessed, only ever set from a real
        /// calculation.
        /// </summary>
        public double? SuggestedValue { get; init; }

        /// <summary>
        /// Human-readable version of SuggestedValue, with the comparison
        /// and units spelled out (e.g. "≥ 3.85 L/m³ gas") — the engine
        /// rounds/formats this once so every view doesn't reinvent it.
        /// </summary>
        public string? SuggestedValueLabel { get; init; }
    }
}