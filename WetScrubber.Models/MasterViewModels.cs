using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Models
{
    public class PollutantFormViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Code is required"), StringLength(20)]
        public string Code { get; set; } = "";            // e.g. "SO2"

        [Required(ErrorMessage = "Name is required"), StringLength(100)]
        public string DisplayName { get; set; } = "";      // "Sulfur Dioxide"

        [StringLength(20)]
        public string Formula { get; set; } = "";          // "SO₂"

        [Range(0, 1000)]
        [Display(Name = "Default Molecular Weight")]
        public double DefaultMolecularWeight { get; set; }

        [Display(Name = "Default Henry's Law Constant")]
        public double DefaultHenrysLawConstant { get; set; }

        [StringLength(300)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class LiquidFormViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Code is required"), StringLength(20)]
        public string Code { get; set; } = "";            // "NaOH"

        [Required(ErrorMessage = "Name is required"), StringLength(100)]
        public string DisplayName { get; set; } = "";      // "Caustic Soda"

        [StringLength(30)]
        public string Formula { get; set; } = "";          // "NaOH"

        [Range(0, 1000)]
        [Display(Name = "Reagent Molecular Weight")]
        public double ReagentMolecularWeight { get; set; }

        [Range(0, 5000)]
        [Display(Name = "Default Density (kg/m³)")]
        public double DefaultDensity { get; set; } = 1000;

        [Range(0, 14)]
        [Display(Name = "Default pH")]
        public double DefaultPH { get; set; } = 7;

        [StringLength(300)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
