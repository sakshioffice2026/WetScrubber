using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    public class ChemicalReactionRepository
    {
        private readonly ApplicationDbContext _DbContext;

        public ChemicalReactionRepository(ApplicationDbContext context)
        {
            _DbContext = context;
        }

        public List<ChemicalReaction> GetAll(bool activeOnly = false)
        {
            var q = _DbContext.ChemicalReactions.AsQueryable();
            if (activeOnly) q = q.Where(r => r.IsActive);
            return q.OrderBy(r => r.PollutantId).ThenBy(r => r.ScrubbingLiquidId).ToList();
        }

        // For the Chemistry page tab of a single pollutant.
        public List<ChemicalReaction> GetByPollutant(int pollutantId, bool activeOnly = true)
        {
            var q = _DbContext.ChemicalReactions.Where(r => r.PollutantId == pollutantId);
            if (activeOnly) q = q.Where(r => r.IsActive);
            return q.OrderByDescending(r => r.IsPrimary).ToList();
        }

        public ChemicalReaction? GetById(int id)
            => _DbContext.ChemicalReactions.FirstOrDefault(r => r.Id == id);

        // ── Design-page lookup ───────────────────────────────────
        // The reaction the calculation should use for a chosen pollutant + liquid.
        // Picks the primary row when several exist for the pair.
        public ChemicalReaction? GetPrimaryForPair(int pollutantId, int scrubbingLiquidId)
            => _DbContext.ChemicalReactions
                .Where(r => r.PollutantId == pollutantId
                         && r.ScrubbingLiquidId == scrubbingLiquidId
                         && r.IsActive)
                .OrderByDescending(r => r.IsPrimary)
                .FirstOrDefault();

        // All variants for a pair (e.g. SO₂+NaOH high-pH vs low-pH).
        public List<ChemicalReaction> GetVariantsForPair(int pollutantId, int scrubbingLiquidId)
            => _DbContext.ChemicalReactions
                .Where(r => r.PollutantId == pollutantId
                         && r.ScrubbingLiquidId == scrubbingLiquidId
                         && r.IsActive)
                .OrderByDescending(r => r.IsPrimary)
                .ToList();

        public int Add(ChemicalReaction r, int? createdByUserId = null)
        {
            r.CreatedByUserId = createdByUserId;
            r.CreatedAt = DateTime.Now;
            r.UpdatedAt = DateTime.Now;

            // Keep a single primary per (pollutant, liquid) pair.
            if (r.IsPrimary) ClearPrimaryFlag(r.PollutantId, r.ScrubbingLiquidId, exceptId: 0);

            _DbContext.ChemicalReactions.Add(r);
            _DbContext.SaveChanges();
            return r.Id;
        }

        public bool Update(ChemicalReaction r)
        {
            var row = _DbContext.ChemicalReactions.FirstOrDefault(x => x.Id == r.Id);
            if (row == null) return false;

            if (r.IsPrimary) ClearPrimaryFlag(r.PollutantId, r.ScrubbingLiquidId, exceptId: r.Id);

            row.PollutantId = r.PollutantId;
            row.ScrubbingLiquidId = r.ScrubbingLiquidId;
            row.Equation = r.Equation;
            row.ReactionType = r.ReactionType;
            row.ProductName = r.ProductName;
            row.StoichiometricRatio = r.StoichiometricRatio;
            row.MaxRemovalEfficiency = r.MaxRemovalEfficiency;
            row.MinOperatingPH = r.MinOperatingPH;
            row.MaxOperatingPH = r.MaxOperatingPH;
            row.HeatOfReaction = r.HeatOfReaction;
            row.DesignNotes = r.DesignNotes;
            row.IsPrimary = r.IsPrimary;
            row.IsActive = r.IsActive;
            row.UpdatedAt = DateTime.Now;

            _DbContext.SaveChanges();
            return true;
        }

        public bool Delete(int id)
        {
            var row = _DbContext.ChemicalReactions.FirstOrDefault(x => x.Id == id);
            if (row == null) return false;

            row.IsActive = false;
            row.UpdatedAt = DateTime.Now;
            _DbContext.SaveChanges();
            return true;
        }

        // Demote any other primary rows for the same pair so only one wins.
        private void ClearPrimaryFlag(int pollutantId, int scrubbingLiquidId, int exceptId)
        {
            var others = _DbContext.ChemicalReactions
                .Where(r => r.PollutantId == pollutantId
                         && r.ScrubbingLiquidId == scrubbingLiquidId
                         && r.IsPrimary
                         && r.Id != exceptId);

            foreach (var o in others) o.IsPrimary = false;
            // saved by the caller's SaveChanges
        }
    }
}
