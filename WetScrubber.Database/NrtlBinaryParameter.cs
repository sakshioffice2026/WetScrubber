using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // Binary interaction parameters (tau, alpha) for the NRTL liquid
    // activity coefficient model. Unordered pair per row (A, B) but tau
    // is direction-dependent (Tau_AB != Tau_BA in general), matching the
    // asymmetric NRTL formula.
    //
    // NOTE ON SOURCING: unlike critical properties, these are NOT safe
    // to seed from general engineering knowledge. Gas-into-water NRTL
    // pairs (SO2-Water, NH3-Water, HCl-Water) are typically only
    // available in AspenTech's own databank, DECHEMA's Chemistry Data
    // Series, or via in-house regression against experimental VLE data.
    // This table intentionally ships EMPTY of seed data — see the
    // conversation notes flagging this as the longest pole in Phase 0.
    // Do not fabricate values here; an uncovered pair should make the
    // engine fall back to ideal-solution behavior (gamma = 1) with a
    // visible warning, not silently use a made-up tau.
    public class NrtlBinaryParameter
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string ComponentACode { get; set; } = "";   // e.g. "SO2"

        [Required, MaxLength(20)]
        public string ComponentBCode { get; set; } = "";   // e.g. "H2O"

        public double Tau_AB { get; set; }
        public double Tau_BA { get; set; }
        public double Alpha { get; set; } = 0.3;            // non-randomness parameter, 0.2-0.47 typical

        public double? ValidTempMinK { get; set; }
        public double? ValidTempMaxK { get; set; }

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