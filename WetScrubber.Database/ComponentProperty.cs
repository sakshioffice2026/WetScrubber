using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // MASTER catalog of pure-component physical/critical properties.
    // One row per chemical species — covers BOTH gas-phase pollutants
    // (SO2, HCl...) and liquid-phase species (Water, NaOH...), because
    // Peng-Robinson and NRTL both need critical properties for every
    // species in the system, not just the pollutant being absorbed.
    //
    // Joined by Code (matches Pollutant.Code / ScrubbingLiquid.Code),
    // same "plain key, no FK constraint, join manually" convention
    // already used between ChemicalReaction and Pollutant/ScrubbingLiquid
    // — see ChemicalReaction.cs for the precedent.
    //
    // IMPORTANT: Values below are seeded from standard engineering
    // references (NIST/DIPPR-class constants) but ValidatedFlag defaults
    // to false. Do NOT treat a row as production-safe until someone has
    // cross-checked it against a primary source (NIST WebBook, DIPPR, or
    // Perry's Chemical Engineers' Handbook) and flipped the flag — the
    // same discipline ScrubberDesign.ReviewStatus already applies to
    // designs should apply to the data a design is built on.
    public class ComponentProperty
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string Code { get; set; } = "";          // "SO2", "H2O", "NaOH"

        [Required, MaxLength(100)]
        public string DisplayName { get; set; } = "";    // "Sulfur Dioxide"

        public double MolecularWeight { get; set; }       // g/mol

        // ── Peng-Robinson EOS inputs ───────────────────────────────
        public double? CriticalTemperatureK { get; set; }
        public double? CriticalPressureKPa { get; set; }
        public double? AcentricFactor { get; set; }
        public double? NormalBoilingPointK { get; set; }

        // ── Reference-state physical properties (25°C, 1 atm) ──────
        public double? LiquidDensityKgM3 { get; set; }
        public double? LiquidViscosityMPas { get; set; }
        public double? SpecificHeatKJKgK { get; set; }

        public bool IsGasPhaseSpecies { get; set; } = true; // false for Water, NaOH(aq), etc.

        // ── Provenance / governance ─────────────────────────────────
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