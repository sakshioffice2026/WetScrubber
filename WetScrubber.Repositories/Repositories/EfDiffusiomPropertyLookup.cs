using System.Linq;
using WetScrubber.Business.MassTransfer;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    // Reads DiffusionProperty (Phase 0/2) by plain ComponentCode — same
    // "plain key, no FK constraint" convention as EfHenrysLawLookup /
    // EfComponentPropertyLookup. No pollutant-id overload needed: callers
    // (ScrubberCalculationEngine.GetEffectiveFilmCoefficients) already
    // resolve the code once via IComponentPropertyLookup before calling
    // this, same two-hop pattern documented in IDiffusionPropertyLookup.cs.
    //
    // Does not filter on ValidatedFlag — existence vs. trustworthiness is
    // a separate concern, same reasoning as the other Ef*Lookup classes.
    public class EfDiffusionPropertyLookup : IDiffusionPropertyLookup
    {
        private readonly ApplicationDbContext _dbContext;

        public EfDiffusionPropertyLookup(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public DiffusionSpeciesData? GetByComponentCode(string code)
        {
            var row = _dbContext.DiffusionProperties
                .FirstOrDefault(d => d.ComponentCode == code && d.IsActive);

            if (row == null) return null;

            return new DiffusionSpeciesData
            {
                ComponentCode = row.ComponentCode,
                MolarVolumeAtBoilingPointCm3Mol = row.MolarVolumeAtBoilingPointCm3Mol,
                AssociationFactor = row.AssociationFactor,
                FullerDiffusionVolumeCm3Mol = row.FullerDiffusionVolumeCm3Mol ?? 0.0
            };
        }
    }
}