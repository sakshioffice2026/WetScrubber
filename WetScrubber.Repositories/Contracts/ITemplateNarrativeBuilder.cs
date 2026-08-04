using WetScrubber.Database;

namespace WetScrubber.Business.Reports
{
    public interface ITemplateNarrativeBuilder
    {
        string Build(ScrubberDesign design);
    }
}