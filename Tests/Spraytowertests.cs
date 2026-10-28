using Xunit;
using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.Tests;

public sealed class SprayTowerTests
{
    private static SprayTowerTransferResult Spray(
        double absorptionFactor,
        double targetOutletFraction,
        double towerHeightCapM = 15.0,
        bool evaluateAtTowerHeight = false,
        double liquidFluxM3M2S = 0.002,
        double? relativeVelocityMs = 2.0,
        double gasVelocityMs = 0.8)
        => SprayTowerDropletTransfer.Calculate(
            dropletDiameterM: 0.001,
            dropletVsGasRelativeVelocityMs: relativeVelocityMs,
            gasVelocityMs: gasVelocityMs,
            liquidVolumetricFluxM3M2S: liquidFluxM3M2S,
            gasDensityKgM3: 1.2,
            liquidDensityKgM3: 1000.0,
            gasViscosityPas: 1.8e-5,
            gasDiffusivityM2S: 1.0e-5,
            absorptionFactor: absorptionFactor,
            targetOutletFraction: targetOutletFraction,
            towerHeightCapM: towerHeightCapM,
            evaluateAtTowerHeight: evaluateAtTowerHeight);

    [Fact]
    public void OutletFractionAtNtu_ZeroNtu_ReturnsOne()
    {
        Assert.Equal(1.0, SprayTowerDropletTransfer.OutletFractionAtNtu(0.0, 2.0), precision: 9);
    }

    [Fact]
    public void OutletFractionAtNtu_AbsorptionFactorOne_MatchesClosedForm()
    {
        Assert.Equal(0.25, SprayTowerDropletTransfer.OutletFractionAtNtu(3.0, 1.0), precision: 9);
    }

    [Fact]
    public void OutletFractionAtNtu_AbsorptionFactorBelowOne_ApproachesEquilibriumLimit()
    {
        double outlet = SprayTowerDropletTransfer.OutletFractionAtNtu(200.0, 0.5);

        Assert.Equal(0.5, outlet, precision: 6);
    }

    [Fact]
    public void OutletFractionAtNtu_NeverBelowEquilibriumLimit()
    {
        foreach (double ntu in new[] { 0.5, 2.0, 10.0, 50.0, 500.0 })
        {
            double outlet = SprayTowerDropletTransfer.OutletFractionAtNtu(ntu, 0.8);

            Assert.True(outlet >= 0.2 - 1e-9);
            Assert.True(outlet <= 1.0);
        }
    }

    [Theory]
    [InlineData(2.0, 0.1)]
    [InlineData(4.0, 0.01)]
    [InlineData(1.0, 0.25)]
    [InlineData(1.5, 0.05)]
    public void RequiredNtu_RoundTripsThroughOutletFraction(double absorptionFactor, double target)
    {
        double ntu = SprayTowerDropletTransfer.RequiredNtu(target, absorptionFactor);

        Assert.Equal(
            target,
            SprayTowerDropletTransfer.OutletFractionAtNtu(ntu, absorptionFactor),
            precision: 6);
    }

    [Fact]
    public void RequiredNtu_AbsorptionFactorOne_MatchesHandCalculation()
    {
        Assert.Equal(3.0, SprayTowerDropletTransfer.RequiredNtu(0.25, 1.0), precision: 9);
    }

    [Fact]
    public void RequiredNtu_TargetAtOrBelowEquilibriumLimit_IsInfinite()
    {
        Assert.True(double.IsPositiveInfinity(SprayTowerDropletTransfer.RequiredNtu(0.4, 0.5)));
        Assert.True(double.IsPositiveInfinity(SprayTowerDropletTransfer.RequiredNtu(0.5, 0.5)));
    }

    [Fact]
    public void Calculate_FeasibleTarget_IsMetAtRequiredHeight()
    {
        var r = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1);

        Assert.True(r.IsFeasible);
        Assert.Null(r.InfeasibilityReason);
        Assert.Equal(0.1, r.OutletFraction, precision: 6);
        Assert.Equal(90.0, r.RemovalEfficiencyPercent, precision: 4);
        Assert.Equal(r.RequiredHeightM, r.InstalledHeightM, precision: 9);
        Assert.True(r.RequiredHeightM > 0.0 && r.RequiredHeightM < 15.0);
    }

    [Fact]
    public void Calculate_HigherAbsorptionFactor_NeedsLessHeight()
    {
        var low = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1);
        var high = Spray(absorptionFactor: 4.0, targetOutletFraction: 0.1);

        Assert.True(high.RequiredHeightM < low.RequiredHeightM);
    }

    [Fact]
    public void Calculate_TargetBeyondEquilibriumLimit_IsInfeasible_AndNotNaN()
    {
        var r = Spray(absorptionFactor: 0.5, targetOutletFraction: 0.4);

        Assert.False(r.IsFeasible);
        Assert.False(string.IsNullOrWhiteSpace(r.InfeasibilityReason));
        Assert.True(double.IsPositiveInfinity(r.RequiredHeightM));
        Assert.False(double.IsNaN(r.RemovalEfficiencyPercent));
        Assert.True(r.OutletFraction >= 0.5 - 1e-9);
        Assert.True(r.RemovalEfficiencyPercent <= 50.0 + 1e-9);
    }

    [Fact]
    public void Calculate_HeightCap_IsReportedAsInfeasible_WithAbsorptionFactorAwareRemoval()
    {
        var r = Spray(absorptionFactor: 1.2, targetOutletFraction: 1e-4);

        Assert.False(r.IsFeasible);
        Assert.Contains("exceeds the configured cap", r.InfeasibilityReason);
        Assert.Equal(15.0, r.InstalledHeightM, precision: 9);
        Assert.True(r.RequiredHeightM > 15.0);
        Assert.True(r.OutletFraction > 1e-4);
        Assert.Equal(
            SprayTowerDropletTransfer.OutletFractionAtNtu(r.NTU, 1.2),
            r.OutletFraction,
            precision: 9);
    }

    [Fact]
    public void Calculate_EvaluateAtTowerHeight_UsesCapAsInstalledHeight()
    {
        var sized = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1);
        var rated = Spray(
            absorptionFactor: 2.0,
            targetOutletFraction: 0.1,
            towerHeightCapM: 10.0,
            evaluateAtTowerHeight: true);

        Assert.Equal(10.0, rated.InstalledHeightM, precision: 9);
        Assert.True(10.0 > sized.RequiredHeightM);
        Assert.True(rated.OutletFraction < 0.1);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void Calculate_RejectsTargetOutsideZeroOne(double target)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Spray(2.0, target));
    }

    [Fact]
    public void Calculate_RejectsHoldupAboveDilutePhaseLimit()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Spray(2.0, 0.1, liquidFluxM3M2S: 0.5));

        Assert.Equal("liquidVolumetricFluxM3M2S", ex.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Calculate_RejectsNonPositiveAbsorptionFactor(double absorptionFactor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Spray(absorptionFactor, 0.1));
    }

    [Fact]
    public void Calculate_RejectsLiquidLighterThanGas()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SprayTowerDropletTransfer.Calculate(
                dropletDiameterM: 0.001,
                dropletVsGasRelativeVelocityMs: 2.0,
                gasVelocityMs: 0.8,
                liquidVolumetricFluxM3M2S: 0.002,
                gasDensityKgM3: 1000.0,
                liquidDensityKgM3: 1.2,
                gasViscosityPas: 1.8e-5,
                gasDiffusivityM2S: 1.0e-5,
                absorptionFactor: 2.0,
                targetOutletFraction: 0.1));
    }

    [Fact]
    public void Calculate_TerminalVelocity_IsPlausibleForOneMillimetreWaterDroplet()
    {
        var r = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1);

        Assert.InRange(r.TerminalVelocityMS, 3.5, 5.5);
    }

    [Fact]
    public void Calculate_SpecificArea_MatchesSixHoldupOverDiameter()
    {
        // holdup = 0.002 / 2.0 = 0.001; a = 6 * 0.001 / 0.001 = 6 m2/m3
        var r = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1);

        Assert.Equal(6.0, r.SpecificInterfacialAreaM2M3, precision: 9);
    }

    [Fact]
    public void Calculate_ExplicitRelativeVelocity_IsUsedAsSlipVelocity()
    {
        var r = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1, relativeVelocityMs: 2.0);

        Assert.Equal(2.0, r.SlipVelocityMS, precision: 9);
        Assert.Equal(0.001, r.HoldupFraction, precision: 9);
    }

    [Fact]
    public void Calculate_NullRelativeVelocity_UsesTerminalVelocityAsSlip()
    {
        var r = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1, relativeVelocityMs: null);

        Assert.Equal(r.TerminalVelocityMS, r.SlipVelocityMS, precision: 9);
    }

    [Fact]
    public void Calculate_NullRelativeVelocity_HoldupUsesTerminalMinusGasVelocity()
    {
        var r = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1, relativeVelocityMs: null);

        double expectedHoldup = 0.002 / (r.TerminalVelocityMS - 0.8);

        Assert.Equal(expectedHoldup, r.HoldupFraction, precision: 9);
        Assert.Equal(6.0 * expectedHoldup / 0.001, r.SpecificInterfacialAreaM2M3, precision: 6);
    }

    [Fact]
    public void Calculate_NullRelativeVelocity_HigherGasVelocity_RaisesHoldup()
    {
        var slow = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1, relativeVelocityMs: null, gasVelocityMs: 0.5);
        var fast = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1, relativeVelocityMs: null, gasVelocityMs: 2.0);

        Assert.True(fast.HoldupFraction > slow.HoldupFraction);
    }

    [Fact]
    public void Calculate_NullRelativeVelocity_GasVelocityAboveTerminal_IsInfeasible()
    {
        var r = Spray(absorptionFactor: 2.0, targetOutletFraction: 0.1, relativeVelocityMs: null, gasVelocityMs: 8.0);

        Assert.False(r.IsFeasible);
        Assert.Contains("terminal velocity", r.InfeasibilityReason);
        Assert.True(double.IsPositiveInfinity(r.RequiredHeightM));
        Assert.Equal(0.0, r.RemovalEfficiencyPercent, precision: 9);
        Assert.Equal(1.0, r.OutletFraction, precision: 9);
    }
}