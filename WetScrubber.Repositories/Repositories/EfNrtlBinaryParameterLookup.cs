using System.Linq;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Repositories
{
    // Reads NrtlBinaryParameters (Phase 0 — currently empty, see
    // NrtlBinaryParameter.cs) and orients the result to the
    // (componentACode, componentBCode) order the caller asked for,
    // swapping Tau_AB/Tau_BA when the row was stored the other way
    // round, so LiquidActivityBuilder never has to know which order
    // the row happened to be entered in.
    public class EfNrtlBinaryParameterLookup : INrtlBinaryParameterLookup
    {
        private readonly ApplicationDbContext _dbContext;

        public EfNrtlBinaryParameterLookup(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public NrtlBinaryInput? GetPair(string componentACode, string componentBCode)
        {
            var forward = _dbContext.NrtlBinaryParameters.FirstOrDefault(n =>
                n.ComponentACode == componentACode && n.ComponentBCode == componentBCode && n.IsActive);

            if (forward != null)
            {
                return new NrtlBinaryInput
                {
                    Tau_AB = forward.Tau_AB,
                    Tau_BA = forward.Tau_BA,
                    Alpha = forward.Alpha
                };
            }

            var reversed = _dbContext.NrtlBinaryParameters.FirstOrDefault(n =>
                n.ComponentACode == componentBCode && n.ComponentBCode == componentACode && n.IsActive);

            if (reversed != null)
            {
                // Row was stored (B, A) — swap so Tau_AB still means
                // "A-in-B" from the caller's requested orientation.
                return new NrtlBinaryInput
                {
                    Tau_AB = reversed.Tau_BA,
                    Tau_BA = reversed.Tau_AB,
                    Alpha = reversed.Alpha
                };
            }

            return null;
        }
    }
}