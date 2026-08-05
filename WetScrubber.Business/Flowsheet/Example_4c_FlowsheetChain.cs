using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Flowsheet
{
    public sealed class Example_4c_FlowsheetChain
    {
        public static void Main()
        {
            // Inlet: SO2 500ppm, H2S 200ppm at 50°C
            var feed = new ProcessStream
            {
                ActualFlowM3Hr = 10000,
                TemperatureC = 50,
                PressurePa = 101325,
                PollutantPpmByCode = new Dictionary<string, double>
                {
                    { "SO2", 500 },
                    { "H2S", 200 }
                }
            };

            // Unit 1: Pre-cooler (cool from 50→35°C)
            var cooler1 = new CoolerUnitOp
            {
                Name = "PreCooler",
                CoolingDutyKW = 50
            };

            // Unit 2: Scrubber (RK45 ODE, 5m height, 2 m² area)
            var scrubber = new ScrubberUnitOp
            {
                Name = "MainScrubber",
                TowerHeightM = 5.0,
                TowerAreaM2 = 2.0,
                LiquidFlowKgS = 50,
                LiquidInletTempC = 25,
                PackingSpecificAreaM2M3 = 250
            };

            // Unit 3: Mist eliminator (remove 95% carryover)
            var separator = new SeparatorUnitOp
            {
                Name = "MistEliminator",
                SeparationEfficiency = 0.95
            };

            // Chain: cooler -> scrubber -> separator
            var flowsheet = new Flowsheet(new IUnitOperation[] { cooler1, scrubber, separator });

            // Solve (no recycle)
            var result = flowsheet.Run(feed);

            Console.WriteLine("═══ PHASE 4c: Flowsheet Chain ═══\n");
            Console.WriteLine($"Feed: {feed.ActualFlowM3Hr} m³/hr @ {feed.TemperatureC}°C");
            Console.WriteLine($"  SO2: {feed.PollutantPpmByCode["SO2"]} ppm");
            Console.WriteLine($"  H2S: {feed.PollutantPpmByCode["H2S"]} ppm\n");

            foreach (var (unitName, outlet) in result.StageOutlets)
            {
                Console.WriteLine($"{unitName}:");
                Console.WriteLine($"  T = {outlet.TemperatureC:F1}°C, Flow = {outlet.ActualFlowM3Hr:F0} m³/hr");
                Console.WriteLine($"  SO2: {outlet.PollutantPpmByCode.GetValueOrDefault("SO2", 0):F1} ppm");
                Console.WriteLine($"  H2S: {outlet.PollutantPpmByCode.GetValueOrDefault("H2S", 0):F1} ppm\n");
            }

            Console.WriteLine($"Final outlet: {result.FinalOutlet.TemperatureC:F1}°C");
            Console.WriteLine($"  SO2 removal: {(1 - result.FinalOutlet.PollutantPpmByCode.GetValueOrDefault("SO2", 0) / feed.PollutantPpmByCode["SO2"]) * 100:F1}%");

            // With recycle: 20% of outlet recycled back
            Console.WriteLine("\n═══ With 20% Recycle ═══\n");
            var recycleResult = flowsheet.RunWithRecycle(feed, recycleFraction: 0.20, maxIterations: 10);
            Console.WriteLine($"Converged: {recycleResult.RecycleConverged} (iterations: {recycleResult.RecycleIterations})");
            Console.WriteLine($"Final SO2: {recycleResult.FinalOutlet.PollutantPpmByCode.GetValueOrDefault("SO2", 0):F1} ppm");
        }
    }
}