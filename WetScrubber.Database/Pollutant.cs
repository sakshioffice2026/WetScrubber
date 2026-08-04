using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // MASTER catalog of pollutants (managed from the Chemistry page).
    // Plain POCO — no navigation properties, no FK config. Ids 1..9 match the
    // values already stored in pollutantstreams.PollutantType.
    public class Pollutant
    {
        [Key]
        public int Id { get; set; }

        public string Code { get; set; } = "";           // "SO2"
        public string DisplayName { get; set; } = "";     // "Sulfur Dioxide"
        public string Formula { get; set; } = "";         // "SO₂"

        public double DefaultMolecularWeight { get; set; }
        public double DefaultHenrysLawConstant { get; set; }

        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;

        public int? CreatedByUserId { get; set; }         // stamped from Session
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
