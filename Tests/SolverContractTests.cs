using Xunit;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.MassTransfer;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.Tests;

public sealed class SolverContractTests
{
    // 10,000 Nm3/hr at 50 C, 1,000 mg/Nm3 SO2.
    private const double MwSo2 = 64.06;
    private const double NormalMolarVolumeM3Kmol = 22.414;

    private static double MgPerNm3ToPpm(double mgPerNm3, double molecularWeight)
        => mgPerNm3 / molecularWeight * NormalMolarVolumeM3Kmol;

    private static MultiPollutantIterativeSolver.PollutantInput So2(double inletPpm)
        => new()
        {
            Code = "SO2",
            InletPpm = inletPpm,
            MolecularWeight = MwSo2,
            HenrysLawConstant = 40.0,
            HeatOfAbsorptionKJKmol = 35000.0
        };

    private static MultiPollutantIterativeSolver.SolverInput BaseInput(
        double towerHeightM,
        params MultiPollutantIterativeSolver.PollutantInput[] pollutants)
        => new()
        {
            Pollutants = pollutants.ToList(),
            GasTemperatureC = 50.0,
            GasMassFlowKgS = 3.6,
            GasDensityKgM3 = 1.09,
            GasViscosityPas = 1.95e-5,
            LiquidInletTempC = 30.0,
            LiquidMassFlowKgS = 10.8,
            LiquidDensityKgM3 = 1000.0,
            LiquidViscosityPas = 1.0e-3,
            LiquidHeatCapacityKJKgK = 4.18,
            TowerHeightM = towerHeightM,
            TowerAreaM2 = 2.2,
            PackingSpecificAreaM2M3 = 102.0,
            PackingNominalSizeM = 0.05,
            PackingCriticalSurfaceTensionNM = 0.061,
            LiquidSurfaceTensionNM = 0.072,
            PressureKPa = 101.3
        };

    private static PackingMassTransferInput Packing()
        => new()
        {
            SpecificAreaM2M3 = 102.0,
            NominalSizeM = 0.05,
            CriticalSurfaceTensionNM = 0.061,
            LiquidSurfaceTensionNM = 0.072,
            TowerAreaM2 = 2.2,
            GasMassFlowKgS = 3.6,
            LiquidMassFlowKgS = 10.8
        };

    private static MassTransferFluidInput Fluid()
        => new()
        {
            GasDensityKgM3 = 1.09,
            GasViscosityPas = 1.95e-5,
            GasDiffusivityM2S = 1.3e-5,
            LiquidDensityKgM3 = 1000.0,
            LiquidViscosityPas = 1.0e-3,
            LiquidDiffusivityM2S = 1.8e-9,
            PressureKPa = 101.3,
            HenrysDimensionless = 0.02
        };

    [Fact]
    public void UnitConversion_1000MgPerNm3So2_Is_About_350Ppm()
    {
        Assert.Equal(349.9, MgPerNm3ToPpm(1000.0, MwSo2), precision: 1);
    }

    [Fact]
    public void Solver_RemovalIncreasesWithPackingHeight_AndStaysBelow100()
    {
        double ppm = MgPerNm3ToPpm(1000.0, MwSo2);

        double previous = 0.0;
        foreach (double height in new[] { 1.0, 2.0, 4.0 })
        {
            var output = MultiPollutantIterativeSolver.SolveIterative(
                BaseInput(height, So2(ppm)));

            double removal = output.OverallRemovalEfficiency["SO2"];

            Assert.True(output.Converged);
            Assert.InRange(removal, 0.0, 100.0);
            Assert.True(removal > previous,
                $"Removal at {height} m ({removal}) must exceed previous ({previous}).");
            previous = removal;
        }
    }

    [Fact]
    public void Solver_LiquidOutletTemperature_IsNotBelowInlet()
    {
        var output = MultiPollutantIterativeSolver.SolveIterative(
            BaseInput(3.0, So2(MgPerNm3ToPpm(1000.0, MwSo2))));

        Assert.True(output.LiquidOutletTemperatureC >= 30.0);
        Assert.True(output.TotalHeatAbsorbedKW >= 0.0);
    }

    [Fact]
    public void Solver_ReturnsEveryPollutantSupplied()
    {
        var hcl = new MultiPollutantIterativeSolver.PollutantInput
        {
            Code = "HCl",
            InletPpm = 200.0,
            MolecularWeight = 36.46,
            HenrysLawConstant = 0.5,
            HeatOfAbsorptionKJKmol = 18000.0
        };

        var output = MultiPollutantIterativeSolver.SolveIterative(
            BaseInput(3.0, So2(350.0), hcl));

        Assert.Contains("SO2", output.OverallRemovalEfficiency.Keys);
        Assert.Contains("HCl", output.OverallRemovalEfficiency.Keys);
        Assert.All(output.Segments, s => Assert.Equal(2, s.Pollutants.Count));
    }

    [Fact]
    public void Solver_Throws_WhenPackingDataIsIncomplete()
    {
        var input = BaseInput(3.0, So2(350.0));
        input.PackingSpecificAreaM2M3 = 0.0;

        Assert.Throws<IncompletePackingInputException>(
            () => MultiPollutantIterativeSolver.SolveIterative(input));
    }

    [Fact]
    public void Solver_Throws_WhenHenryConstantIsMissing()
    {
        var pollutant = So2(350.0);
        pollutant.HenrysLawConstant = 0.0;

        Assert.Throws<PropertyOutOfBoundsException>(
            () => MultiPollutantIterativeSolver.SolveIterative(BaseInput(3.0, pollutant)));
    }

    [Fact]
    public void Solver_Throws_WhenNoPollutants()
    {
        Assert.Throws<ArgumentException>(
            () => MultiPollutantIterativeSolver.SolveIterative(BaseInput(3.0)));
    }

    [Fact]
    public void Solver_Throws_WhenLiquidFlowIsZero()
    {
        var input = BaseInput(3.0, So2(350.0));
        input.LiquidMassFlowKgS = 0.0;

        Assert.ThrowsAny<Exception>(
            () => MultiPollutantIterativeSolver.SolveIterative(input));
    }

    [Fact]
    public void Solver_Throws_WhenInletConcentrationIsNegative()
    {
        Assert.Throws<PropertyOutOfBoundsException>(
            () => MultiPollutantIterativeSolver.SolveIterative(
                BaseInput(3.0, So2(-1.0))));
    }

    [Fact]
    public void Provider_GasFilm_FollowsGasTemperature_NotLiquidTemperature()
    {
        var cold = MassTransferCoefficientProvider.Compute(
            Packing(), Fluid(), 323.15, 293.15, 1.0);
        var hot = MassTransferCoefficientProvider.Compute(
            Packing(), Fluid(), 323.15, 343.15, 1.0);

        Assert.Equal(cold.GasFilmCoeffKmolM2SPa, hot.GasFilmCoeffKmolM2SPa, precision: 12);
        Assert.NotEqual(
            cold.LiquidSideResistanceFraction,
            hot.LiquidSideResistanceFraction);
    }

    [Fact]
    public void Provider_GasFilm_ChangesWithGasTemperature()
    {
        var a = MassTransferCoefficientProvider.Compute(
            Packing(), Fluid(), 303.15, 303.15, 1.0);
        var b = MassTransferCoefficientProvider.Compute(
            Packing(), Fluid(), 353.15, 303.15, 1.0);

        Assert.NotEqual(a.GasFilmCoeffKmolM2SPa, b.GasFilmCoeffKmolM2SPa);
    }

    [Fact]
    public void Provider_EnhancementFactor_RaisesOverallKGa_ButNeverLowersIt()
    {
        var physical = MassTransferCoefficientProvider.Compute(
            Packing(), Fluid(), 323.15, 303.15, 1.0);
        var enhanced = MassTransferCoefficientProvider.Compute(
            Packing(), Fluid(), 323.15, 303.15, 5.0);

        Assert.True(enhanced.OverallKGaKmolM3HrKPa >= physical.OverallKGaKmolM3HrKPa);
        Assert.Equal(5.0, enhanced.EnhancementFactor, precision: 9);
    }

    [Fact]
    public void Provider_Throws_WhenEnhancementFactorBelowOne()
    {
        Assert.Throws<PropertyOutOfBoundsException>(
            () => MassTransferCoefficientProvider.Compute(
                Packing(), Fluid(), 323.15, 303.15, 0.5));
    }

    [Fact]
    public void Provider_Throws_WhenHenryConstantNotPositive()
    {
        var fluid = Fluid();
        fluid.HenrysDimensionless = 0.0;

        Assert.Throws<PropertyOutOfBoundsException>(
            () => MassTransferCoefficientProvider.Compute(
                Packing(), fluid, 323.15, 303.15, 1.0));
    }

    [Fact]
    public void Provider_Throws_WhenTemperatureIsNotPositive()
    {
        Assert.Throws<PropertyOutOfBoundsException>(
            () => MassTransferCoefficientProvider.Compute(
                Packing(), Fluid(), 0.0, 303.15, 1.0));
    }

    [Fact]
    public void Provider_Throws_WhenPackingIsIncomplete()
    {
        var packing = Packing();
        packing.NominalSizeM = 0.0;

        Assert.Throws<IncompletePackingInputException>(
            () => MassTransferCoefficientProvider.Compute(
                packing, Fluid(), 323.15, 303.15, 1.0));
    }

    [Fact]
    public void Onda_Throws_WhenInputIsIncomplete()
    {
        var packing = Packing();
        packing.TowerAreaM2 = 0.0;

        var gas = new GasPhaseProperties(323.15, 1.09, 1.95e-5, 1.3e-5);
        var liquid = new LiquidPhaseProperties(303.15, 1000.0, 1.0e-3, 1.8e-9, 0.072);

        Assert.Throws<IncompletePackingInputException>(
            () => OndaMassTransferCorrelation.Calculate(packing, gas, liquid));
    }

    [Fact]
    public void Onda_ReturnsPositiveCoefficients_ForValidInput()
    {
        var gas = new GasPhaseProperties(323.15, 1.09, 1.95e-5, 1.3e-5);
        var liquid = new LiquidPhaseProperties(303.15, 1000.0, 1.0e-3, 1.8e-9, 0.072);

        var result = OndaMassTransferCorrelation.Calculate(Packing(), gas, liquid);

        Assert.True(result.WettedAreaM2M3 > 0.0);
        Assert.True(result.GasFilmCoeffKmolM2SPa > 0.0);
        Assert.True(result.LiquidFilmCoeffMS > 0.0);
        Assert.True(result.WettedAreaM2M3 <= 102.0 + 1e-9);
    }
}