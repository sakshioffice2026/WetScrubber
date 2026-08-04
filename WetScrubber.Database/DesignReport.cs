using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using WetScrubber.Database.Enums;

namespace WetScrubber.Database
{
    /// <summary>
    /// Stores the narrative report for a scrubber design.
    ///
    /// IMPORTANT:
    /// Numerical engineering results are NEVER stored here as authoritative
    /// calculations. They always come from the deterministic calculation engine.
    ///
    /// This entity only stores narrative text surrounding those numbers.
    /// </summary>
    public class DesignReport
    {
        [Key]
        public int ReportId { get; set; }

        [Required]
        public int DesignId { get; set; }

        /// <summary>
        /// Draft / Approved / Rejected
        /// </summary>
        public ReportStatus Status { get; set; }
            = ReportStatus.Draft;

        /// <summary>
        /// TemplateOnly or AiDrafted
        /// </summary>
        public NarrativeSource NarrativeSource { get; set; }
            = NarrativeSource.TemplateOnly;

        /// <summary>
        /// Engineering template generated from deterministic calculations.
        /// Never modified by AI.
        /// </summary>
        [Required]
        public string TemplateNarrative { get; set; }
            = string.Empty;

        /// <summary>
        /// AI generated narrative.
        /// Optional.
        /// </summary>
        public string? AiNarrative { get; set; }

        /// <summary>
        /// Final narrative after engineer review.
        /// This is what gets exported.
        /// </summary>
        public string? ApprovedNarrative { get; set; }

        [MaxLength(1000)]
        public string? ReviewerComments { get; set; }

        public int? ReviewedByUserId { get; set; }

        public DateTime? ReviewedAt { get; set; }

        public DateTime CreatedAt { get; set; }
            = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; }
            = DateTime.UtcNow;

        [Timestamp]
        public byte[]? RowVersion { get; set; }

        [ForeignKey(nameof(DesignId))]
        public virtual ScrubberDesign Design { get; set; } = null!;
    }
}