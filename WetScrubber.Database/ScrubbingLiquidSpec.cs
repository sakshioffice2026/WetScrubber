using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WetScrubber.Database
{
    public class ScrubbingLiquidSpec
    {
        [Key]
        public int LiquidSpecId { get; set; }

        public int DesignId { get; set; }

        // Was: LiquidType (enum). Now a plain int key holding ScrubbingLiquid.Id.
        // Same column name "LiquidType", so existing rows and queries are unaffected.
        public int LiquidType { get; set; } = 1;

        public double Concentration { get; set; }
        public double pH { get; set; } = 7;
        public double Temperature { get; set; } = 25;
        public double Density { get; set; } = 1000;
        public double Viscosity { get; set; } = 1;
        public double LiquidToGasRatio { get; set; } = 2;

        [ForeignKey(nameof(LiquidType))]
        public virtual ScrubbingLiquid? ScrubbingLiquid { get; set; }

        // Design navigation kept (already present, works via DesignId).
        public ScrubberDesign Design { get; set; } = null!;
    }
}
