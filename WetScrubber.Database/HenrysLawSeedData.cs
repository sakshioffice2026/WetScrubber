using System;

namespace WetScrubber.Database
{
    /// <summary>
    /// Seed data for HenrysLawData / ReferenceSource.
    /// Reference constants are carried forward unchanged from
    /// chemistrypredictor.py KNOWN_REACTIONS (ReferenceSourceId = 2).
    /// HeatOfSolutionKJmol is left null so the engine uses its shared
    /// fallback temperature coefficient until a verified value is sourced.
    /// ReferenceSource Ids 1-3 (and any others) are seeded in ApplicationDbContext; 1000 avoids key clashes.
    /// </summary>
    public static class HenrysLawSeedData
    {
        private static readonly DateTime SeedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static readonly ReferenceSource SanderSource = new ReferenceSource
        {
            Id = 1000,
            Citation = "Sander, R. (2015). Compilation of Henry's law constants (version 4.0) for water as solvent. Atmos. Chem. Phys., 15, 4399-4981.",
            Url = "https://doi.org/10.5194/acp-15-4399-2015",
            SourceType = "Other",
            CreatedAt = SeedDate
        };

        public static readonly HenrysLawData[] Rows = new[]
        {
            Row(1, "SO2", 0.034),
            Row(2, "HCl", 0.00002),
            Row(3, "NH3", 0.00069),
            Row(4, "H2S", 0.40),
            Row(5, "Cl2", 0.66)
        };

        private static HenrysLawData Row(int id, string code, double hAt25C) => new HenrysLawData
        {
            Id = id,
            PollutantCode = code,
            H_ReferenceAt25C = hAt25C,
            HeatOfSolutionKJmol = null,
            ReferenceSourceId = 2,
            ValidatedFlag = false,
            IsActive = true,
            CreatedAt = SeedDate,
            UpdatedAt = SeedDate
        };
    }
}