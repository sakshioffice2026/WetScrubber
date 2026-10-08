using System;

namespace WetScrubber.Database
{
    /// <summary>
    /// Seed data for HenrysLawData / ReferenceSource.
    /// H_ReferenceAt25C is dimensionless Cg/Cl (volatility form), carried forward from
    /// chemistrypredictor.py KNOWN_REACTIONS (ReferenceSourceId = 2).
    /// HeatOfSolutionKJmol is negative for exothermic dissolution; SO2 is sourced from
    /// ReferenceSourceId = 3 (Goldberg and Parker, 1985), consistent with Sander (2023),
    /// d ln H / d(1/T) = 3100 K for SO2. Other species stay null until sourced.
    /// ReferenceSource Ids 1-3 are seeded in ApplicationDbContext; 1000 avoids key clashes.
    /// </summary>
    public static class HenrysLawSeedData
    {
        private static readonly DateTime SeedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static readonly ReferenceSource SanderSource = new ReferenceSource
        {
            Id = 1000,
            Citation = "Sander, R. (2023). Compilation of Henry's law constants (version 5.0.0) for water as solvent. Atmos. Chem. Phys., 23, 10901-12440.",
            Url = "https://doi.org/10.5194/acp-23-10901-2023",
            SourceType = "Other",
            CreatedAt = SeedDate
        };

        public static readonly HenrysLawData[] Rows = new[]
        {
            Row(1, "SO2", 0.034, -26.97, 3),
            // HCl: heat of solution intentionally null. The tabulated 9000 K
            // (-74.8 kJ/mol) is for the effective constant (H x Ka) incl.
            // dissociation, not the physical Cg/Cl constant seeded here.
            Row(2, "HCl", 0.00002),
            // d ln Hs / d(1/T) = 4200 K -> -34.9 kJ/mol (Sander 2023)
            Row(3, "NH3", 0.00069, -34.92, 1000),
            // d ln Hs / d(1/T) = 2100 K -> -17.46 kJ/mol (JPL / Sander 2023)
            Row(4, "H2S", 0.40, -17.46, 1000),
            // d ln Hs / d(1/T) = 2000 K -> -16.63 kJ/mol (JPL / Sander 2023;
            // effective value incl. Cl2/HOCl hydrolysis)
            Row(5, "Cl2", 0.66, -16.63, 1000)
        };

        private static HenrysLawData Row(
            int id,
            string code,
            double hAt25C,
            double? heatOfSolutionKJmol = null,
            int referenceSourceId = 2) => new HenrysLawData
            {
                Id = id,
                PollutantCode = code,
                H_ReferenceAt25C = hAt25C,
                HeatOfSolutionKJmol = heatOfSolutionKJmol,
                ReferenceSourceId = referenceSourceId,
                ValidatedFlag = false,
                IsActive = true,
                CreatedAt = SeedDate,
                UpdatedAt = SeedDate
            };
    }
}