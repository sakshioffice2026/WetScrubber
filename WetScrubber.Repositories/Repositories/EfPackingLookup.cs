using System.Linq;
using WetScrubber.Business.MassTransfer;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    public class EfPackingLookup : IPackingLookup
    {
        private readonly ApplicationDbContext _dbContext;

        public EfPackingLookup(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public PackingData? GetByCode(string code)
        {
            var row = _dbContext.Packings
                .FirstOrDefault(p => p.Code == code && p.IsActive);

            if (row == null) return null;

            return new PackingData
            {
                Code = row.Code,
                SpecificAreaM2M3 = row.SpecificAreaM2M3,
                NominalSizeM = row.NominalSizeM,
                CriticalSurfaceTensionNM = row.CriticalSurfaceTensionNM
            };
        }
    }
}