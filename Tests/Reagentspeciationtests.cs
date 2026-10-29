// PATH IN REPO: Tests/ReagentSpeciationTests.cs  (new file)
using WetScrubber.Business.Thermodynamics;
using Xunit;

namespace WetScrubber.Business.Tests;

public sealed class ReagentSpeciationTests
{
    [Fact]
    public void SulfurousAcid_pH7p2_IsOnePointFive() =>
        Assert.Equal(1.5, AcidBaseSpeciation.DiproticBaseEquivalentsPerMol(
            7.2, AcidBaseSpeciation.PKa1SulfurousAcid, AcidBaseSpeciation.PKa2SulfurousAcid), 3);

    [Fact]
    public void SulfurousAcid_pH7p0_Is1p39() =>
        Assert.Equal(1.39, AcidBaseSpeciation.DiproticBaseEquivalentsPerMol(
            7.0, AcidBaseSpeciation.PKa1SulfurousAcid, AcidBaseSpeciation.PKa2SulfurousAcid), 2);

    [Fact]
    public void SulfurousAcid_pH8p0_Is1p86() =>
        Assert.Equal(1.86, AcidBaseSpeciation.DiproticBaseEquivalentsPerMol(
            8.0, AcidBaseSpeciation.PKa1SulfurousAcid, AcidBaseSpeciation.PKa2SulfurousAcid), 2);

    [Fact]
    public void SulfurousAcid_pH8p5_Is1p95() =>
        Assert.Equal(1.95, AcidBaseSpeciation.DiproticBaseEquivalentsPerMol(
            8.5, AcidBaseSpeciation.PKa1SulfurousAcid, AcidBaseSpeciation.PKa2SulfurousAcid), 2);

    [Fact]
    public void SolvePh_PureNaOH_0p1M_Is13() =>
        Assert.Equal(13.0, AcidBaseSpeciation.SolvePh(0.1, 0.0, 0.0, 0.0), 2);

    [Fact]
    public void SolvePh_SodiumBisulfite_0p1M_IsAmphiproticRegion()
    {
        double pH = AcidBaseSpeciation.SolvePh(0.1, 0.1, 0.0, 0.0);
        Assert.InRange(pH, 4.2, 4.9);
    }

    private static ReagentSufficiencyInput So2Case() => new ReagentSufficiencyInput
    {
        Reagent = ReagentKind.Caustic,
        PollutantCode = "SO2",
        ReagentEquivalentsPerL = 0.5,
        StoichiometricRatio = 2.0,
        InletGasMoleFraction = 350e-6,
        OutletGasMoleFraction = 17.5e-6,
        GasKmolPerM3 = 0.0377,
        LiquidToGasLPerM3 = 3.8
    };

    [Fact]
    public void Evaluate_So2_ControlPh7p5_MinimumLG_Matches_HandCalc()
    {
        var input = So2Case();
        input.ControlPh = 7.5;                       // 1.666 eq NaOH per mol SO2
        var r = ReagentSufficiencyService.Evaluate(input);
        Assert.Equal(0.042, r.MinimumLiquidToGasLPerM3, 3);   // 0.04177 L/m3
    }

    [Fact]
    public void Evaluate_So2_WithCo2_WorstCase_MinimumLG_Matches_HandCalc()
    {
        var input = So2Case();                       // ControlPh unset -> 2 eq/mol SO2, 2 eq/mol CO2
        input.InletCo2MoleFraction = 0.12;
        input.Co2AbsorbedFraction = 0.01;
        var r = ReagentSufficiencyService.Evaluate(input);
        Assert.Equal(0.231, r.MinimumLiquidToGasLPerM3, 3);   // 0.2311 L/m3
    }

    [Fact]
    public void Evaluate_WithoutCo2_UnchangedFromStoichiometricRatio()
    {
        var input = So2Case();                       // 2 eq/mol: 2 * 332.5e-6 * 0.0377 / 0.5 * 1000
        var r = ReagentSufficiencyService.Evaluate(input);
        Assert.Equal(0.050, r.MinimumLiquidToGasLPerM3, 3);   // 0.05014 L/m3
    }
}