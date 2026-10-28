using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Scrubbing
{
    /// <summary>
    /// Selects the governing pollutant from already-computed per-pollutant
    /// design requirements. The caller must calculate each pollutant's required
    /// height/NTU using its own properties and target before invoking this selector.
    /// </summary>
    public static class GoverningPollutantSelector
    {
        public sealed class PollutantRequirement
        {
            public string PollutantCode { get; init; } = string.Empty;
            public double InletPpm { get; init; }
            public double TargetOutletPpm { get; init; }
            public double RequiredHeightM { get; init; }
            public double RequiredNtu { get; init; }
            public double AchievedOutletPpm { get; init; }
        }

        public sealed class SelectionResult
        {
            public PollutantRequirement GoverningPollutant { get; init; } = new();
            public double RequiredHeightM { get; init; }
            public double RequiredNtu { get; init; }
            public bool AllTargetsMetAtSelectedHeight { get; init; }
            public IReadOnlyList<PollutantRequirement> Pollutants { get; init; } = Array.Empty<PollutantRequirement>();
            public IReadOnlyList<string> TargetFailures { get; init; } = Array.Empty<string>();
        }

        public static SelectionResult Select(
            IEnumerable<PollutantRequirement> requirements,
            double selectedTowerHeightM)
        {
            if (requirements == null)
                throw new ArgumentNullException(nameof(requirements));
            if (!double.IsFinite(selectedTowerHeightM) || selectedTowerHeightM <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(selectedTowerHeightM));

            var items = requirements.ToList();
            if (items.Count == 0)
                throw new ArgumentException("At least one pollutant requirement is required.", nameof(requirements));

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.PollutantCode))
                    throw new ArgumentException("Every pollutant requirement must have a pollutant code.", nameof(requirements));
                RequireFiniteNonNegative(item.InletPpm, nameof(item.InletPpm));
                RequireFiniteNonNegative(item.TargetOutletPpm, nameof(item.TargetOutletPpm));
                RequireFiniteNonNegative(item.RequiredHeightM, nameof(item.RequiredHeightM));
                RequireFiniteNonNegative(item.RequiredNtu, nameof(item.RequiredNtu));
                RequireFiniteNonNegative(item.AchievedOutletPpm, nameof(item.AchievedOutletPpm));
                if (item.TargetOutletPpm > item.InletPpm)
                    throw new ArgumentException($"Target outlet for '{item.PollutantCode}' exceeds its inlet concentration.", nameof(requirements));
            }

            var governing = items.OrderByDescending(x => x.RequiredHeightM).First();
            var failures = items
                .Where(x => x.AchievedOutletPpm > x.TargetOutletPpm * 1.005 + 1e-12)
                .Select(x => x.PollutantCode)
                .ToArray();

            return new SelectionResult
            {
                GoverningPollutant = governing,
                RequiredHeightM = governing.RequiredHeightM,
                RequiredNtu = items.OrderByDescending(x => x.RequiredNtu).First().RequiredNtu,
                AllTargetsMetAtSelectedHeight = failures.Length == 0 && selectedTowerHeightM >= governing.RequiredHeightM,
                Pollutants = items.AsReadOnly(),
                TargetFailures = failures
            };
        }

        private static void RequireFiniteNonNegative(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name, value, "Value must be finite and non-negative.");
        }
    }
}
