using WetScrubber.Business.Cyclone;
using WetScrubber.Business.Particulate;

namespace WetScrubber.Business.Tests;

public sealed class WetCycloneBenchmarkTests
{
    // Textbook Lapple ratios (a=0.5, b=0.25, De=0.5, S=0.625, h=2, H=4, B=0.25) -> Ne = (1/0.5)(2 + 2/2) = 6
    private static readonly CycloneRatios TextbookLapple = new CycloneRatios
    {
        Name = "Lapple-textbook",
        Source = "Lapple (1951); Cooper and Alley",
        InletHeight = 0.5,
        InletWidth = 0.25,
        GasOutletDiameter = 0.5,
        OutletLength = 0.625,
        CylinderHeight = 2.0,
        ConeHeight = 2.0,
        DustOutletDiameter = 0.25
    };

    // Reference case: Q = 10,000 m3/h, T = 20 C, P = 101325 Pa, mu = 1.81e-5 Pa.s, rho_p = 2700 kg/m3, Lapple ratios
    // Hand calc: Vi = 22.22 m/s, rho_g = 1.2043, dP = 8 * 0.5 * rho_g * Vi^2 = 2379 Pa, d50 = 4.244 um
    private static WetCycloneInput Reference(double dM, CycloneRatios? ratios) => new WetCycloneInput
    {
        GasFlowM3s = 10000.0 / 3600.0,
        TemperatureK = 293.15,
        PressurePa = 101325.0,
        GasViscosityPas = 1.81e-5,
        ParticleDensityKgM3 = 2700.0,
        InletVelocityMs = 22.2222,
        Distribution = ParticleSizeDistribution.Monodisperse(dM),
        LiquidToGasRatioLM3 = 1.0,
        Ratios = ratios
    };

    [Fact]
    public void TextbookLapple_EffectiveTurnsIsSix()
    {
        var r = WetCycloneCollectionModel.Calculate(Reference(4.244e-6, TextbookLapple));
        Assert.Equal(6.0, r.EffectiveTurns, precision: 6);
    }

    [Fact]
    public void TextbookLapple_CutDiameter_MatchesHandCalc()
    {
        var r = WetCycloneCollectionModel.Calculate(Reference(4.244e-6, TextbookLapple));
        Assert.InRange(r.CutDiameterM * 1e6, 4.244 * 0.98, 4.244 * 1.02);
    }

    [Fact]
    public void TextbookLapple_PressureDrop_MatchesHandCalc()
    {
        var r = WetCycloneCollectionModel.Calculate(Reference(4.244e-6, TextbookLapple));
        Assert.InRange(r.PressureDropPa, 2379.0 * 0.98, 2379.0 * 1.02);
    }

    [Theory]
    [InlineData(0.5, 0.20)]
    [InlineData(1.0, 0.50)]
    [InlineData(2.0, 0.80)]
    [InlineData(3.0, 0.90)]
    public void TextbookLapple_GradeEfficiency_FollowsLappleCurve(double dOverD50, double expected)
    {
        double d50 = 4.244e-6;
        var r = WetCycloneCollectionModel.Calculate(Reference(dOverD50 * d50, TextbookLapple));
        Assert.Equal(expected, r.OverallEfficiency, precision: 2);
    }

    // Default ratios in the repo (CycloneRatios.Lapple) must equal the textbook Lapple set.
    [Fact]
    public void RepoDefaultLapple_EffectiveTurnsIsSix()
    {
        var r = WetCycloneCollectionModel.Calculate(Reference(4.244e-6, null));
        Assert.Equal(6.0, r.EffectiveTurns, precision: 6);
    }

    [Fact]
    public void RepoDefaultLapple_OutletLengthIsPointSixTwoFive()
    {
        Assert.Equal(0.625, CycloneRatios.Lapple.OutletLength, precision: 6);
    }

    [Fact]
    public void RepoDefaultLapple_CylinderAndConeHeightsAreTwoAndTwo()
    {
        Assert.Equal(2.0, CycloneRatios.Lapple.CylinderHeight, precision: 6);
        Assert.Equal(2.0, CycloneRatios.Lapple.ConeHeight, precision: 6);
    }
}