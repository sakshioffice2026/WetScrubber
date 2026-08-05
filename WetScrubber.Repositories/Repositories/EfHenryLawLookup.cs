using System.Linq;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    // Reads HenrysLawData (Phase 0) and joins it to Pollutant by Code —
    // same "plain key, join manually" convention as EfComponentPropertyLookup.
    //
    // Does not filter on ValidatedFlag, same reasoning as
    // EfComponentPropertyLookup: existence vs. trustworthiness are
    // separate concerns.
    public class EfHenrysLawLookup : IHenrysLawLookup
    {
        private readonly ApplicationDbContext _dbContext;

        public EfHenrysLawLookup(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public HenrysLawSpeciesData? GetByPollutantCode(string code)
        {
            var row = _dbContext.HenrysLawData
                .FirstOrDefault(h => h.PollutantCode == code && h.IsActive);

            if (row == null) return null;

            return new HenrysLawSpeciesData
            {
                PollutantCode = row.PollutantCode,
                H_ReferenceAt25C = row.H_ReferenceAt25C,
                HeatOfSolutionKJmol = row.HeatOfSolutionKJmol
            };
        }

        public HenrysLawSpeciesData? GetByPollutantId(int pollutantId)
        {
            var pollutantCode = _dbContext.Pollutants
                .Where(p => p.Id == pollutantId)
                .Select(p => p.Code)
                .FirstOrDefault();

            return string.IsNullOrEmpty(pollutantCode) ? null : GetByPollutantCode(pollutantCode);
        }
    }
}