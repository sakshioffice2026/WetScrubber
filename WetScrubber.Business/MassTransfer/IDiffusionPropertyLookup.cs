using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.MassTransfer
{
    public interface IDiffusionPropertyLookup
    {
        DiffusionSpeciesData? GetByComponentCode(string code);
    }
}