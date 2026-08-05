using Microsoft.EntityFrameworkCore;


namespace WetScrubber.Database
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        // ── DbSets ───────────────────────────────────────────────
        public DbSet<Role> Roles { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Project> Projects { get; set; }
        public DbSet<ScrubberDesign> ScrubberDesigns { get; set; }
        public DbSet<GasStream> GasStreams { get; set; }
        public DbSet<PollutantStream> PollutantStreams { get; set; }
        public DbSet<ScrubbingLiquidSpec> ScrubbingLiquidSpecs { get; set; }
        public DbSet<ScrubberGeometry> ScrubberGeometries { get; set; }
        public DbSet<Pollutant> Pollutants { get; set; }
        public DbSet<ScrubbingLiquid> ScrubbingLiquids { get; set; }
        public DbSet<ChemicalReaction> ChemicalReactions { get; set; }

        // Phase 2 — uncomment when Project model is added
        // public DbSet<Project> Projects { get; set; }

        //AI Narrative
        public DbSet<DesignReport> DesignReports { get; set; }

        // ── Phase 0 — thermodynamic/property foundation ────────────
        public DbSet<ComponentProperty> ComponentProperties { get; set; }
        public DbSet<HenrysLawData> HenrysLawData { get; set; }
        public DbSet<NrtlBinaryParameter> NrtlBinaryParameters { get; set; }
        public DbSet<DiffusionProperty> DiffusionProperties { get; set; }
        public DbSet<ReferenceSource> ReferenceSources { get; set; }

        // ── Phase 5: packing vendor library ─────────────────────────
        public DbSet<PackingMaterial> PackingMaterials { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);


            // ── Seed default roles ────────────────────────────────
            modelBuilder.Entity<Role>().HasData(
                new Role { RoleId = 1, RoleName = "Admin" },
                new Role { RoleId = 2, RoleName = "Engineer" },
                new Role { RoleId = 3, RoleName = "Viewer" }
            );

            //AI Narrative 
            modelBuilder.Entity<DesignReport>()
                .HasOne(x => x.Design)
                .WithMany()
                .HasForeignKey(x => x.DesignId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ScrubbingLiquidSpec>()
                .HasOne(x => x.ScrubbingLiquid)
                .WithMany()
                .HasForeignKey(x => x.LiquidType)
                .OnDelete(DeleteBehavior.Restrict);

            // Redesign lineage — self-referencing, optional. Restrict
            // delete so removing a revision can never cascade-delete the
            // locked original it was compared against.
            modelBuilder.Entity<ScrubberDesign>()
                .HasOne(x => x.PreviousDesign)
                .WithMany()
                .HasForeignKey(x => x.PreviousDesignId)
                .OnDelete(DeleteBehavior.Restrict);

            // ── Phase 0 seed data ───────────────────────────────────
            // ReferenceSourceId = 1 is a deliberate placeholder meaning
            // "recalled engineering constant, not yet cross-checked
            // against a primary source." Every ComponentProperty row
            // below carries ValidatedFlag = false for the same reason —
            // see ComponentProperty.cs for why that matters before any
            // of this feeds a real design.
            modelBuilder.Entity<ReferenceSource>().HasData(
                new ReferenceSource
                {
                    Id = 1,
                    Citation = "Recalled standard engineering constant — PENDING verification against NIST WebBook / DIPPR / Perry's before production use",
                    SourceType = "Unverified"
                },
                new ReferenceSource
                {
                    Id = 2,
                    Citation = "Existing WetScrubber seed data (chemistrypredictor.py KNOWN_REACTIONS) — carried forward, not independently re-sourced",
                    SourceType = "InHouse"
                }
            );

            // Critical properties for Peng-Robinson. Tc/Pc/omega values
            // are standard textbook-grade constants (right order of
            // magnitude, plausible digits) but UNVALIDATED — do not
            // ship these into a real design calc without a NIST/DIPPR
            // cross-check first. Liquid species (Water, NaOH, H2SO4,
            // NaOCl) are flagged IsGasPhaseSpecies = false so NRTL can
            // find them without touching the gas-phase table.
            modelBuilder.Entity<ComponentProperty>().HasData(
                new ComponentProperty { Id = 1, Code = "SO2", DisplayName = "Sulfur Dioxide", MolecularWeight = 64.07, CriticalTemperatureK = 430.8, CriticalPressureKPa = 7884, AcentricFactor = 0.256, NormalBoilingPointK = 263.1, IsGasPhaseSpecies = true, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 2, Code = "HCl", DisplayName = "Hydrogen Chloride", MolecularWeight = 36.46, CriticalTemperatureK = 324.7, CriticalPressureKPa = 8310, AcentricFactor = 0.13, NormalBoilingPointK = 188.1, IsGasPhaseSpecies = true, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 3, Code = "NH3", DisplayName = "Ammonia", MolecularWeight = 17.03, CriticalTemperatureK = 405.5, CriticalPressureKPa = 11350, AcentricFactor = 0.253, NormalBoilingPointK = 239.7, IsGasPhaseSpecies = true, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 4, Code = "H2S", DisplayName = "Hydrogen Sulfide", MolecularWeight = 34.08, CriticalTemperatureK = 373.2, CriticalPressureKPa = 8940, AcentricFactor = 0.10, NormalBoilingPointK = 213.5, IsGasPhaseSpecies = true, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 5, Code = "Cl2", DisplayName = "Chlorine", MolecularWeight = 70.90, CriticalTemperatureK = 417.2, CriticalPressureKPa = 7700, AcentricFactor = 0.07, NormalBoilingPointK = 239.1, IsGasPhaseSpecies = true, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 6, Code = "H2O", DisplayName = "Water", MolecularWeight = 18.02, CriticalTemperatureK = 647.1, CriticalPressureKPa = 22064, AcentricFactor = 0.344, NormalBoilingPointK = 373.15, LiquidDensityKgM3 = 997, LiquidViscosityMPas = 0.89, SpecificHeatKJKgK = 4.18, IsGasPhaseSpecies = false, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 7, Code = "NaOH", DisplayName = "Caustic Soda (solute)", MolecularWeight = 40.00, LiquidDensityKgM3 = 2130, IsGasPhaseSpecies = false, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 8, Code = "H2SO4", DisplayName = "Sulfuric Acid (solute)", MolecularWeight = 98.08, LiquidDensityKgM3 = 1830, IsGasPhaseSpecies = false, ReferenceSourceId = 1, ValidatedFlag = false },
                new ComponentProperty { Id = 9, Code = "NaOCl", DisplayName = "Sodium Hypochlorite (solute)", MolecularWeight = 74.44, LiquidDensityKgM3 = 1210, IsGasPhaseSpecies = false, ReferenceSourceId = 1, ValidatedFlag = false }
            );

            // Henry's Law reference constants (H at 25C) carried forward
            // unchanged from the existing chemistrypredictor.py seed
            // data — same numbers already trusted elsewhere in this
            // codebase, not new claims. HeatOfSolutionKJmol is left
            // NULL deliberately: see HenrysLawData.cs for why this one
            // field is not safe to seed from memory. The calculation
            // engine must keep using its current hardcoded fallback
            // until these are populated from a real source.
            modelBuilder.Entity<HenrysLawData>().HasData(
                new HenrysLawData { Id = 1, PollutantCode = "SO2", H_ReferenceAt25C = 0.0083, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false },
                new HenrysLawData { Id = 2, PollutantCode = "HCl", H_ReferenceAt25C = 0.00002, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false },
                new HenrysLawData { Id = 3, PollutantCode = "NH3", H_ReferenceAt25C = 0.00061, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false },
                new HenrysLawData { Id = 4, PollutantCode = "H2S", H_ReferenceAt25C = 0.0102, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false },
                new HenrysLawData { Id = 5, PollutantCode = "Cl2", H_ReferenceAt25C = 0.0074, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false }
            );

            // ── Phase 2 seed data ────────────────────────────────────
            // Molar volume at normal boiling point (Vb, cm3/mol) and
            // Fuller-Schettler-Giddings diffusion volume (SumV, cm3/mol)
            // for each pollutant, plus water's Wilke-Chang association
            // factor (2.6 — the standard literature value for water as
            // solvent). Same discipline as the ComponentProperty seed
            // above: recalled standard engineering-reference values,
            // right order of magnitude, UNVALIDATED — ReferenceSourceId=1,
            // ValidatedFlag=false. Do not ship into a real design without
            // a NIST/DIPPR/Poling-Prausnitz-O'Connell cross-check first.
            modelBuilder.Entity<DiffusionProperty>().HasData(
                new DiffusionProperty { Id = 1, ComponentCode = "SO2", MolarVolumeAtBoilingPointCm3Mol = 44.8, FullerDiffusionVolumeCm3Mol = 41.1, ReferenceSourceId = 1, ValidatedFlag = false },
                new DiffusionProperty { Id = 2, ComponentCode = "HCl", MolarVolumeAtBoilingPointCm3Mol = 30.7, FullerDiffusionVolumeCm3Mol = 23.3, ReferenceSourceId = 1, ValidatedFlag = false },
                new DiffusionProperty { Id = 3, ComponentCode = "NH3", MolarVolumeAtBoilingPointCm3Mol = 25.8, FullerDiffusionVolumeCm3Mol = 14.9, ReferenceSourceId = 1, ValidatedFlag = false },
                new DiffusionProperty { Id = 4, ComponentCode = "H2S", MolarVolumeAtBoilingPointCm3Mol = 32.9, FullerDiffusionVolumeCm3Mol = 27.5, ReferenceSourceId = 1, ValidatedFlag = false },
                new DiffusionProperty { Id = 5, ComponentCode = "Cl2", MolarVolumeAtBoilingPointCm3Mol = 48.4, FullerDiffusionVolumeCm3Mol = 38.4, ReferenceSourceId = 1, ValidatedFlag = false },
                new DiffusionProperty { Id = 6, ComponentCode = "H2O", AssociationFactor = 2.6, ReferenceSourceId = 1, ValidatedFlag = false }
            );

            // NrtlBinaryParameter: deliberately NOT seeded. See
            // NrtlBinaryParameter.cs — fabricating tau/alpha values here
            // would be worse than leaving the table empty, since a wrong
            // NRTL parameter silently produces a wrong liquid activity
            // coefficient with no obvious symptom downstream.

            // ── Phase 5: packing vendor library ─────────────────────
            // Fp/ap/void-fraction are the widely-published GPDC generalized
            // correlation constants (Perry's 9th ed., Table 14-13/14-14) —
            // catalog-level generic values, not a vendor's proprietary test
            // data, so (unlike NrtlBinaryParameter) safe to seed here. Row
            // Id=3 (PALL-PP-50) matches ScrubberCalculationEngine's old
            // hardcoded DefaultPackingFactor/DefaultSurfaceArea exactly —
            // see EfPackingMaterialLookup.FallbackDefault for the same values.
            modelBuilder.Entity<PackingMaterial>().HasData(
                new PackingMaterial { Id = 1, Code = "PALL-PP-25", DisplayName = "Pall Ring 25mm Polypropylene", Vendor = "Generic", PackingType = "Pall Ring", MaterialOfConstruction = "Polypropylene", NominalSizeMm = 25, PackingFactorPerM = 170.0, SpecificSurfaceAreaM2M3 = 205.0, VoidFraction = 0.90, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 2, Code = "PALL-PP-38", DisplayName = "Pall Ring 38mm Polypropylene", Vendor = "Generic", PackingType = "Pall Ring", MaterialOfConstruction = "Polypropylene", NominalSizeMm = 38, PackingFactorPerM = 130.0, SpecificSurfaceAreaM2M3 = 130.0, VoidFraction = 0.91, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 3, Code = "PALL-PP-50", DisplayName = "Pall Ring 50mm Polypropylene", Vendor = "Generic", PackingType = "Pall Ring", MaterialOfConstruction = "Polypropylene", NominalSizeMm = 50, PackingFactorPerM = 66.0, SpecificSurfaceAreaM2M3 = 112.0, VoidFraction = 0.94, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 4, Code = "PALL-SS-50", DisplayName = "Pall Ring 50mm Stainless Steel 316", Vendor = "Generic", PackingType = "Pall Ring", MaterialOfConstruction = "SS316", NominalSizeMm = 50, PackingFactorPerM = 79.0, SpecificSurfaceAreaM2M3 = 115.0, VoidFraction = 0.94, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 5, Code = "RASCHIG-CER-25", DisplayName = "Raschig Ring 25mm Ceramic", Vendor = "Generic", PackingType = "Raschig Ring", MaterialOfConstruction = "Ceramic", NominalSizeMm = 25, PackingFactorPerM = 510.0, SpecificSurfaceAreaM2M3 = 190.0, VoidFraction = 0.68, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 6, Code = "RASCHIG-CER-50", DisplayName = "Raschig Ring 50mm Ceramic", Vendor = "Generic", PackingType = "Raschig Ring", MaterialOfConstruction = "Ceramic", NominalSizeMm = 50, PackingFactorPerM = 220.0, SpecificSurfaceAreaM2M3 = 92.0, VoidFraction = 0.71, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 7, Code = "IMTP-SS-25", DisplayName = "Intalox Metal Tower Packing 25mm SS", Vendor = "Koch-Glitsch", PackingType = "Intalox Saddle", MaterialOfConstruction = "SS316", NominalSizeMm = 25, PackingFactorPerM = 141.0, SpecificSurfaceAreaM2M3 = 148.0, VoidFraction = 0.97, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 8, Code = "IMTP-SS-50", DisplayName = "Intalox Metal Tower Packing 50mm SS", Vendor = "Koch-Glitsch", PackingType = "Intalox Saddle", MaterialOfConstruction = "SS316", NominalSizeMm = 50, PackingFactorPerM = 66.0, SpecificSurfaceAreaM2M3 = 92.0, VoidFraction = 0.97, ReferenceSourceId = 1, ValidatedFlag = false },
                new PackingMaterial { Id = 9, Code = "MELLAPAK-250Y", DisplayName = "Mellapak 250Y Structured Packing", Vendor = "Sulzer", PackingType = "Structured", MaterialOfConstruction = "SS316", NominalSizeMm = null, PackingFactorPerM = 66.0, SpecificSurfaceAreaM2M3 = 250.0, VoidFraction = 0.975, NominalHetpM = 0.45, ReferenceSourceId = 1, ValidatedFlag = false }
            );
        }
    }
}