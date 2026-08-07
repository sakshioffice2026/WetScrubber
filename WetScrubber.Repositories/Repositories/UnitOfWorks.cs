using WetScrubber.Database;
using WetScrubber.Repositories.Contracts;

namespace WetScrubber.Repositories.Repositories
{
    // Mirrors CF's UnitOfWorks: holds one shared DbContext, constructs each
    // repository with it, and Commit() saves everything at once. Register it
    // scoped in Program.cs (see the DI note).
    public class UnitOfWorks : IUnitOfWork
    {
        private readonly ApplicationDbContext _DbContext;

        public PollutantRepository pollutantRepository { get; private set; }
        public ScrubbingLiquidRepository scrubbingLiquidRepository { get; private set; }
        public ChemicalReactionRepository chemicalReactionRepository { get; private set; }
        public DesignOutcomeRepository designOutcomeRepository { get; private set; }

        public UnitOfWorks(ApplicationDbContext context)
        {
            _DbContext = context;
            pollutantRepository = new PollutantRepository(_DbContext);
            scrubbingLiquidRepository = new ScrubbingLiquidRepository(_DbContext);
            chemicalReactionRepository = new ChemicalReactionRepository(_DbContext);
            designOutcomeRepository = new DesignOutcomeRepository(_DbContext);
        }

        public async Task Commit()
        {
            await _DbContext.SaveChangesAsync();
        }

        public void Dispose()
        {
            // DbContext lifetime is managed by DI (scoped) — don't dispose here,
            // same as CF's UnitOfWorks.
        }
    }
}