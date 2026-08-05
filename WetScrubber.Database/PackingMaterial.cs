using System;
using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Database
{
    // MASTER catalog of packing types (managed from the Chemistry/Packing
    // page, same governance model as ComponentProperty/HenrysLawData).
    // Replaces ScrubberCalculationEngine's hardcoded:
    //   DefaultPackingFactor = 66.0   (Fp, 1/m, Pall Rings 50mm)
    //   DefaultSurfaceArea   = 112.0  (ap, m²/m³, Pall Rings 50mm)
    // with a real vendor/type/size lookup table so a design can select
    // any packing instead of always computing against one hardcoded row.
    //
    // Joined by Code (same "plain key, no FK constraint, join manually"
    // convention as ChemicalReaction/ComponentProperty).
    //
    // IMPORTANT: same sourcing discipline as ComponentProperty — seeded
    // from standard references (Perry's 9th ed. Table 14-13/14-14,
    // GPDC generalized pressure drop correlation charts) but
    // ValidatedFlag defaults to false until cross-checked against a
    // primary source and flipped.
    public class PackingMaterial
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(30)]
        public string Code { get; set; } = "";           // "PALL-PP-50", "IMTP-SS-25"

        [Required, MaxLength(100)]
        public string DisplayName { get; set; } = "";     // "Pall Ring 50mm Polypropylene"

        [MaxLength(100)]
        public string? Vendor { get; set; }                // "Koch-Glitsch", "Sulzer", "Generic"

        [Required, MaxLength(50)]
        public string PackingType { get; set; } = "";      // "Pall Ring" | "Raschig Ring" |
                                                           // "Intalox Saddle" | "Hy-Pak" |
                                                           // "Structured" — free text, not an
                                                           // enum, since new vendor types show
                                                           // up faster than migrations should.

        [MaxLength(30)]
        public string? MaterialOfConstruction { get; set; } // "Polypropylene" | "Ceramic" |
                                                            // "SS316" | "Carbon Steel"

        public double? NominalSizeMm { get; set; }          // 25 / 38 / 50 / 90 ...

        // ── GPDC / rate-based mass-transfer inputs ──────────────────
        // Same units ScrubberCalculationEngine and OndaMassTransferCorrelation
        // already use throughout — no unit conversion needed at the call site.
        public double PackingFactorPerM { get; set; }        // Fp, 1/m
        public double SpecificSurfaceAreaM2M3 { get; set; }  // ap, m²/m³
        public double VoidFraction { get; set; }             // ε, dimensionless (0-1)

        // Structured packing is characterized by HETP directly rather than
        // NTU/HTU film coefficients; null for random packing (the norm today).
        public double? NominalHetpM { get; set; }

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