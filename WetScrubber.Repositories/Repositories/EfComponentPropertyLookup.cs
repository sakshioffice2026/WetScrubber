using System.Linq;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    // Reads ComponentProperties (Phase 0) and joins it to Pollutant by
    // Code, same "plain key, join manually" convention already used
    // between ChemicalReaction and Pollutant/ScrubbingLiquid.
    //
    // NOTE: does not filter on ValidatedFlag. A row with
    // ValidatedFlag = false still gets used — this lookup's job is
    // "does data exist", not "is it trustworthy yet". Surfacing
    // unvalidated-data warnings to the person running the design is a
    // separate concern (belongs in DesignDiagnosticsEngine or the
    // calculation result, not silently inside this lookup).
    public class EfComponentPropertyLookup : IComponentPropertyLookup
    {
        private readonly ApplicationDbContext _dbContext;

        public EfComponentPropertyLookup(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public EosComponentInput? GetByCode(string code)
        {
            var row = _dbContext.ComponentProperties
                .FirstOrDefault(c => c.Code == code && c.IsActive);

            if (row == null) return null;

            // A component missing any critical property can't feed the
            // EOS — treat it the same as "not found" rather than letting
            // a null propagate into the cubic solver.
            if (row.CriticalTemperatureK == null || row.CriticalPressureKPa == null || row.AcentricFactor == null)
                return null;

            return new EosComponentInput
            {
                Code = row.Code,
                CriticalTemperatureK = row.CriticalTemperatureK.Value,
                CriticalPressureKPa = row.CriticalPressureKPa.Value,
                AcentricFactor = row.AcentricFactor.Value,
                MolecularWeight = row.MolecularWeight
                // MoleFraction intentionally left at 0 — GasMixtureBuilder
                // sets it once concentration is known.
            };
        }

        public EosComponentInput? GetByPollutantId(int pollutantId)
        {
            var pollutantCode = _dbContext.Pollutants
                .Where(p => p.Id == pollutantId)
                .Select(p => p.Code)
                .FirstOrDefault();

            return string.IsNullOrEmpty(pollutantCode) ? null : GetByCode(pollutantCode);
        }
    }
}