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

        // ── Phase 4c — Flowsheet framework ──────────────────────────
        public DbSet<FlowsheetEntity> Flowsheets { get; set; }
        public DbSet<UnitOperationEntity> UnitOperations { get; set; }
        public DbSet<StreamConnectionEntity> StreamConnections { get; set; }

        // Phase 2 — uncomment when Project model is added
        // public DbSet<Project> Projects { get; set; }

        //AI Narrative
        public DbSet<DesignReport> DesignReports { get; set; }

        // ── Phase 0 — thermodynamic/property foundation ────────────
        public DbSet<ComponentProperty> ComponentProperties { get; set; }
        public DbSet<HenrysLawData> HenrysLawData { get; set; }
        public DbSet<NrtlBinaryParameter> NrtlBinaryParameters { get; set; }
        public DbSet<DiffusionProperty> DiffusionProperties { get; set; }
        public DbSet<Packing> Packings { get; set; }
        public DbSet<ReferenceSource> ReferenceSources { get; set; }

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
                },
                new ReferenceSource
                {
                    Id = 3,
                    Citation = "Rumble, J.R. (ed.), NIST-JANAF-class critical review: \"Thermodynamics of Solution of SO2(g) in Water and of Aqueous Sulfur Dioxide Solutions\", hosted by NIST (PMC6658418 / NIST J. Res. 90(5), 1985). Selected value for SO2(g) = SO2(aq): deltaH ~= -26.97 +/- 0.30 kJ/mol at 298.15 K. Independently retrievable at https://pmc.ncbi.nlm.nih.gov/articles/PMC6658418/ — cross-check against a second primary source (e.g. DIPPR) before flipping ValidatedFlag.",
                    SourceType = "NIST"
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
            // codebase, not new claims.
            //
            // HeatOfSolutionKJmol: SO2 is now populated from an actual
            // literature search (NIST-hosted critical review, see
            // ReferenceSourceId=3 above) — this is a real citable number,
            // not a recalled one, but still ValidatedFlag=false pending a
            // second-source cross-check per this table's own governance
            // rule. HCl/NH3/H2S/Cl2 are left NULL: a first search attempt
            // for HCl came back too ambiguous to attribute cleanly (the
            // NIST WebBook Henry's-law table did not resolve to a single
            // unambiguous species-specific row), and NH3/H2S/Cl2 haven't
            // been attempted yet. Do not fill these from memory — see
            // HenrysLawData.cs. The calculation engine already falls back
            // safely to its shared tempCoeff=2000 default for any species
            // with HeatOfSolutionKJmol == null (see
            // ScrubberCalculationEngine.GetVanTHoffTempCoeff), so leaving
            // these null is safe, just less accurate than a sourced value.
            modelBuilder.Entity<HenrysLawData>().HasData(
                new HenrysLawData { Id = 1, PollutantCode = "SO2", H_ReferenceAt25C = 0.0083, HeatOfSolutionKJmol = -26.97, ReferenceSourceId = 3, ValidatedFlag = false },
                new HenrysLawData { Id = 2, PollutantCode = "HCl", H_ReferenceAt25C = 0.00002, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false },
                new HenrysLawData { Id = 3, PollutantCode = "NH3", H_ReferenceAt25C = 0.00061, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false },
                new HenrysLawData { Id = 4, PollutantCode = "H2S", H_ReferenceAt25C = 0.0102, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false },
                new HenrysLawData { Id = 5, PollutantCode = "Cl2", H_ReferenceAt25C = 0.0074, HeatOfSolutionKJmol = null, ReferenceSourceId = 2, ValidatedFlag = false }
            );

            // NrtlBinaryParameter: deliberately NOT seeded. See
            // NrtlBinaryParameter.cs — fabricating tau/alpha values here
            // would be worse than leaving the table empty, since a wrong
            // NRTL parameter silently produces a wrong liquid activity
            // coefficient with no obvious symptom downstream.
        }
    }
}