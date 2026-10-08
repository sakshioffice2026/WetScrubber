using Xunit;
using Microsoft.Extensions.Configuration;
using WetScrubber.Business.Exceptions;
using WetScrubber.Services;

namespace WetScrubber.Business.Tests;

[Collection("DesignBasisSettings")]
public sealed class EngineAndSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static void ResetSettings() => DesignBasisSettings.Load(Config());

    [Fact]
    public void DesignBasis_Defaults_MatchPreviouslyHardCodedValues()
    {
        ResetSettings();

        Assert.Equal(5.0, DesignBasisSettings.PackedPumpHeadOffsetM);
        Assert.Equal(10.0, DesignBasisSettings.VenturiPumpHeadM);
        Assert.Equal(8.0, DesignBasisSettings.SprayTowerPumpHeadM);
        Assert.Equal(0.30, DesignBasisSettings.FreeboardFactor);
        Assert.Equal(2.0, DesignBasisSettings.SumpAndTopAllowanceM);
        Assert.Equal(300.0, DesignBasisSettings.SprayTowerAuxiliaryPressureDropPa);
        Assert.Equal(0.951, DesignBasisSettings.DefaultVoidFraction);
        Assert.Equal(0.0728, DesignBasisSettings.DefaultLiquidSurfaceTensionNM);
        Assert.Equal(0.8, DesignBasisSettings.SprayTowerDesignVelocityMs);
    }

    [Fact]
    public void DesignBasis_AcceptsConfiguredOverride()
    {
        DesignBasisSettings.Load(Config(("DesignBasis:PackedPumpHeadOffsetM", "7.5")));

        Assert.Equal(7.5, DesignBasisSettings.PackedPumpHeadOffsetM);

        ResetSettings();
    }

    [Theory]
    [InlineData("DesignBasis:FreeboardFactor", "-0.1")]
    [InlineData("DesignBasis:FreeboardFactor", "1.5")]
    [InlineData("DesignBasis:DefaultVoidFraction", "1.2")]
    [InlineData("DesignBasis:DefaultLiquidSurfaceTensionNM", "0")]
    [InlineData("DesignBasis:SprayTowerDesignVelocityMs", "10")]
    [InlineData("DesignBasis:PackedPumpHeadOffsetM", "NaN")]
    public void DesignBasis_RejectsOutOfRangeValues(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => DesignBasisSettings.Load(Config((key, value))));

        ResetSettings();
    }

    [Fact]
    public void Pump_Power_MatchesHandCalculation()
    {
        // 36 m3/hr = 0.01 m3/s -> 10 kg/s; 10 kg/s * 9.81 * 10 m / 0.70 = 1401.4 W
        var engine = new ScrubberCalculationEngine();

        double kw = engine.CalculatePumpPower(36.0, 10.0, 1000.0, 0.70);

        Assert.Equal(1.4014, kw, precision: 3);
    }

    [Fact]
    public void Venturi_Sizing_MatchesHandCalculation()
    {
        // 3 m3/s at 60 m/s -> 0.05 m2 -> D = sqrt(4*0.05/pi) = 0.2523 m
        // dP = rho*v^2/2 * (1 + (L/G/1000)*(rhoL/rhoG)) = 1962 * 1.9174 = 3762 Pa
        var engine = new ScrubberCalculationEngine();

        var result = engine.CalculateVenturiSizing(
            gasFlowRateM3S: 3.0,
            throatVelocityMs: 60.0,
            liquidToGasRatioLM3: 1.0,
            gasDensityKgM3: 1.09,
            particleDensityKgM3: 2000.0,
            particleDiameterMicron: 2.0,
            liquidDensityKgM3: 1000.0,
            gasViscosityPas: 1.95e-5);

        Assert.Equal(0.05, result.ThroatArea, precision: 6);
        Assert.Equal(0.2523, result.ThroatDiameter, precision: 3);
        Assert.InRange(result.PressureDrop, 3750.0, 3775.0);
        Assert.InRange(result.CollectionEfficiency, 0.0, 100.0);
    }

    [Theory]
    [InlineData(0.0, 60.0, 1.0, 1.09, 2000.0, 2.0)]
    [InlineData(3.0, 0.0, 1.0, 1.09, 2000.0, 2.0)]
    [InlineData(3.0, 60.0, 0.0, 1.09, 2000.0, 2.0)]
    [InlineData(3.0, 60.0, 1.0, 0.0, 2000.0, 2.0)]
    [InlineData(3.0, 60.0, 1.0, 1.09, 0.0, 2.0)]
    [InlineData(3.0, 60.0, 1.0, 1.09, 2000.0, 0.0)]
    public void Venturi_Throws_OnNonPositiveInputs(
        double flow, double velocity, double lg, double rhoG, double rhoP, double dpMicron)
    {
        var engine = new ScrubberCalculationEngine();

        Assert.Throws<PropertyOutOfBoundsException>(
            () => engine.CalculateVenturiSizing(
                flow, velocity, lg, rhoG, rhoP, dpMicron, 1000.0, 1.95e-5));
    }

    [Fact]
    public void Venturi_CollectionEfficiency_IncreasesWithParticleSize()
    {
        var engine = new ScrubberCalculationEngine();

        double Eff(double micron) => engine.CalculateVenturiSizing(
            3.0, 60.0, 1.0, 1.09, 2000.0, micron, 1000.0, 1.95e-5).CollectionEfficiency;

        Assert.True(Eff(5.0) > Eff(1.0));
    }
}