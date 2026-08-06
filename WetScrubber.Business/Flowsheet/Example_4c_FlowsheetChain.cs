using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Flowsheet
{
    public sealed class Example_4c_FlowsheetChain
    {
        public static void Main()
        {
            // Inlet: SO2 500ppm, H2S 200ppm at 50°C
            var gasFeed = new ProcessStream
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

            // Fresh scrubbing liquid feed: 50 kg/s at 25°C, no pollutant
            // loading yet (this is what used to be fixed parameters on
            // ScrubberUnitOp — now it's a real stream that can recycle).
            var liquidFeed = new LiquidStream
            {
                MassFlowKgS = 50,
                TemperatureC = 25,
                PollutantLoadingKgKg = new Dictionary<string, double>()
            };

            var feed = new FlowsheetPorts { Gas = gasFeed, Liquid = liquidFeed };

            // Unit 1: Pre-cooler (cool from 50→35°C)
            var cooler1 = new CoolerUnitOp
            {
                Name = "PreCooler",
                CoolingDutyKW = 50
            };

            // Unit 2: Scrubber (RK45 ODE, 5m height, 2 m² area). Liquid
            // flow/temp are now sourced from the wired liquid stream when
            // present, falling back to these configured values otherwise.
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
            Console.WriteLine($"Feed: {gasFeed.ActualFlowM3Hr} m³/hr @ {gasFeed.TemperatureC}°C, liquid {liquidFeed.MassFlowKgS} kg/s @ {liquidFeed.TemperatureC}°C");
            Console.WriteLine($"  SO2: {gasFeed.PollutantPpmByCode["SO2"]} ppm");
            Console.WriteLine($"  H2S: {gasFeed.PollutantPpmByCode["H2S"]} ppm\n");

            foreach (var (unitName, outlet) in result.StageOutlets)
            {
                Console.WriteLine($"{unitName}:");
                Console.WriteLine($"  Gas:    T = {outlet.Gas.TemperatureC:F1}°C, Flow = {outlet.Gas.ActualFlowM3Hr:F0} m³/hr");
                Console.WriteLine($"  SO2: {outlet.Gas.PollutantPpmByCode.GetValueOrDefault("SO2", 0):F1} ppm");
                Console.WriteLine($"  H2S: {outlet.Gas.PollutantPpmByCode.GetValueOrDefault("H2S", 0):F1} ppm");
                Console.WriteLine($"  Liquid: T = {outlet.Liquid.TemperatureC:F1}°C, Flow = {outlet.Liquid.MassFlowKgS:F1} kg/s");
                Console.WriteLine($"  SO2 loading: {outlet.Liquid.PollutantLoadingKgKg.GetValueOrDefault("SO2", 0) * 1e6:F1} mg/kg\n");
            }

            Console.WriteLine($"Final outlet: {result.FinalOutlet.Gas.TemperatureC:F1}°C");
            Console.WriteLine($"  SO2 removal: {(1 - result.FinalOutlet.Gas.PollutantPpmByCode.GetValueOrDefault("SO2", 0) / gasFeed.PollutantPpmByCode["SO2"]) * 100:F1}%");

            // With liquid recycle: 20% of scrubbing liquid is recirculated
            // sump water (carrying its picked-up SO2/H2S loading and
            // higher temperature) instead of fresh makeup.
            Console.WriteLine("\n═══ With 20% Liquid Recycle ═══\n");
            var recycleResult = flowsheet.RunWithRecycle(feed, liquidRecycleFraction: 0.20, maxIterations: 10);
            Console.WriteLine($"Converged: {recycleResult.RecycleConverged} (iterations: {recycleResult.RecycleIterations})");
            Console.WriteLine($"Final SO2 (gas): {recycleResult.FinalOutlet.Gas.PollutantPpmByCode.GetValueOrDefault("SO2", 0):F1} ppm");
            Console.WriteLine($"Recirculated liquid SO2 loading: {recycleResult.FinalOutlet.Liquid.PollutantLoadingKgKg.GetValueOrDefault("SO2", 0) * 1e6:F1} mg/kg");
            Console.WriteLine($"Recirculated liquid temperature: {recycleResult.FinalOutlet.Liquid.TemperatureC:F1}°C");
        }
    }
}