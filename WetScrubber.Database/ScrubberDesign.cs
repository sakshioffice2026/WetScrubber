using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WetScrubber.Database.Enums;

namespace WetScrubber.Database
{
    public class ScrubberDesign
    {
        [Key]
        public int DesignId { get; set; }

        public int ProjectId { get; set; }

        [Required]
        [MaxLength(200)]
        public string DesignName { get; set; } = string.Empty;

        public ScrubberType ScrubberType { get; set; } = ScrubberType.PackedTower;

        public ConstructionMaterial ShellMaterial { get; set; } = ConstructionMaterial.FRP;

        public ConstructionMaterial InternalMaterial { get; set; } = ConstructionMaterial.PP;

        [MaxLength(30)]
        public string? PackingCode { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // ── Phase 5: PE review / sign-off workflow ────────────────
        // Draft -> UnderReview -> Approved (locked) | ChangesRequested.
        public DesignReviewStatus ReviewStatus { get; set; } = DesignReviewStatus.Draft;

        public int? SubmittedForReviewByUserId { get; set; }
        public DateTime? SubmittedForReviewAt { get; set; }

        public int? ReviewedByUserId { get; set; }     // the PE who signed off
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewNotes { get; set; }

        // Once true, ScrubberController.Edit must refuse changes — the
        // engineer has to create a new revision/branch instead of mutating
        // a signed-off design.
        public bool IsLocked { get; set; } = false;

        // ── Redesign lineage ───────────────────────────────────────
        // Set by ScrubberController.CreateRevision when a locked design is
        // cloned into a fresh, editable draft ("Redesign as per AI
        // narrative"). Null for a design that was never derived from
        // another one. Lets the Compare page find what to diff against,
        // and RevisionNumber gives a human-readable "Rev 2", "Rev 3" trail
        // without having to walk the chain just to render a label.
        public int? PreviousDesignId { get; set; }

        public int RevisionNumber { get; set; } = 1;

        // ── Navigation ───────────────────────────────────────────
        public Project Project { get; set; } = null!;

        public ScrubberDesign? PreviousDesign { get; set; }

        public GasStream? GasStream { get; set; }

        public ScrubbingLiquidSpec? LiquidSpec { get; set; }

        public ScrubberGeometry? Geometry { get; set; }
    }


}
