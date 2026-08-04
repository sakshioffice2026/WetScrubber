using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    // All Pollutant-master database work lives here. Controllers never touch
    // the DbContext directly — they call _uow.pollutantRepository.*.
    public class PollutantRepository
    {
        private readonly ApplicationDbContext _DbContext;

        public PollutantRepository(ApplicationDbContext context)
        {
            _DbContext = context;
        }

        public List<Pollutant> GetAll(bool activeOnly = false)
        {
            var q = _DbContext.Pollutants.AsQueryable();
            if (activeOnly) q = q.Where(p => p.IsActive);
            return q.OrderBy(p => p.Id).ToList();
        }

        public Pollutant? GetById(int id)
            => _DbContext.Pollutants.FirstOrDefault(p => p.Id == id);

        // Handy for showing names on the design pages without a FK/join.
        public Dictionary<int, Pollutant> GetLookup()
            => _DbContext.Pollutants.ToDictionary(p => p.Id);

        // createdByUserId comes from the controller (HttpContext.Session "UserId").
        public int Add(Pollutant p, int? createdByUserId = null)
        {
            p.CreatedByUserId = createdByUserId;
            p.CreatedAt = DateTime.Now;
            p.UpdatedAt = DateTime.Now;
            _DbContext.Pollutants.Add(p);
            _DbContext.SaveChanges();
            return p.Id;
        }

        public bool Update(Pollutant p)
        {
            var row = _DbContext.Pollutants.FirstOrDefault(x => x.Id == p.Id);
            if (row == null) return false;

            row.Code = p.Code;
            row.DisplayName = p.DisplayName;
            row.Formula = p.Formula;
            row.DefaultMolecularWeight = p.DefaultMolecularWeight;
            row.DefaultHenrysLawConstant = p.DefaultHenrysLawConstant;
            row.Description = p.Description;
            row.IsActive = p.IsActive;
            row.UpdatedAt = DateTime.Now;

            _DbContext.SaveChanges();
            return true;
        }

        // Soft delete — keeps history and avoids orphaning existing designs
        // that still reference this pollutant id.
        public bool Delete(int id)
        {
            var row = _DbContext.Pollutants.FirstOrDefault(x => x.Id == id);
            if (row == null) return false;

            row.IsActive = false;
            row.UpdatedAt = DateTime.Now;
            _DbContext.SaveChanges();
            return true;
        }
    }
}
