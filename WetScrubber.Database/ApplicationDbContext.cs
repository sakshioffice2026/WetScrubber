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

        }
    }
}
