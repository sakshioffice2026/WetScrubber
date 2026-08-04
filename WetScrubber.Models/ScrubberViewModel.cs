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

        // ── Step 5: Construction materials ───────────────────────
        [Display(Name = "Shell Material")]
        public ConstructionMaterial ShellMaterial { get; set; } = ConstructionMaterial.FRP;

        [Display(Name = "Internal Material")]
        public ConstructionMaterial InternalMaterial { get; set; } = ConstructionMaterial.PP;
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
    }
}
