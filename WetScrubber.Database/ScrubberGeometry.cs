using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WetScrubber.Database
{
    public class ScrubberGeometry
    {
        [Key]
        public int GeometryId { get; set; }

        public int DesignId { get; set; }

        public double TowerDiameter { get; set; }
        public double TowerHeight { get; set; }
        public double PackingHeight { get; set; }
        public double PressureDrop { get; set; }
        public double RemovalEfficiency { get; set; }

        // ── Added for DesignDiagnosticsEngine ─────────────────────
        // Previously only computed in-memory (CalculationResult) and
        // discarded; the diagnostics rule table needs them again when a
        // report is generated later, so they're now persisted alongside
        // the rest of the calculated geometry.
        public double AbsorptionFactor { get; set; }
        public double ActualLGRatio { get; set; }
        public double MinLGRatio { get; set; }
        public double GasVelocity { get; set; }

        // ── Navigation ───────────────────────────────────────────
        public ScrubberDesign Design { get; set; } = null!;
    }

}