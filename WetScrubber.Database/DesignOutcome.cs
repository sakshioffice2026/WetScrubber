using System;
using System.ComponentModel.DataAnnotations;
using WetScrubber.Database.Enums;

namespace WetScrubber.Database
{
    // Phase 6 — deferred (data-dependent). No correction/calibration model
    // exists yet and none is built here. This table exists purely so real
    // field-performance data starts accumulating now; a future model can be
    // trained on it once there's enough volume. Nothing in this pipeline is
    // predictive — it is a plain, deterministic data-capture form.
    public class DesignOutcome
    {
        [Key]
        public int Id { get; set; }

        public int DesignId { get; set; }

        public OutcomeDataSource Source { get; set; } = OutcomeDataSource.FieldMeasurement;

        public DateTime MeasurementDate { get; set; } = DateTime.UtcNow;

        // Predicted vs. measured — the two numbers a future calibration
        // model would eventually learn to reconcile.
        public double PredictedRemovalEfficiency { get; set; }   // copied from ScrubberGeometry at capture time

        public double MeasuredRemovalEfficiency { get; set; }

        public double? MeasuredPressureDrop { get; set; }

        public double? MeasuredGasFlowRate { get; set; }

        public double? MeasuredLiquidToGasRatio { get; set; }

        [MaxLength(1000)]
        public string? FieldNotes { get; set; }

        [MaxLength(200)]
        public string? RecordedBy { get; set; }

        public int CreatedByUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}