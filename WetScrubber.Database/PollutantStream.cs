using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    public class PollutantStream
    {
        [Key]
        public int PollutantId { get; set; }        // stream-row PK (unchanged)

        public int GasStreamId { get; set; }

        // Was: PollutantType (enum). Now a plain int key holding Pollutant.Id.
        // Same column name "PollutantType", so existing rows and queries are unaffected.
        public int PollutantType { get; set; } = 1;

        public double InletConcentration { get; set; }
        public double TargetOutletConcentration { get; set; }
        public double TargetRemovalEfficiency { get; set; } = 95;
        public double MolecularWeight { get; set; } = 64;
        public double HenrysLawConstant { get; set; }

        // GasStream navigation kept (it was already here and works via GasStreamId).
        public GasStream GasStream { get; set; } = null!;
    }
}
