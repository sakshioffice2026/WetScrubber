using Xunit;
using Microsoft.Extensions.Configuration;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Business.Venturi;
using WetScrubber.Database.Enums;
using WetScrubber.Models;
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
        // Calvert: dP[cmH2O] = 8.24e-4 * (6000 cm/s)^2 * (1 L/m3 / 1000) = 29.664 cmH2O = 2909 Pa
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
        Assert.InRange(result.PressureDrop, 2900.0, 2920.0);
        Assert.InRange(result.CollectionEfficiency, 0.0, 100.0);
        Assert.InRange(result.DropletDiameterMicron, 100.0, 125.0);
    }

    [Fact]
    public void Venturi_Johnstone_Efficiency_MatchesHandCalculation()
    {
        // 1 um, 2000 kg/m3, 60 m/s, L/G 1 L/m3, 293.15 K, 101325 Pa, mu 1.95e-5:
        // C = 1.176, d_d = 111.5 um, psi = C*rho*v*dp^2/(18*mu*d_d) = 3.61,
        // eta = 1 - exp(-1.12 * sqrt(psi)) = 88.1 %
        var engine = new ScrubberCalculationEngine();

        var result = engine.CalculateVenturiSizing(
            3.0, 60.0, 1.0, 1.09, 2000.0, 1.0, 1000.0, 1.95e-5);

        Assert.InRange(result.CollectionEfficiency, 86.5, 89.5);
    }

    [Fact]
    public void Venturi_LargeParticle_AtHighVelocity_IsNearlyFullyCollected()
    {
        var engine = new ScrubberCalculationEngine();

        var result = engine.CalculateVenturiSizing(
            3.0, 80.0, 1.0, 1.2, 1500.0, 5.0, 1000.0, 1.81e-5);

        Assert.InRange(result.CollectionEfficiency, 99.0, 100.0);
    }

    [Fact]
    public void Venturi_Efficiency_IncreasesWithLiquidToGasRatio()
    {
        double Eff(double lg) => VenturiCollectionModel.Calculate(
            60.0, lg, 1e-6, 2000.0, 1.95e-5).CollectionEfficiencyPercent;

        Assert.True(Eff(1.5) > Eff(1.0));
    }

    [Fact]
    public void Venturi_Model_FlagsThroatVelocityOutsideUsualRange()
    {
        var result = VenturiCollectionModel.Calculate(
            30.0, 1.0, 2e-6, 2000.0, 1.95e-5);

        Assert.Contains(result.Warnings, w => w.Contains("Throat velocity"));
    }

    [Fact]
    public void NukiyamaTanasawa_Diameter_MatchesHandCalculation()
    {
        // 60 m/s, 1 L/m3, water: 82.7 um + 28.7 um = 111.5 um
        double d = VenturiCollectionModel.NukiyamaTanasawaDiameterM(
            60.0, 1.0, 1000.0, 1.0e-3, 0.072);

        Assert.InRange(d, 108e-6, 115e-6);
    }

    [Fact]
    public void Cunningham_Slip_IsAboveOne_AndFallsWithParticleSize()
    {
        double small = VenturiCollectionModel.CunninghamSlip(0.5e-6);
        double large = VenturiCollectionModel.CunninghamSlip(5e-6);

        Assert.True(small > large);
        Assert.True(large > 1.0);
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

    private sealed class StubComponentLookup : IComponentPropertyLookup
    {
        private static readonly Dictionary<int, string> Codes = new()
        {
            [1] = "SO2",
            [2] = "NH3",
            [3] = "HCl"
        };

        public EosComponentInput? GetByCode(string code)
            => new EosComponentInput { Code = code };

        public EosComponentInput? GetByPollutantId(int pollutantId)
            => Codes.TryGetValue(pollutantId, out var code)
                ? new EosComponentInput { Code = code }
                : null;
    }

    private static ScrubberCalculationEngine SprayEngine()
        => new ScrubberCalculationEngine(null!, new StubComponentLookup());

    private static PollutantInputViewModel SprayPollutant(
        int type,
        double molecularWeight,
        double henry,
        double targetRemovalPercent,
        double inletPpm = 1000.0)
        => new PollutantInputViewModel
        {
            PollutantType = type,
            MolecularWeight = molecularWeight,
            HenrysLawConstant = henry,
            InletConcentration = inletPpm,
            TargetOutletConcentration = 0.0,
            TargetRemovalEfficiency = targetRemovalPercent
        };

    private static CreateDesignViewModel SprayDesign(params PollutantInputViewModel[] pollutants)
        => new CreateDesignViewModel
        {
            ScrubberType = ScrubberType.SprayTower,
            ActualFlowRate = 36000.0,
            NormalFlowRate = 30000.0,
            InletTemperature = 25.0,
            InletPressure = 101325.0,
            GasDensity = 1.2,
            GasViscosity = 1.85e-5,
            LiquidTemperature = 25.0,
            LiquidDensity = 1000.0,
            LiquidMolecularWeightGMol = 18.015,
            CarrierGasCode = "Air",
            CarrierGasMolecularWeightGMol = 28.9647,
            LiquidToGasRatio = 3.0,
            Pollutants = pollutants.ToList()
        };

    [Fact]
    public void SprayEngine_NoPollutants_IsInfeasible()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(SprayDesign());

        Assert.False(result.IsFeasible);
        Assert.Contains("at least one pollutant", result.InfeasibilityReason);
    }

    [Fact]
    public void SprayEngine_MissingTarget_IsInfeasible_TargetRequired()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(
            SprayDesign(SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 0.0)));

        Assert.False(result.IsFeasible);
        Assert.Contains("TARGET REQUIRED", result.InfeasibilityReason);
    }

    [Fact]
    public void SprayEngine_TargetOutletConcentration_IsAcceptedInPlaceOfRemoval()
    {
        ResetSettings();
        var pollutant = SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 0.0);
        pollutant.TargetOutletConcentration = 100.0;

        var result = SprayEngine().RunCalculation(SprayDesign(pollutant));

        Assert.DoesNotContain("TARGET REQUIRED", result.InfeasibilityReason ?? string.Empty);
    }

    [Fact]
    public void SprayEngine_TargetBeyondEquilibrium_IsInfeasible_WithSprayWarning()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(
            SprayDesign(SprayPollutant(1, 64.0, 0.83, targetRemovalPercent: 90.0)));

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Warnings, w => w.Contains("SPRAY TOWER"));
        Assert.True(result.RemovalEfficiency < 90.0);
    }

    [Fact]
    public void SprayEngine_SolublePollutant_MeetsTarget_WithinHeightCap()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(
            SprayDesign(SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 90.0)));

        Assert.True(result.IsFeasible);
        Assert.InRange(result.TowerHeight, 0.01, 15.0);
        Assert.True(result.RemovalEfficiency >= 89.9);
    }

    [Fact]
    public void SprayEngine_MultiplePollutants_SizesOnGoverningPollutant()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(
            SprayDesign(
                SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 90.0),
                SprayPollutant(2, 17.0, 0.001, targetRemovalPercent: 99.5)));

        Assert.Contains(result.Warnings, w => w.Contains("GOVERNING POLLUTANT") && w.Contains("'NH3'"));
    }

    [Fact]
    public void SprayEngine_MultiplePollutants_RemovalIsConcentrationWeighted()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(
            SprayDesign(
                SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 90.0),
                SprayPollutant(2, 17.0, 0.001, targetRemovalPercent: 99.5)));

        Assert.True(result.IsFeasible);
        Assert.InRange(result.RemovalEfficiency, (90.0 + 99.5) / 2.0 - 0.1, 100.0);
    }

    [Fact]
    public void SprayEngine_MultiplePollutants_UnequalInlet_WeightsByInletConcentration()
    {
        ResetSettings();

        var heavyOnTightTarget = SprayEngine().RunCalculation(
            SprayDesign(
                SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 90.0, inletPpm: 100.0),
                SprayPollutant(2, 17.0, 0.001, targetRemovalPercent: 99.5, inletPpm: 1900.0)));
        var heavyOnLooseTarget = SprayEngine().RunCalculation(
            SprayDesign(
                SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 90.0, inletPpm: 1900.0),
                SprayPollutant(2, 17.0, 0.001, targetRemovalPercent: 99.5, inletPpm: 100.0)));

        Assert.True(heavyOnTightTarget.RemovalEfficiency > heavyOnLooseTarget.RemovalEfficiency);
    }

    [Fact]
    public void SprayEngine_UnknownPollutantCode_Throws()
    {
        ResetSettings();

        Assert.Throws<InvalidOperationException>(
            () => SprayEngine().RunCalculation(
                SprayDesign(SprayPollutant(99, 64.0, 0.001, targetRemovalPercent: 90.0))));
    }

    private static CreateDesignViewModel VenturiDesign(params PollutantInputViewModel[] pollutants)
    {
        var vm = SprayDesign(pollutants);
        vm.ScrubberType = ScrubberType.VenturiScrubber;
        vm.VenturiThroatVelocityMs = 80.0;
        vm.VenturiParticleDiameterMicron = 2.0;
        vm.VenturiParticleDensityKgM3 = 1500.0;
        vm.LiquidToGasRatio = 1.0;
        return vm;
    }

    [Fact]
    public void Venturi_ImpactionParameter_UsesDivisorEighteen()
    {
        // 1 * 1000 * 10 * (1e-6)^2 / (18 * 1e-5 * 1e-4) = 0.5556
        double psi = VenturiCollectionModel.ImpactionParameter(
            1.0, 1000.0, 10.0, 1e-6, 1e-5, 1e-4);

        Assert.Equal(0.5556, psi, precision: 4);
    }

    [Fact]
    public void Venturi_MeanFreePath_AirAtAmbient_IsAboutSixtyFiveNanometres()
    {
        double lambda = VenturiCollectionModel.MeanFreePathM(1.81e-5, 293.15, 101325.0);

        Assert.InRange(lambda, 0.060e-6, 0.070e-6);
    }

    [Fact]
    public void Venturi_MeanFreePath_RisesWithTemperature_AndFallsWithPressure()
    {
        double ambient = VenturiCollectionModel.MeanFreePathM(1.81e-5, 293.15, 101325.0);
        double hot = VenturiCollectionModel.MeanFreePathM(1.81e-5, 400.0, 101325.0);
        double pressurised = VenturiCollectionModel.MeanFreePathM(1.81e-5, 293.15, 200000.0);

        Assert.True(hot > ambient);
        Assert.True(pressurised < ambient);
    }

    [Fact]
    public void Venturi_Cunningham_IsLargerAtLowerPressure()
    {
        double atmospheric = VenturiCollectionModel.CunninghamSlip(
            1e-6, VenturiCollectionModel.MeanFreePathM(1.81e-5, 293.15, 101325.0));
        double reduced = VenturiCollectionModel.CunninghamSlip(
            1e-6, VenturiCollectionModel.MeanFreePathM(1.81e-5, 293.15, 60000.0));

        Assert.True(reduced > atmospheric);
    }

    [Fact]
    public void Venturi_Model_ReportsMeanFreePathUsedForSlip()
    {
        var result = VenturiCollectionModel.Calculate(
            60.0, 1.0, 1e-6, 2000.0, 1.81e-5,
            gasTemperatureK: 293.15, gasPressurePa: 101325.0);

        Assert.Equal(
            VenturiCollectionModel.MeanFreePathM(1.81e-5, 293.15, 101325.0),
            result.MeanFreePathM,
            precision: 12);
    }

    [Fact]
    public void Venturi_SizeDistribution_ResultStaysWithinZeroToHundred()
    {
        var result = VenturiCollectionModel.Calculate(
            80.0, 1.0, 2e-6, 1500.0, 1.81e-5, geometricStdDev: 2.0);

        Assert.InRange(result.CollectionEfficiencyPercent, 0.0, 100.0);
        Assert.Equal(2.0, result.GeometricStdDev, precision: 9);
    }

    [Fact]
    public void Venturi_SizeDistribution_FinesLowerOverallEfficiencyBelowMonodisperse()
    {
        var mono = VenturiCollectionModel.Calculate(
            80.0, 1.0, 5e-6, 1500.0, 1.81e-5, geometricStdDev: 1.0);
        var broad = VenturiCollectionModel.Calculate(
            80.0, 1.0, 5e-6, 1500.0, 1.81e-5, geometricStdDev: 3.0);

        Assert.True(broad.CollectionEfficiencyPercent < mono.CollectionEfficiencyPercent);
    }

    [Fact]
    public void Venturi_SizeDistribution_DefaultIsMonodisperse()
    {
        var implicitMono = VenturiCollectionModel.Calculate(80.0, 1.0, 2e-6, 1500.0, 1.81e-5);
        var explicitMono = VenturiCollectionModel.Calculate(
            80.0, 1.0, 2e-6, 1500.0, 1.81e-5, geometricStdDev: 1.0);

        Assert.Equal(
            implicitMono.CollectionEfficiencyPercent,
            explicitMono.CollectionEfficiencyPercent,
            precision: 12);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void Venturi_SizeDistribution_RejectsGeometricStdDevBelowOne(double gsd)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VenturiCollectionModel.Calculate(
                80.0, 1.0, 2e-6, 1500.0, 1.81e-5, geometricStdDev: gsd));
    }

    [Fact]
    public void Venturi_Settings_GeometricStdDevDefaultsToMonodisperse()
    {
        ResetSettings();

        Assert.Equal(1.0, DesignBasisSettings.VenturiParticleGeometricStdDev);
    }

    [Fact]
    public void VenturiEngine_ParticulateOnly_IsFeasible_WithValidEfficiencyAndPressureDrop()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(VenturiDesign());

        Assert.True(result.IsFeasible);
        Assert.InRange(result.RemovalEfficiency, 0.0, 100.0);
        Assert.True(result.PressureDrop > 0.0);
        Assert.True(result.TowerDiameter > 0.0);
        Assert.True(result.TowerHeight > 0.0);
    }

    [Fact]
    public void VenturiEngine_GaseousPollutant_IsInfeasible_ScopeReported()
    {
        ResetSettings();

        var result = SprayEngine().RunCalculation(
            VenturiDesign(SprayPollutant(1, 64.0, 0.001, targetRemovalPercent: 90.0)));

        Assert.False(result.IsFeasible);
        Assert.Contains("VENTURI SCOPE", result.InfeasibilityReason);
        Assert.Contains("'SO2'", result.InfeasibilityReason);
    }

    [Fact]
    public void VenturiEngine_ThroatVelocityOutsideRange_IsInfeasible()
    {
        ResetSettings();
        var vm = VenturiDesign();
        vm.VenturiThroatVelocityMs = 30.0;

        var result = SprayEngine().RunCalculation(vm);

        Assert.False(result.IsFeasible);
        Assert.Contains("throat velocity", result.InfeasibilityReason);
    }

    [Fact]
    public void VenturiEngine_HigherThroatVelocity_RaisesPressureDrop()
    {
        ResetSettings();
        var slow = VenturiDesign();
        slow.VenturiThroatVelocityMs = 70.0;
        var fast = VenturiDesign();
        fast.VenturiThroatVelocityMs = 100.0;

        var slowResult = SprayEngine().RunCalculation(slow);
        var fastResult = SprayEngine().RunCalculation(fast);

        Assert.True(fastResult.PressureDrop > slowResult.PressureDrop);
    }
}