namespace WetScrubber.Models
{
    public class PollutantResult
    {
        public int PollutantType { get; set; }

        public bool PhysicallyDerivedRating { get; set; }

        public double RatedOutletConcentrationPpm { get; set; }

        public double InletConcentrationPpm { get; set; }

        public double RemovalEfficiency { get; set; }
    }
}