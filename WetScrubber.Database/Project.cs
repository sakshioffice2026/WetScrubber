using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WetScrubber.Database.Enums;

namespace WetScrubber.Database
{
    public class Project
    {
        public int ProjectId { get; set; }

        [Required]
        [MaxLength(50)]
        public string ProjectNumber { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string ProjectName { get; set; } = string.Empty;

        [MaxLength(200)]
        public string ClientName { get; set; } = string.Empty;

        [MaxLength(100)]
        public string EngineerName { get; set; } = string.Empty;

        [MaxLength(1000)]
        public string Description { get; set; } = string.Empty;

        public ProjectStatus Status { get; set; } = ProjectStatus.Draft;

        public UnitSystem UnitSystem { get; set; } = UnitSystem.Metric;

        public int CreatedByUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // ── Navigation ───────────────────────────────────────────
        public User CreatedBy { get; set; } = null!;

        public ICollection<ScrubberDesign> Designs { get; set; } = new List<ScrubberDesign>();
    }

}
