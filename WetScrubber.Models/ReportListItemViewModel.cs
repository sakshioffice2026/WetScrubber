using System;

namespace WetScrubber.Models
{
    public class ReportListItemViewModel
    {
        public int DesignId { get; set; }
        public string ProjectNumber { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string DesignName { get; set; } = string.Empty;
        public string ScrubberType { get; set; } = string.Empty;
        public bool IsCalculated { get; set; }
        public double RemovalEfficiency { get; set; }
        public double PressureDrop { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}