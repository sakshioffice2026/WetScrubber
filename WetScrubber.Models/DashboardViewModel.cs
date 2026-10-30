using WetScrubber.Database;

namespace WetScrubber.Models
{
    // ── Main dashboard ViewModel ──────────────────────────────────
    public class DashboardViewModel
    {
        // ── User info (for sidebar + welcome banner) ──────────────
        public string UserFullName { get; set; } = string.Empty;
        public string UserInitials { get; set; } = string.Empty;
        public string UserRole { get; set; } = string.Empty;
        public string UserJobTitle { get; set; } = string.Empty;
        public string UserCompany { get; set; } = string.Empty;
        public DateTime LastLoginAt { get; set; }

        // ── KPI Cards ─────────────────────────────────────────────
        public int TotalProjects { get; set; }
        public int ActiveProjects { get; set; }
        public int TotalDesigns { get; set; }
        public int FlowsheetCount { get; set; }
        public int DesignsThisWeek { get; set; }
        public int DesignsLastWeek { get; set; }

        // ── Review pipeline ───────────────────────────────────────
        public int DraftCount { get; set; }
        public int UnderReviewCount { get; set; }
        public int ChangesRequestedCount { get; set; }
        public int ApprovedCount { get; set; }

        // ── Action queue ──────────────────────────────────────────
        public List<AttentionItem> Attention { get; set; } = new();

        // ── Pollutant mix (waffle) ────────────────────────────────
        public List<PollutantShare> PollutantMix { get; set; } = new();

        // ── JSON payload for client-side visuals (heatmap, envelope) ──
        public string DataJson { get; set; } = "{}";

        // ── Legacy (kept so other views/partials don't break) ─────
        public string ChartMonths { get; set; } = string.Empty;
        public string ChartValues { get; set; } = string.Empty;
        public List<ScrubberTypeStat> ScrubberTypes { get; set; } = new();

        // ── Recent projects ───────────────────────────────────────
        public List<Project> RecentProjects { get; set; } = new();
    }

    // ── Scrubber type stat ────────────────────────────────────────
    public class ScrubberTypeStat
    {
        public string TypeName { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    // ── One row in the "needs attention" queue ────────────────────
    public class AttentionItem
    {
        public int DesignId { get; set; }
        public string DesignName { get; set; } = string.Empty;
        public string ProjectNumber { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;   // changes | review | stale
        public int AgeDays { get; set; }
    }

    // ── One slice of the pollutant mix ────────────────────────────
    public class PollutantShare
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}