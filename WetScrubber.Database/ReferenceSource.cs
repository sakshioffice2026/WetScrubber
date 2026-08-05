using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // Lookup table for citing where every thermodynamic/property number
    // came from (NIST WebBook, DIPPR, Perry's 9th ed. Table 2-XXX, or
    // "regressed in-house from VLE run on <date>"). Every property table
    // in Phase 0 (ComponentProperty, HenrysLawData, NrtlBinaryParameter,
    // DiffusionProperty) points at this via ReferenceSourceId so a
    // number's provenance is always queryable — the same discipline
    // ScrubberDesign.ReviewStatus already applies to designs, applied
    // one layer down to the data those designs are computed from.
    public class ReferenceSource
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(300)]
        public string Citation { get; set; } = "";   // "NIST WebBook, SO2 thermophysical data"

        [MaxLength(500)]
        public string? Url { get; set; }

        [MaxLength(50)]
        public string? SourceType { get; set; }       // "NIST" | "DIPPR" | "Perrys" | "InHouseRegression" | "Other"

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}