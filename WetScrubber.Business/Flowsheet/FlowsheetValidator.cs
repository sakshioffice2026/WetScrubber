using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Flowsheet
{
    public sealed record BenchmarkCase(
        string Name,
        string PollutantCode,
        double InletConcentrationPpm,
        double TargetRemovalFraction,
        double LiquidToGasRatioMolar,
        double InletTemperatureC,
        double ExpectedTowerDiameterM,
        double ExpectedPackingHeightM,
        double ExpectedRemovalEfficiency,
        double DiameterToleranceFraction = 0.05,
        double HeightToleranceFraction = 0.10);

    public sealed record ValidationResult(
        string CaseName,
        double WetScrubberDiameter,
        double ReferenceDiameter,
        double DiameterErrorPercent,
        bool DiameterWithinTolerance,
        double WetScrubberHeight,
        double ReferenceHeight,
        double HeightErrorPercent,
        bool HeightWithinTolerance,
        bool CasePassed);

    public static class BenchmarkCaseLibrary
    {
        public static List<BenchmarkCase> LoadAll() => new()
        {
            new BenchmarkCase(
                Name: "SO2-in-NaOH-500ppm",
                PollutantCode: "SO2",
                InletConcentrationPpm: 500,
                TargetRemovalFraction: 0.95,
                LiquidToGasRatioMolar: 2.0,
                InletTemperatureC: 25,
                ExpectedTowerDiameterM: 1.20,
                ExpectedPackingHeightM: 4.50,
                ExpectedRemovalEfficiency: 0.95),
            new BenchmarkCase(
                Name: "HCl-in-Caustic-300ppm",
                PollutantCode: "HCl",
                InletConcentrationPpm: 300,
                TargetRemovalFraction: 0.90,
                LiquidToGasRatioMolar: 1.8,
                InletTemperatureC: 30,
                ExpectedTowerDiameterM: 0.95,
                ExpectedPackingHeightM: 3.80,
                ExpectedRemovalEfficiency: 0.90),
            new BenchmarkCase(
                Name: "NH3-in-Water-400ppm",
                PollutantCode: "NH3",
                InletConcentrationPpm: 400,
                TargetRemovalFraction: 0.85,
                LiquidToGasRatioMolar: 2.2,
                InletTemperatureC: 20,
                ExpectedTowerDiameterM: 1.10,
                ExpectedPackingHeightM: 5.00,
                ExpectedRemovalEfficiency: 0.85)
        };
    }

    public sealed class ValidationRunner
    {
        public static List<ValidationResult> RunAllCases(Func<BenchmarkCase, (double diameter, double height, double efficiency)> designCalculator)
        {
            var results = new List<ValidationResult>();
            var cases = BenchmarkCaseLibrary.LoadAll();

            foreach (var benchCase in cases)
            {
                var engineResult = designCalculator(benchCase);

                double diamError = Math.Abs(engineResult.diameter - benchCase.ExpectedTowerDiameterM) / Math.Max(benchCase.ExpectedTowerDiameterM, 0.01) * 100;
                double heightError = Math.Abs(engineResult.height - benchCase.ExpectedPackingHeightM) / Math.Max(benchCase.ExpectedPackingHeightM, 0.01) * 100;

                bool diamOk = diamError <= benchCase.DiameterToleranceFraction * 100;
                bool heightOk = heightError <= benchCase.HeightToleranceFraction * 100;

                results.Add(new ValidationResult(
                    benchCase.Name,
                    engineResult.diameter,
                    benchCase.ExpectedTowerDiameterM,
                    diamError,
                    diamOk,
                    engineResult.height,
                    benchCase.ExpectedPackingHeightM,
                    heightError,
                    heightOk,
                    diamOk && heightOk));
            }

            return results;
        }

        public static void PrintReport(List<ValidationResult> results)
        {
            int passCount = results.Count(r => r.CasePassed);
            Console.WriteLine($"\n╔═══════════════════════════════════════════════╗");
            Console.WriteLine($"║  WetScrubber Validation Report                 ║");
            Console.WriteLine($"║  {DateTime.Now:yyyy-MM-dd HH:mm:ss}                             ║");
            Console.WriteLine($"╚═══════════════════════════════════════════════╝\n");
            Console.WriteLine($"Results: {passCount}/{results.Count} cases passed\n");

            foreach (var r in results)
            {
                string status = r.CasePassed ? "✓ PASS" : "✗ FAIL";
                Console.WriteLine($"{status} | {r.CaseName}");
                Console.WriteLine($"     Diameter: {r.WetScrubberDiameter:F2}m (ref {r.ReferenceDiameter:F2}m, {r.DiameterErrorPercent:F1}%)");
                Console.WriteLine($"     Height:   {r.WetScrubberHeight:F2}m (ref {r.ReferenceHeight:F2}m, {r.HeightErrorPercent:F1}%)\n");
            }

            if (passCount == results.Count)
                Console.WriteLine("✓ ALL TESTS PASSED\n");
            else
                Console.WriteLine($"✗ {results.Count - passCount} TEST(S) FAILED\n");
        }
    }
}