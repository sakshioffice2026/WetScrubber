using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // Raw inputs for the Wilke-Chang liquid diffusion coefficient
    // estimation (Phase 2). Captured in Phase 0 alongside the other
    // property tables so this data is sourced once, not revisited.
    //   D_AB = 7.4e-8 * sqrt(assoc_factor * MW_solvent) * T
    //          / (viscosity_solvent * MolarVolumeAtBp_solute^0.6)
    public class DiffusionProperty
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string ComponentCode { get; set; } = "";

        // Molar volume of the solute at its normal boiling point (cm^3/mol).
        public double? MolarVolumeAtBoilingPointCm3Mol { get; set; }

        // Wilke-Chang association factor for the SOLVENT (e.g. water = 2.6).
        // Stored per-component so a row can represent either the solute's
        // own molar volume entry or a solvent's association factor entry.
        public double? AssociationFactor { get; set; }

        public int? ReferenceSourceId { get; set; }
        public bool ValidatedFlag { get; set; } = false;
        public int? ValidatedByUserId { get; set; }
        public DateTime? ValidatedAt { get; set; }

        public bool IsActive { get; set; } = true;
        public int? CreatedByUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}