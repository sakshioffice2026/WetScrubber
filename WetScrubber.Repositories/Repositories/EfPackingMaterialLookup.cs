using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.MassTransfer;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    // Reads PackingMaterial (Phase 5). Does not filter on ValidatedFlag —
    // same "existence vs. trustworthiness are separate concerns" reasoning
    // as EfComponentPropertyLookup/EfHenrysLawLookup.
    public class EfPackingMaterialLookup : IPackingMaterialLookup
    {
        // Historical single hardcoded packing (Pall Ring 50mm PP), kept as
        // the hard fallback so callers that don't pass a packing code, or
        // pass one that isn't in the DB yet, get byte-identical behavior
        // to ScrubberCalculationEngine's old DefaultPackingFactor/
        // DefaultSurfaceArea constants rather than a silent zero/crash.
        private static readonly PackingMaterialData FallbackDefault = new()
        {
            Code = "PALL-PP-50",
            DisplayName = "Pall Ring 50mm Polypropylene (default)",
            PackingType = "Pall Ring",
            NominalSizeMm = 50,
            PackingFactorPerM = 66.0,
            SpecificSurfaceAreaM2M3 = 112.0,
            VoidFraction = 0.94
        };

        private readonly ApplicationDbContext _dbContext;

        public EfPackingMaterialLookup(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public PackingMaterialData? GetByCode(string code)
        {
            var row = _dbContext.PackingMaterials
                .FirstOrDefault(p => p.Code == code && p.IsActive);

            return row == null ? null : ToDto(row);
        }

        public IReadOnlyList<PackingMaterialData> GetAll()
        {
            return _dbContext.PackingMaterials
                .Where(p => p.IsActive)
                .OrderBy(p => p.PackingType).ThenBy(p => p.NominalSizeMm)
                .Select(ToDto)
                .ToList();
        }

        public PackingMaterialData GetDefault()
        {
            return GetByCode(FallbackDefault.Code) ?? FallbackDefault;
        }

        private static PackingMaterialData ToDto(PackingMaterial row) => new()
        {
            Code = row.Code,
            DisplayName = row.DisplayName,
            PackingType = row.PackingType,
            NominalSizeMm = row.NominalSizeMm,
            PackingFactorPerM = row.PackingFactorPerM,
            SpecificSurfaceAreaM2M3 = row.SpecificSurfaceAreaM2M3,
            VoidFraction = row.VoidFraction,
            NominalHetpM = row.NominalHetpM
        };
    }
}