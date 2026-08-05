namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Resolved per-species Henry's Law data for the Van 't Hoff
    /// temperature correction. HeatOfSolutionKJmol is nullable because
    /// HenrysLawData seeds it as NULL for every pollutant today (see
    /// HenrysLawData.cs) — callers must fall back to the existing
    /// hardcoded tempCoeff when it's missing, not treat null as zero.
    /// </summary>
    public sealed class HenrysLawSpeciesData
    {
        public string PollutantCode { get; set; } = "";
        public double H_ReferenceAt25C { get; set; }
        public double? HeatOfSolutionKJmol { get; set; }
    }

    /// <summary>
    /// Mirrors IComponentPropertyLookup's shape (GasMixtureBuilder.cs) —
    /// same two-hop reasoning: PollutantInputViewModel.PollutantType is
    /// a plain int FK, not a code string, so callers need the
    /// Pollutant.Id -> Pollutant.Code -> HenrysLawData hop too.
    /// </summary>
    public interface IHenrysLawLookup
    {
        HenrysLawSpeciesData? GetByPollutantCode(string code);

        HenrysLawSpeciesData? GetByPollutantId(int pollutantId);
    }
}