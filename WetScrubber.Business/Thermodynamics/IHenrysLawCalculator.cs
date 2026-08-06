namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Single source of truth for the Van 't Hoff temperature correction
    /// to Henry's Law: H(T) = H_ref * exp( -(deltaH_soln/R) * (1/T - 1/T_ref) ).
    ///
    /// This exact formula is currently duplicated inline in
    /// WetScrubber.Services.ScrubberCalculationEngine at four call sites
    /// (GetHenrysLawConstant + three copies of the tempCoeff conversion
    /// in TryComputeIterativeTowerSolution / TryComputeMultiPollutant...).
    /// That duplication is what this interface exists to remove — new
    /// callers should depend on this instead of re-deriving the formula.
    ///
    /// NOTE: existing call sites in ScrubberCalculationEngine have NOT
    /// been rewired to use this yet. They still work correctly (the
    /// formula is duplicated correctly, not incorrectly), this is a
    /// structural follow-up, not a bug fix.
    /// </summary>
    public interface IHenrysLawCalculator
    {
        /// <summary>
        /// Returns H(T) given a reference constant at 25 C and an
        /// optional heat of solution. When heatOfSolutionKJmol is null,
        /// callers should decide their own fallback (existing engine
        /// behavior: fall back to a shared default tempCoeff) — this
        /// method does NOT silently substitute a guessed heat of
        /// solution, consistent with HenrysLawData.cs's governance rule.
        /// </summary>
        double GetTemperatureCorrectedHenrysConstant(
            double referenceHenrysConstantAt25C,
            double? heatOfSolutionKJmol,
            double temperatureC,
            double fallbackTempCoeffK);
    }
}