using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using WetScrubber.Database;

namespace WetScrubber.Models
{
    // List page: reactions grouped under pollutant tabs, with a liquid lookup for labels.
    public class ChemistryIndexViewModel
    {
        public List<Pollutant> Pollutants { get; set; } = new();          // tabs
        public List<ChemicalReaction> Reactions { get; set; } = new();    // all active
        public Dictionary<int, ScrubbingLiquid> Liquids { get; set; } = new(); // id → liquid
    }

    // Create / Edit form.
    public class ReactionFormViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Choose a pollutant")]
        public int PollutantId { get; set; }

        [Required(ErrorMessage = "Choose a scrubbing liquid")]
        public int ScrubbingLiquidId { get; set; }

        [Required(ErrorMessage = "Equation is required"), StringLength(200)]
        public string Equation { get; set; } = "";

        [StringLength(100)]
        public string ReactionType { get; set; } = "";

        [StringLength(150)]
        public string ProductName { get; set; } = "";

        [Range(0, 100, ErrorMessage = "0–100")]
        public double StoichiometricRatio { get; set; } = 1;

        [Range(0, 100, ErrorMessage = "0–100")]
        public double MaxRemovalEfficiency { get; set; } = 99;

        [Range(0, 14, ErrorMessage = "0–14")]
        public double MinOperatingPH { get; set; } = 0;

        [Range(0, 14, ErrorMessage = "0–14")]
        public double MaxOperatingPH { get; set; } = 14;

        public double? HeatOfReaction { get; set; }

        [StringLength(500)]
        public string? DesignNotes { get; set; }

        public bool IsPrimary { get; set; } = true;
        public bool IsActive { get; set; } = true;

        // Dropdown sources (populated by the controller).
        public List<Pollutant> Pollutants { get; set; } = new();
        public List<ScrubbingLiquid> Liquids { get; set; } = new();
    }
}
