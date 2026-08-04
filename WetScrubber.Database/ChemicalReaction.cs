using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // MASTER reaction between a pollutant and a scrubbing liquid + the factors
    // that drive the calculation. PollutantId / ScrubbingLiquidId are plain key
    // columns (no FK constraint, no navigation) — you join manually in code.
    public class ChemicalReaction
    {
        [Key]
        public int Id { get; set; }

        // "Key data" — the ids of the pair this reaction belongs to.
        public int PollutantId { get; set; }
        public int ScrubbingLiquidId { get; set; }

        public string Equation { get; set; } = "";        // "SO₂ + 2NaOH → Na₂SO₃ + H₂O"
        public string ReactionType { get; set; } = "";    // "Acid-base neutralisation"
        public string ProductName { get; set; } = "";     // "Sodium sulfite (Na₂SO₃)"

        public double StoichiometricRatio  { get; set; } = 1;   // mol reagent per mol pollutant
        public double MaxRemovalEfficiency { get; set; } = 99;   // % ceiling
        public double MinOperatingPH       { get; set; } = 0;
        public double MaxOperatingPH       { get; set; } = 14;
        public double? HeatOfReaction      { get; set; }

        public string? DesignNotes { get; set; }

        // When a pair has several reactions, the design page uses IsPrimary = true.
        public bool IsPrimary { get; set; } = true;
        public bool IsActive  { get; set; } = true;

        public int? CreatedByUserId { get; set; }          // stamped from Session
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
