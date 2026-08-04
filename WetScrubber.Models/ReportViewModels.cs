using WetScrubber.Database.Enums;

namespace WetScrubber.Models
{
    /// <summary>
    /// Everything the Report/Review view needs. Purely a display shape —
    /// it doesn't recompute or reinterpret anything from DesignReport.
    /// </summary>
    public class ReportReviewViewModel
    {
        public int ReportId { get; set; }

        public int DesignId { get; set; }

        public string DesignName { get; set; } = string.Empty;

        public ReportStatus Status { get; set; }

        public NarrativeSource NarrativeSource { get; set; }

        public string TemplateNarrative { get; set; } = string.Empty;

        public string? AiNarrative { get; set; }

        public string? ApprovedNarrative { get; set; }

        public string? ReviewerComments { get; set; }

        public bool AiDraftFailed { get; set; }

        public string? AiDraftError { get; set; }
    }
}