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

        [MaxLength(30)]
        public string? PackingCode { get; set; }
        [MaxLength(100)]
        public string? PackingSizingMethod { get; set; }
        public bool IsLimestoneSlurry { get; set; }
        public double SolidsLoadingWtPercent { get; set; }
        public double SlurryApparentViscosityMPas { get; set; }

        // ── Wet cyclone results (null for other scrubber types) ──
        // Body diameter -> TowerDiameter, total height -> TowerHeight,
        // inlet velocity -> GasVelocity, L/G -> ActualLGRatio,
        // overall efficiency -> RemovalEfficiency, pressure drop -> PressureDrop.

        // Impingement plate calculated results
        public int? ImpingementPlateStages { get; set; }
        public double? ImpingementPlateActiveAreaM2 { get; set; }
        public double? ImpingementPlateGasVelocityMPerS { get; set; }
        public double? ImpingementPlateFloodingVelocityMPerS { get; set; }
        public double? ImpingementPlatePercentFlood { get; set; }
        public double? ImpingementPlatePressureDropPa { get; set; }
        public double? ImpingementPlateRemovalEfficiencyPercent { get; set; }
        public double? ImpingementPlateCutDiameterMicron { get; set; }
        public double? CycloneCutDiameterMicron { get; set; }
        public int? CycloneParallelUnits { get; set; }
        public double? CycloneInletAreaM2 { get; set; }
        public double? CycloneGasOutletDiameterM { get; set; }
        public double? CycloneEffectiveTurns { get; set; }
        public double? CycloneFanPowerKw { get; set; }
        public double? CyclonePumpPowerKw { get; set; }
        public double? CycloneWaterRateLMin { get; set; }

        // ── Navigation ───────────────────────────────────────────
        public ScrubberDesign Design { get; set; } = null!;
    }

}