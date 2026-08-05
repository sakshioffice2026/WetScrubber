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

        public ProcessStream Process(ProcessStream inlet)
        {
            var pollutants = inlet.PollutantPpmByCode
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
                GasTemperatureC = inlet.TemperatureC,
                GasMassFlowKgS = inlet.ActualFlowM3Hr / 3600.0 * GasDensityKgM3,
                LiquidInletTempC = LiquidInletTempC,
                LiquidMassFlowKgS = LiquidFlowKgS,
                LiquidDensityKgM3 = 1000,
                GasDensityKgM3 = GasDensityKgM3,
                TowerHeightM = TowerHeightM,
                TowerAreaM2 = TowerAreaM2,
                PackingSpecificAreaM2M3 = PackingSpecificAreaM2M3,
                PackingNominalSizeM = PackingNominalSizeM
            };

            var result = MultiPollutantOdeSolver.SolveOde(odeInput);

            return new ProcessStream
            {
                ActualFlowM3Hr = inlet.ActualFlowM3Hr,
                TemperatureC = result.OutletGasTemperatureK - 273.15,
                PressurePa = inlet.PressurePa,
                PollutantPpmByCode = result.OutletConcKgM3
            };
        }

        private double MolWeight(string code) => code switch { "SO2" => 64, "H2S" => 34, "NH3" => 17, _ => 50 };
        private double Henry(string code) => code switch { "SO2" => 1.5e5, "H2S" => 9.7e4, "NH3" => 58, _ => 1e5 };
        private double HeatAbs(string code) => code switch { "SO2" => -40000, "H2S" => -45000, "NH3" => -38000, _ => -40000 };
    }

    public sealed class CoolerUnitOp : IUnitOperation
    {
        public string Name { get; set; }
        public double CoolingDutyKW { get; set; }

        public ProcessStream Process(ProcessStream inlet)
        {
            double m_gas = inlet.ActualFlowM3Hr / 3600.0 * 1.2;
            double dT = CoolingDutyKW * 3600.0 / (m_gas * 1.05);

            return new ProcessStream
            {
                ActualFlowM3Hr = inlet.ActualFlowM3Hr,
                TemperatureC = Math.Max(inlet.TemperatureC - dT, 15),
                PressurePa = inlet.PressurePa,
                PollutantPpmByCode = inlet.PollutantPpmByCode
            };
        }
    }

    public sealed class SeparatorUnitOp : IUnitOperation
    {
        public string Name { get; set; }
        public double SeparationEfficiency { get; set; } = 0.98;

        public ProcessStream Process(ProcessStream inlet)
        {
            var cleaned = inlet.PollutantPpmByCode
                .ToDictionary(kv => kv.Key, kv => kv.Value * (1.0 - SeparationEfficiency));

            return new ProcessStream
            {
                ActualFlowM3Hr = inlet.ActualFlowM3Hr * SeparationEfficiency,
                TemperatureC = inlet.TemperatureC,
                PressurePa = inlet.PressurePa,
                PollutantPpmByCode = cleaned
            };
        }
    }
}