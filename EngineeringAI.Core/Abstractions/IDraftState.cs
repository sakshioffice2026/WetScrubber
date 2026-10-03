namespace EngineeringAI.Core.Abstractions;

public interface IDraftState
{
    string SessionId { get; set; }

    DateTime LastUpdatedUtc { get; set; }

    IReadOnlyList<string> GetMissingMandatoryFields();
}