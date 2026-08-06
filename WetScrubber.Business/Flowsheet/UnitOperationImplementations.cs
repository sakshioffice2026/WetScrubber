using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.Flowsheet
{
    public sealed class ScrubberUnitOp : IUnitOperation
    {
        public string Name { get; set; }
        public double TowerHeightM { get; set; }
        public double TowerAreaM2 { get; set; }
        public double LiquidFlowKgS { get; set; }
        public double LiquidInletTempC { get; set; }
        public double GasDensityKgM3 { get; set; } = 1.2;
        public double PackingSpecificAreaM2M3 { get; set; } = 250;
        public double PackingNominalSizeM { get; set; } = 0.025;

        public FlowsheetPorts Process(FlowsheetPorts inlet)
        {
            var gasIn = inlet.Gas;
            var liquidIn = inlet.Liquid;

            // Fall back to this unit's configured liquid parameters when
            // no liquid stream has actually been wired in (first pass of
            // a flowsheet with no liquid feed connected, or a caller that
            // hasn't adopted liquid wiring yet) — same contract as before.
            bool liquidWired = liquidIn != null && liquidIn.MassFlowKgS > 0;
            double liquidFlowKgS = liquidWired ? liquidIn.MassFlowKgS : LiquidFlowKgS;
            double liquidInTempC = liquidWired ? liquidIn.TemperatureC : LiquidInletTempC;
            var liquidLoading = liquidWired ? liquidIn.PollutantLoadingKgKg : new Dictionary<string, double>();

            var pollutants = gasIn.PollutantPpmByCode
                .Select(kv => new MultiPollutantIterativeSolver.PollutantInput
                {
                    Code = kv.Key,
                    InletPpm = kv.Value,
                    MolecularWeight = MolWeight(kv.Key),
                    HenrysLawConstant = Henry(kv.Key),
                    HeatOfAbsorptionKJKmol = HeatAbs(kv.Key),
                    HenrysLawTemperatureCorrectionFn = T => 1.0 + 0.01 * (T - 25)
                })
                .ToList();

            var odeInput = new MultiPollutantOdeSolver.SolverInput
            {
                Pollutants = pollutants,
                GasTemperatureC = gasIn.TemperatureC,
                GasMassFlowKgS = gasIn.ActualFlowM3Hr / 3600.0 * GasDensityKgM3,
                LiquidInletTempC = liquidInTempC,
                LiquidMassFlowKgS = liquidFlowKgS,
                LiquidDensityKgM3 = 1000,
                GasDensityKgM3 = GasDensityKgM3,
                TowerHeightM = TowerHeightM,
                TowerAreaM2 = TowerAreaM2,
                PackingSpecificAreaM2M3 = PackingSpecificAreaM2M3,
                PackingNominalSizeM = PackingNominalSizeM,
                InletLiquidLoadingKgKg = liquidLoading
            };

            var result = MultiPollutantOdeSolver.SolveOde(odeInput);

            var gasOut = new ProcessStream
            {
                ActualFlowM3Hr = gasIn.ActualFlowM3Hr,
                TemperatureC = result.OutletGasTemperatureK - 273.15,
                PressurePa = gasIn.PressurePa,
                PollutantPpmByCode = result.OutletConcKgM3
            };

            var liquidOut = new LiquidStream
            {
                MassFlowKgS = liquidFlowKgS,
                TemperatureC = result.LiquidOutletTemperatureC,
                PollutantLoadingKgKg = result.OutletLiquidLoadingKgKg
            };

            return new FlowsheetPorts { Gas = gasOut, Liquid = liquidOut };
        }

        private double MolWeight(string code) => code switch { "SO2" => 64, "H2S" => 34, "NH3" => 17, _ => 50 };
        private double Henry(string code) => code switch { "SO2" => 1.5e5, "H2S" => 9.7e4, "NH3" => 58, _ => 1e5 };
        private double HeatAbs(string code) => code switch { "SO2" => -40000, "H2S" => -45000, "NH3" => -38000, _ => -40000 };
    }

    public sealed class CoolerUnitOp : IUnitOperation
    {
        public string Name { get; set; }
        public double CoolingDutyKW { get; set; }

        public FlowsheetPorts Process(FlowsheetPorts inlet)
        {
            var gasIn = inlet.Gas;
            double m_gas = gasIn.ActualFlowM3Hr / 3600.0 * 1.2;
            double dT = CoolingDutyKW * 3600.0 / (m_gas * 1.05);

            var gasOut = new ProcessStream
            {
                ActualFlowM3Hr = gasIn.ActualFlowM3Hr,
                TemperatureC = Math.Max(gasIn.TemperatureC - dT, 15),
                PressurePa = gasIn.PressurePa,
                PollutantPpmByCode = gasIn.PollutantPpmByCode
            };

            // Indirect heat exchanger — no gas/liquid contact, so the
            // liquid stream (if any is being wired through the chain
            // for downstream recycle) passes through untouched.
            return new FlowsheetPorts { Gas = gasOut, Liquid = inlet.Liquid };
        }
    }

    public sealed class SeparatorUnitOp : IUnitOperation
    {
        public string Name { get; set; }
        public double SeparationEfficiency { get; set; } = 0.98;

        public FlowsheetPorts Process(FlowsheetPorts inlet)
        {
            var gasIn = inlet.Gas;
            var cleaned = gasIn.PollutantPpmByCode
                .ToDictionary(kv => kv.Key, kv => kv.Value * (1.0 - SeparationEfficiency));

            var gasOut = new ProcessStream
            {
                ActualFlowM3Hr = gasIn.ActualFlowM3Hr * SeparationEfficiency,
                TemperatureC = gasIn.TemperatureC,
                PressurePa = gasIn.PressurePa,
                PollutantPpmByCode = cleaned
            };

            // Droplet carryover to the liquid stream isn't modeled yet
            // (same unsourced-data stance as before) — liquid passes
            // through unchanged.
            return new FlowsheetPorts { Gas = gasOut, Liquid = inlet.Liquid };
        }
    }
}