
using WetScrubber.Database;

namespace WetScrubber.Models
{
    // ── Main dashboard ViewModel ──────────────────────────────────
    public class DashboardViewModel
    {
        // ── User info (for sidebar + welcome banner) ──────────────
        public string UserFullName  { get; set; } = string.Empty;
        public string UserInitials  { get; set; } = string.Empty;
        public string UserRole      { get; set; } = string.Empty;
        public string UserJobTitle  { get; set; } = string.Empty;
        public string UserCompany   { get; set; } = string.Empty;
        public DateTime LastLoginAt { get; set; }

        // ── KPI Cards ─────────────────────────────────────────────
        public int TotalProjects { get; set; }
        public int TotalDesigns  { get; set; }

        // ── Bar chart data (comma-separated for JS) ───────────────
        // Example: "Nov,Dec,Jan,Feb,Mar,Apr"
        public string ChartMonths { get; set; } = string.Empty;

        // Example: "3,5,4,7,6,9"
        public string ChartValues { get; set; } = string.Empty;

        // ── Scrubber type breakdown ───────────────────────────────
        public List<ScrubberTypeStat> ScrubberTypes { get; set; } = new();

        // ── Recent projects table ─────────────────────────────────
        public List<Project> RecentProjects { get; set; } = new();
    }

    // ── Scrubber type stat (for breakdown bar chart) ──────────────
    public class ScrubberTypeStat
    {
        public string TypeName { get; set; } = string.Empty;
        public int    Count    { get; set; }
    }
}
