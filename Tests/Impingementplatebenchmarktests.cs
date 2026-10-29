using WetScrubber.Business.Plate;

namespace WetScrubber.Business.Tests;

public sealed class ImpingementPlateBenchmarkTests
{
    // Reference case (Treybal sieve-tray head model, hand calculation)
    // Qg = 1.0 m3/s, rho_g = 1.2, rho_L = 1000, QL = 0.003 m3/s, sigma = 0.072 N/m
    // D = 1.0 m, Lw/D = 0.7, open area = 10 % of active, d_h = 5 mm, hw = 50 mm, spacing = 0.45 m
    // C0 = 0.70, phi = 0.60, C_SB = 0.08 m/s
    private static PlateHydraulicsResult Run() => ImpingementPlateHydraulics.Evaluate(new PlateHydraulicsInput
    {
        GasFlowM3s = 1.0,
        GasDensityKgM3 = 1.2,
        LiquidDensityKgM3 = 1000.0,
        LiquidFlowM3s = 0.003,
        SurfaceTensionNm = 0.072,
        ColumnDiameterM = 1.0,
        WeirLengthFraction = 0.7,
        OpenAreaFraction = 0.10,
        HoleDiameterM = 0.005,
        WeirHeightM = 0.05,
        TraySpacingM = 0.45,
        OrificeCoefficient = 0.70,
        AerationFactor = 0.60,
        FloodCapacityFactorMs = 0.08
    });

    private static void Within(double expected, double actual, double relTol = 0.01) =>
        Assert.InRange(actual, expected * (1.0 - relTol), expected * (1.0 + relTol));

    [Fact]
    public void DowncomerAreaFraction_ForChordRatio0p7_Is0p0877()
    {
        Within(0.0877, ImpingementPlateHydraulics.DowncomerAreaFraction(0.7), 0.005);
    }

    [Fact]
    public void Areas_MatchHandCalc()
    {
        var r = Run();
        Within(0.7165, r.NetAreaM2);
        Within(0.6477, r.ActiveAreaM2);
        Within(0.06477, r.HoleAreaM2);
    }

    [Fact]
    public void HoleVelocityAndFFactor_MatchHandCalc()
    {
        var r = Run();
        Within(15.44, r.HoleVelocityMs);
        Within(16.91, r.HoleFFactor);
    }

    [Fact]
    public void Heads_MatchHandCalc()
    {
        var r = Run();
        Within(0.02977, r.DryHeadM);
        Within(0.01755, r.WeirCrestM);
        Within(0.04053, r.HydraulicHeadM);
        Within(0.008811, r.SurfaceTensionHeadM);
        Within(0.07911, r.TotalHeadM);
    }

    [Fact]
    public void PressureDropPerPlate_MatchesHandCalc()
    {
        Within(775.8, Run().PressureDropPerPlatePa);
    }

    [Fact]
    public void Flooding_MatchesHandCalc()
    {
        var r = Run();
        Within(2.308, r.FloodingVelocityMs);
        Within(60.5, r.PercentFlood);
        Assert.False(r.IsFlooding);
        Assert.False(r.IsWeeping);
    }

    [Fact]
    public void DryHead_ScalesWithVelocitySquared()
    {
        double h1 = ImpingementPlateHydraulics.DryHeadM(10.0, 1.2, 1000.0, 0.7);
        double h2 = ImpingementPlateHydraulics.DryHeadM(20.0, 1.2, 1000.0, 0.7);
        Within(4.0, h2 / h1, 1e-9);
    }
}