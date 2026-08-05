using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // Replaces ScrubberCalculationEngine's hardcoded call:
    //   GetHenrysLawConstant(pollutant.HenrysLawConstant, 2000, temperatureC)
    // where "2000" was one fixed tempCoeff shared by every pollutant.
    // This table gives each pollutant its own reference constant AND its
    // own heat of solution, so the Van 't Hoff correction is per-species
    // instead of a single guessed number for everyone.
    //
    // HeatOfSolutionKJmol is intentionally nullable with no seeded value
    // for most rows — this number varies by source/concentration and a
    // wrong one silently corrupts the whole temperature correction, so
    // it is left for deliberate sourcing (NIST/DIPPR/Perry's) rather than
    // filled in from memory. See conversation notes: this was flagged as
    // the one property in Phase 0 not safe to seed from general knowledge.
    public class HenrysLawData
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string PollutantCode { get; set; } = "";   // joins ComponentProperty.Code / Pollutant.Code

        // H at 25°C reference temperature, same units/convention as the
        // existing Pollutant.DefaultHenrysLawConstant field.
        public double H_ReferenceAt25C { get; set; }

        // -ΔH_soln / R in the Van 't Hoff form used by
        // ScrubberCalculationEngine.GetHenrysLawConstant. Null until
        // sourced — the engine should fall back to the current single
        // hardcoded constant (with a logged warning) if this is null,
        // rather than silently computing with a wrong default.
        public double? HeatOfSolutionKJmol { get; set; }

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