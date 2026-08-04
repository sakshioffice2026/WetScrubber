namespace WetScrubber.Business.AI
{
    public interface IAiPromptBuilder
    {
        string BuildSystemPrompt();

        string BuildUserPrompt(string deterministicReport);
    }
}