using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.Tests;

public sealed class Phase5RegressionTests
{
    [Fact]
    public void StructuredPacking_UsesKremserStagesAndNominalHetp()
    {
        var result = StructuredPackingHeightEstimator.Calculate(
            absorptionFactor: 2.0,
            inletGasMoleFraction: 0.10,
            outletGasMoleFraction: 0.025,
            hetpM: 0.45);

        Assert.True(result.IsFeasible);
        Assert.Equal(1.3219, result.TheoreticalStages, precision: 4);
        Assert.Equal(0.5949, result.PackingHeightM, precision: 4);
    }

    [Fact]
    public void StructuredPacking_RejectsAnUnachievableTarget()
    {
        var result = StructuredPackingHeightEstimator.Calculate(
            absorptionFactor: 0.5,
            inletGasMoleFraction: 0.10,
            outletGasMoleFraction: 0.025,
            hetpM: 0.45);

        Assert.False(result.IsFeasible);
    }

    [Fact]
    public void LimestoneSlurry_IncreasesDensityAndApparentViscosity()
    {
        var result = LimestoneSlurryHydraulics.Calculate(
            carrierLiquidDensityKgM3: 1000,
            carrierLiquidViscosityMPas: 1.0,
            solidsLoadingWtPercent: 15.0);

        Assert.InRange(result.SolidsVolumeFraction, 0.05, 0.07);
        Assert.True(result.DensityKgM3 > 1000);
        Assert.True(result.ApparentViscosityMPas > 1.0);
    }
}
