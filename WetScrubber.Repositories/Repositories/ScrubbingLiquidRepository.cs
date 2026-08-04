using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    public class ScrubbingLiquidRepository
    {
        private readonly ApplicationDbContext _DbContext;

        public ScrubbingLiquidRepository(ApplicationDbContext context)
        {
            _DbContext = context;
        }

        public List<ScrubbingLiquid> GetAll(bool activeOnly = false)
        {
            var q = _DbContext.ScrubbingLiquids.AsQueryable();
            if (activeOnly) q = q.Where(l => l.IsActive);
            return q.OrderBy(l => l.Id).ToList();
        }

        public ScrubbingLiquid? GetById(int id)
            => _DbContext.ScrubbingLiquids.FirstOrDefault(l => l.Id == id);

        public Dictionary<int, ScrubbingLiquid> GetLookup()
            => _DbContext.ScrubbingLiquids.ToDictionary(l => l.Id);

        public int Add(ScrubbingLiquid l, int? createdByUserId = null)
        {
            l.CreatedByUserId = createdByUserId;
            l.CreatedAt = DateTime.Now;
            l.UpdatedAt = DateTime.Now;
            _DbContext.ScrubbingLiquids.Add(l);
            _DbContext.SaveChanges();
            return l.Id;
        }

        public bool Update(ScrubbingLiquid l)
        {
            var row = _DbContext.ScrubbingLiquids.FirstOrDefault(x => x.Id == l.Id);
            if (row == null) return false;

            row.Code = l.Code;
            row.DisplayName = l.DisplayName;
            row.Formula = l.Formula;
            row.ReagentMolecularWeight = l.ReagentMolecularWeight;
            row.DefaultDensity = l.DefaultDensity;
            row.DefaultPH = l.DefaultPH;
            row.Description = l.Description;
            row.IsActive = l.IsActive;
            row.UpdatedAt = DateTime.Now;

            _DbContext.SaveChanges();
            return true;
        }

        public bool Delete(int id)
        {
            var row = _DbContext.ScrubbingLiquids.FirstOrDefault(x => x.Id == id);
            if (row == null) return false;

            row.IsActive = false;
            row.UpdatedAt = DateTime.Now;
            _DbContext.SaveChanges();
            return true;
        }
    }
}
