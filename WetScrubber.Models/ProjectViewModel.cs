using System.ComponentModel.DataAnnotations;
using WetScrubber.Database.Enums;


namespace WetScrubber.Models
{
    // ============================================================
    //  PROJECT LIST VIEW MODEL
    // ============================================================
    public class ProjectListViewModel
    {
        public List<ProjectSummaryViewModel> Projects { get; set; } = new();

        // Stats for top cards
        public int TotalProjects    { get; set; }
        public int ActiveProjects   { get; set; }
        public int CompletedProjects { get; set; }
        public int TotalDesigns     { get; set; }

        // Search / filter state (to keep form values on postback)
        public string? SearchTerm   { get; set; }
        public string? StatusFilter { get; set; }
    }

    // ============================================================
    //  PROJECT SUMMARY  (one row in list table)
    // ============================================================
    public class ProjectSummaryViewModel
    {
        public int    ProjectId     { get; set; }
        public string ProjectNumber { get; set; } = string.Empty;
        public string ProjectName   { get; set; } = string.Empty;
        public string ClientName    { get; set; } = string.Empty;
        public string EngineerName  { get; set; } = string.Empty;
        public ProjectStatus Status { get; set; }
        public UnitSystem UnitSystem { get; set; }
        public int    DesignCount   { get; set; }
        public DateTime CreatedAt   { get; set; }
        public DateTime UpdatedAt   { get; set; }
    }

    // ============================================================
    //  CREATE PROJECT VIEW MODEL
    // ============================================================
    public class CreateProjectViewModel
    {
        [Required(ErrorMessage = "Project number is required")]
        [MaxLength(50, ErrorMessage = "Max 50 characters")]
        [Display(Name = "Project Number")]
        public string ProjectNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Project name is required")]
        [MaxLength(200, ErrorMessage = "Max 200 characters")]
        [Display(Name = "Project Name")]
        public string ProjectName { get; set; } = string.Empty;

        [MaxLength(200)]
        [Display(Name = "Client Name")]
        public string ClientName { get; set; } = string.Empty;

        [MaxLength(100)]
        [Display(Name = "Engineer Name")]
        public string EngineerName { get; set; } = string.Empty;

        [MaxLength(1000)]
        [Display(Name = "Description")]
        public string Description { get; set; } = string.Empty;

        [Display(Name = "Unit System")]
        public UnitSystem UnitSystem { get; set; } = UnitSystem.Metric;
    }
    // ============================================================
    //  EDIT PROJECT VIEW MODEL
    // ============================================================
    public class EditProjectViewModel
    {
        public int ProjectId { get; set; }

        [Required(ErrorMessage = "Project number is required")]
        [MaxLength(50)]
        [Display(Name = "Project Number")]
        public string ProjectNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Project name is required")]
        [MaxLength(200)]
        [Display(Name = "Project Name")]
        public string ProjectName { get; set; } = string.Empty;

        [MaxLength(200)]
        [Display(Name = "Client Name")]
        public string ClientName { get; set; } = string.Empty;

        [MaxLength(100)]
        [Display(Name = "Engineer Name")]
        public string EngineerName { get; set; } = string.Empty;

        [MaxLength(1000)]
        [Display(Name = "Description")]
        public string Description { get; set; } = string.Empty;

        [Display(Name = "Unit System")]
        public UnitSystem UnitSystem { get; set; } = UnitSystem.Metric;

        [Display(Name = "Status")]
        public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    }

    // ============================================================
    //  PROJECT DETAIL VIEW MODEL
    // ============================================================
    public class ProjectDetailViewModel
    {
        public int    ProjectId     { get; set; }
        public string ProjectNumber { get; set; } = string.Empty;
        public string ProjectName   { get; set; } = string.Empty;
        public string ClientName    { get; set; } = string.Empty;
        public string EngineerName  { get; set; } = string.Empty;
        public string Description   { get; set; } = string.Empty;
        public ProjectStatus Status { get; set; }
        public UnitSystem UnitSystem { get; set; }
        public DateTime CreatedAt   { get; set; }
        public DateTime UpdatedAt   { get; set; }

        // Designs under this project
        public List<DesignSummaryViewModel> Designs { get; set; } = new();

        // Stats
        public int TotalDesigns     { get; set; }
        public int CompletedDesigns { get; set; }
    }

    // ============================================================
    //  DESIGN SUMMARY  (one row inside project detail)
    // ============================================================
    public class DesignSummaryViewModel
    {
        public int    DesignId       { get; set; }
        public string DesignName     { get; set; } = string.Empty;
        public string ScrubberType   { get; set; } = string.Empty;
        public string ShellMaterial  { get; set; } = string.Empty;
        public bool   HasResults     { get; set; }
        public double RemovalEfficiency { get; set; }
        public DateTime CreatedAt    { get; set; }
        public DateTime UpdatedAt    { get; set; }
    }
}
