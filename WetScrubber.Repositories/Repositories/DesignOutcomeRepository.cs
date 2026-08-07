using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    // Write path for Phase 6 field-outcome capture. Every row saved here is
    // training data for the self-learning design-calibration model in
    // WetScrubber.Business/GNN — see DesignOutcomeModel in ml_models.py.
    public class DesignOutcomeRepository
    {
        private readonly ApplicationDbContext _DbContext;

        public DesignOutcomeRepository(ApplicationDbContext context)
        {
            _DbContext = context;
        }

        public List<DesignOutcome> GetByDesign(int designId)
            => _DbContext.DesignOutcomes
                .Where(o => o.DesignId == designId)
                .OrderByDescending(o => o.MeasurementDate)
                .ToList();

        public List<DesignOutcome> GetAll()
            => _DbContext.DesignOutcomes.OrderByDescending(o => o.CreatedAt).ToList();

        public int Add(DesignOutcome outcome, int createdByUserId)
        {
            outcome.CreatedByUserId = createdByUserId;
            outcome.CreatedAt = DateTime.UtcNow;

            _DbContext.DesignOutcomes.Add(outcome);
            _DbContext.SaveChanges();
            return outcome.Id;
        }

        public int Count() => _DbContext.DesignOutcomes.Count();
    }
}