using System.Collections.Generic;
using System.Linq;
using WetScrubber.Database;
using WetScrubber.Database.Enums;


namespace WetScrubber.Business.Diagnostics
{
    /// <summary>
    /// Maps a loaded ScrubberDesign onto DesignMetrics and runs it through
    /// IDesignDiagnosticsEngine. This is the single place that mapping
    /// happens — Report, DesignDetail, Edit, and Compare all call this
    /// instead of each re-deriving DesignMetrics slightly differently,
    /// which would risk the same design showing different findings on
    /// different screens.
    ///
    /// Requires design.Geometry and design.GasStream to be loaded
    /// (.Include(d => d.Geometry), .Include(d => d.GasStream).ThenInclude
    /// (g => g.Pollutants)) — returns an empty list rather than throwing
    /// if either is missing, since "no calculation run yet" is a normal
    /// state for DesignDetail/Edit (unlike Report, which requires a
    /// completed calculation before it can be generated at all).
    /// </summary>
    public static class DesignDiagnosticsMapper
    {
        public static IReadOnlyList<DesignFinding> Evaluate(
            ScrubberDesign design,
            IDesignDiagnosticsEngine engine)
        {
            if (design.Geometry == null)
                return new List<DesignFinding>();

            var geometry = design.Geometry;
            var pollutant = design.GasStream?.Pollutants?.FirstOrDefault();

            var metrics = new DesignMetrics
            {
                ScrubberType = ToScrubberTypeLabel(design.ScrubberType),
                AbsorptionFactor = geometry.AbsorptionFactor,
                ActualLGRatio = geometry.ActualLGRatio,
                MinLGRatio = geometry.MinLGRatio,
                PressureDrop = geometry.PressureDrop,
                GasVelocity = geometry.GasVelocity,
                PackingHeight = geometry.PackingHeight,
                RemovalEfficiency = geometry.RemovalEfficiency,
                TargetRemovalEfficiency = pollutant?.TargetRemovalEfficiency
            };

            return engine.Evaluate(metrics);
        }

        // Kept identical to TemplateNarrativeBuilder.ToScrubberTypeLabel —
        // if you ever change one, change both, or better, delete that
        // private copy in TemplateNarrativeBuilder and call this one.
        private static string ToScrubberTypeLabel(ScrubberType type) => type switch
        {
            ScrubberType.PackedTower => "Packed Tower",
            ScrubberType.VenturiScrubber => "Venturi Scrubber",
            ScrubberType.SprayTower => "Spray Tower",
            _ => type.ToString()
        };
    }
}