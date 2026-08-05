using System.Linq;
using WetScrubber.Business.MassTransfer;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    public class EfDiffusionCoefficientLookup : IDiffusionCoefficientLookup
    {
        private readonly ApplicationDbContext _dbContext;

        public EfDiffusionCoefficientLookup(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public DiffusionSpeciesData? GetByCode(string code)
        {
            var row = _dbContext.DiffusionProperties
                .FirstOrDefault(d => d.ComponentCode == code && d.IsActive);

            if (row == null) return null;

            return new DiffusionSpeciesData
            {
                Code = row.ComponentCode,
                MolarVolumeAtBoilingPointCm3Mol = row.MolarVolumeAtBoilingPointCm3Mol,
                AssociationFactor = row.AssociationFactor
            };
        }
    }
}