using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // MASTER catalog of scrubbing liquids / reagents (managed from Chemistry page).
    // Ids 1..5 match the values already stored in scrubbingliquidspecs.LiquidType.
    //
    // NOTE: delete the enum WetScrubber.Database.Enums.ScrubbingLiquid — this class
    // name would otherwise collide with it.
    public class ScrubbingLiquid
    {
        [Key]
        public int Id { get; set; }

        public string Code { get; set; } = "";            // "NaOH"
        public string DisplayName { get; set; } = "";      // "Caustic Soda"
        public string Formula { get; set; } = "";          // "NaOH"

        // Used by the calculation for reagent dosing.
        public double ReagentMolecularWeight { get; set; }

        public double DefaultDensity { get; set; } = 1000;
        public double DefaultPH { get; set; } = 7;

        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;

        public int? CreatedByUserId { get; set; }          // stamped from Session
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
