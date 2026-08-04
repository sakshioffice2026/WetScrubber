namespace WetScrubber.Business.Diagnostics
{
    public enum FindingSeverity
    {
        Info,
        Warning,
        Critical
    }

    /// <summary>
    /// One row of the doctor's "diagnosis + recommendation" — always the
    /// output of a deterministic rule, never something an LLM invented.
    /// </summary>
    public sealed class DesignFinding
    {
        public string Code { get; init; } = string.Empty;
        public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
        public string Symptom { get; init; } = string.Empty;
        public string Diagnosis { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;
    }
}
