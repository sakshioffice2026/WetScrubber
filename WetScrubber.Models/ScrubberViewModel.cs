using System.ComponentModel.DataAnnotations;
using WetScrubber.Database;
using WetScrubber.Database.Enums;

namespace WetScrubber.Models
{
    // ============================================================
    //  CREATE DESIGN VIEW MODEL  (main form)
    // ============================================================
    public class CreateDesignViewModel
    {
        // ── Which project this design belongs to ─────────────────
        public int ProjectId { get; set; }

        public string ProjectNumber { get; set; } = string.Empty;

        public string ProjectName { get; set; } = string.Empty;

        // ── Step 1: Design name + scrubber type ──────────────────
        [Required(ErrorMessage = "Design name is required")]
        [MaxLength(200)]
        [Display(Name = "Design Name")]
        public string DesignName { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Scrubber Type")]
        public ScrubberType ScrubberType { get; set; } = ScrubberType.PackedTower;

        // Exposes the protected object.MemberwiseClone() for callers outside
        // this class (e.g. PackedTowerScrubberUnit.CloneTemplate) that need a
        // shallow copy per flowsheet/recycle pass without mutating the shared
        // template instance.
        public CreateDesignViewModel Clone() => (CreateDesignViewModel)MemberwiseClone();

        // ── Step 2: Gas stream ────────────────────────────────────
        [Required(ErrorMessage = "Normal flow rate is required")]
        [Range(1, 10000000, ErrorMessage = "Must be between 1 and 10,000,000")]
        [Display(Name = "Normal Flow Rate (Nm³/hr)")]
        public double NormalFlowRate { get; set; }

        [Required(ErrorMessage = "Actual flow rate is required")]
        [Range(1, 10000000, ErrorMessage = "Must be between 1 and 10,000,000")]
        [Display(Name = "Actual Flow Rate (m³/hr)")]
        public double ActualFlowRate { get; set; }

        [Required(ErrorMessage = "Inlet temperature is required")]
        [Range(-50, 1500, ErrorMessage = "Must be between -50 and 1500 °C")]
        [Display(Name = "Inlet Temperature (°C)")]
        public double InletTemperature { get; set; }

        [Range(50000, 300000, ErrorMessage = "Must be between 50,000 and 300,000 Pa")]
        [Display(Name = "Inlet Pressure (Pa)")]
        public double InletPressure { get; set; } = 101325;

        [Range(0, 100, ErrorMessage = "Must be between 0 and 100")]
        [Display(Name = "Moisture Content (% vol)")]
        public double MoistureContent { get; set; }

        [Range(0.1, 5, ErrorMessage = "Must be between 0.1 and 5 kg/m³")]
        [Display(Name = "Gas Density (kg/m³)")]
        public double GasDensity { get; set; } = 1.2;

        [Range(0.000001, 0.001, ErrorMessage = "Typical range: 0.000010 to 0.000050 Pa·s")]
        [Display(Name = "Gas Viscosity (Pa·s)")]
        public double GasViscosity { get; set; } = 0.0000185;

        // ── Step 3: Pollutants (dynamic list) ────────────────────
        public List<PollutantInputViewModel> Pollutants { get; set; } = new()
        {
            new PollutantInputViewModel()   // start with one row
        };

        // ── Step 4: Scrubbing liquid ──────────────────────────────
        [Display(Name = "Liquid Type")]
        public int LiquidType { get; set; } = 2;   // FK -> scrubbingliquids.Id (2 = Caustic Soda)

        // ── Master dropdown sources (populated by the controller) ──
        public List<Pollutant> PollutantOptions { get; set; } = new();
        public List<ScrubbingLiquid> LiquidOptions { get; set; } = new();
        public List<PackingMaterial> PackingOptions { get; set; } = new();

        [Range(0, 50)]
        [Display(Name = "Concentration (% wt)")]
        public double LiquidConcentration { get; set; } = 10;

        [Range(0, 14)]
        [Display(Name = "pH")]
        public double LiquidPH { get; set; } = 12;

        [Range(0, 100)]
        [Display(Name = "Temperature (°C)")]
        public double LiquidTemperature { get; set; } = 25;

        [Range(800, 2000)]
        [Display(Name = "Density (kg/m³)")]
        public double LiquidDensity { get; set; } = 1050;

        [Range(0.1, 20)]
        [Display(Name = "Viscosity (mPa·s)")]
        public double LiquidViscosity { get; set; } = 1.0;

        [Required]
        [Range(0.1, 50)]
        [Display(Name = "L/G Ratio (L/m³ gas)")]
        public double LiquidToGasRatio { get; set; } = 3.0;

        // ── Step 4b: Packing selection (Phase 5) ─────────────────
        // Joins PackingMaterial.Code. Null/blank = keep using the
        // engine's historical hardcoded Pall Ring 50mm default (see
        // ScrubberCalculationEngine.ResolvePacking) — same "unselected
        // is not an error" contract the rest of Phase 5 uses.
        [Display(Name = "Packing Type")]
        public string? PackingCode { get; set; }

        // ── Step 5: Construction materials ───────────────────────
        [Display(Name = "Shell Material")]
        public ConstructionMaterial ShellMaterial { get; set; } = ConstructionMaterial.FRP;

        [Display(Name = "Internal Material")]
        public ConstructionMaterial InternalMaterial { get; set; } = ConstructionMaterial.PP;

        // ── Diagnostics (populated by the controller from the last-run
        // calculation, empty until then) ─────────────────────────
        public List<DesignFindingViewModel> Diagnostics { get; set; } = new();
    }

    // ============================================================
    //  DESIGN FINDING VIEW MODEL
    //  Lightweight mirror of WetScrubber.Business.Diagnostics.DesignFinding
    //  — kept as a plain DTO here rather than a project reference, since
    //  WetScrubber.Models otherwise depends only on WetScrubber.Database.
    // ============================================================
    public class DesignFindingViewModel
    {
        public string Code { get; set; } = string.Empty;
        public string Severity { get; set; } = "Info";   // Info / Warning / Critical
        public string Symptom { get; set; } = string.Empty;
        public string Diagnosis { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
        public List<string> AffectedFields { get; set; } = new();
        public double? SuggestedValue { get; set; }
        public string? SuggestedValueLabel { get; set; }
    }

    // ============================================================
    //  EDIT DESIGN VIEW MODEL
    //  Reuses every field of CreateDesignViewModel and just adds
    //  the DesignId so the POST knows which design to update.
    // ============================================================
    public class EditDesignViewModel : CreateDesignViewModel
    {
        public int DesignId { get; set; }
    }

    // ============================================================
    //  POLLUTANT INPUT  (one row in pollutant table)
    // ============================================================
    public class PollutantInputViewModel
    {
        [Display(Name = "Pollutant Type")]
        public int PollutantType { get; set; } = 1;   // FK -> pollutants.Id (1 = SO2)

        [Required]
        [Range(0.1, 1000000)]
        [Display(Name = "Inlet Concentration (mg/Nm³)")]
        public double InletConcentration { get; set; } = 1000;

        [Required]
        [Range(0, 1000000)]
        [Display(Name = "Target Outlet (mg/Nm³)")]
        public double TargetOutletConcentration { get; set; } = 50;

        [Required]
        [Range(1, 99.99)]
        [Display(Name = "Target Removal (%)")]
        public double TargetRemovalEfficiency { get; set; } = 95;

        [Range(1, 500)]
        [Display(Name = "Molecular Weight (g/mol)")]
        public double MolecularWeight { get; set; } = 64;

        [Range(0, 100)]
        [Display(Name = "Henry's Law Constant")]
        public double HenrysLawConstant { get; set; } = 0.83;
    }

    // ============================================================
    //  DESIGN DETAIL VIEW MODEL  (view after saving)
    // ============================================================
    public class DesignDetailViewModel
    {
        public int DesignId { get; set; }
        public int ProjectId { get; set; }
        public string ProjectNumber { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string DesignName { get; set; } = string.Empty;
        public string ScrubberType { get; set; } = string.Empty;
        public string ShellMaterial { get; set; } = string.Empty;
        public string InternalMaterial { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Gas stream
        public double NormalFlowRate { get; set; }
        public double ActualFlowRate { get; set; }
        public double InletTemperature { get; set; }
        public double InletPressure { get; set; }
        public double MoistureContent { get; set; }
        public double GasDensity { get; set; }

        // Liquid
        public string LiquidType { get; set; } = string.Empty;
        public double LiquidPH { get; set; }
        public double LiquidConcentration { get; set; }
        public double LiquidToGasRatio { get; set; }

        // Pollutants
        public List<PollutantInputViewModel> Pollutants { get; set; } = new();

        // Calculated geometry (if calculation has been run)
        public bool HasResults { get; set; }
        public double TowerDiameter { get; set; }
        public double TowerHeight { get; set; }
        public double PackingHeight { get; set; }
        public double PressureDrop { get; set; }
        public double RemovalEfficiency { get; set; }

        // Diagnostics — empty until RunCalculation has produced geometry.
        public List<DesignFindingViewModel> Diagnostics { get; set; } = new();

        // Redesign lineage — lets the view offer "Compare with previous".
        public bool IsLocked { get; set; }
        public int? PreviousDesignId { get; set; }
        public int RevisionNumber { get; set; } = 1;

        // Report status — drives the shared Generate Report / Redesign /
        // Compare action bar on the Results page. HasReport is true as
        // soon as a report row exists (template-only counts); Redesign
        // and Compare stay locked behind it so there's always something
        // concrete to redesign or compare against.
        public bool HasReport { get; set; }
        public int? ReportId { get; set; }
    }

    // ============================================================
    //  DESIGN COMPARE VIEW MODEL  (Option A — old vs. redesigned)
    // ============================================================
    public class DesignCompareViewModel
    {
        public int OldDesignId { get; set; }
        public string OldDesignName { get; set; } = string.Empty;
        public int NewDesignId { get; set; }
        public string NewDesignName { get; set; } = string.Empty;
        public int NewRevisionNumber { get; set; }

        public List<CompareRowViewModel> Rows { get; set; } = new();

        // Findings still open on the new revision — the "did this
        // actually help" half of the loop.
        public List<DesignFindingViewModel> NewDiagnostics { get; set; } = new();

        // Narrative blocks, shown side by side rather than merged.
        public string? OldApprovedNarrative { get; set; }
        public string? NewAiNarrative { get; set; }
        public string? NewApprovedNarrative { get; set; }
        public string NewReportStatus { get; set; } = string.Empty;
    }

    // One field's old value, new value, and whether the change moved in
    // the direction the diagnostics on the OLD design recommended.
    public class CompareRowViewModel
    {
        public string Label { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public double OldValue { get; set; }
        public double NewValue { get; set; }
        public double Delta => NewValue - OldValue;
        public double? DeltaPercent => OldValue != 0 ? (NewValue - OldValue) / OldValue * 100.0 : null;

        // null = no diagnostic on this field to judge against;
        // true/false = did the change go the recommended direction.
        public bool? MatchesRecommendation { get; set; }
    }
}