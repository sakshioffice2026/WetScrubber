using WetScrubber.Business.Flowsheet;
using WetScrubber.Models;

namespace WetScrubber.Services.Flowsheet
{
    /// <summary>
    /// Adapts ScrubberCalculationEngine (a single-tower calculator) into
    /// a Flowsheet IUnitOperation. Lives in the web project rather than
    /// WetScrubber.Business because it needs CreateDesignViewModel /
    /// ScrubberCalculationEngine, and WetScrubber.Business deliberately
    /// has no reference back to either (see the .csproj files) — Business
    /// stays pure math, this is the wiring layer.
    /// </summary>
    public sealed class PackedTowerScrubberUnit : IUnitOperation
    {
        public string Name { get; }

        /// <summary>Full result of the most recent Process() call — the
        /// geometry/diagnostics data a flowsheet report needs beyond what
        /// fits in a ProcessStream.</summary>
        public CalculationResult? LastResult { get; private set; }

        private readonly ScrubberCalculationEngine _engine;
        private readonly CreateDesignViewModel _template;
        private readonly IReadOnlyDictionary<int, string> _pollutantTypeToCode;

        public PackedTowerScrubberUnit(
            string name,
            ScrubberCalculationEngine engine,
            CreateDesignViewModel template,
            IReadOnlyDictionary<int, string> pollutantTypeToCode)
        {
            Name = name;
            _engine = engine;
            _template = template;
            _pollutantTypeToCode = pollutantTypeToCode;
        }

        public ProcessStream Process(ProcessStream inlet)
        {
            // Clone per call — recycle iterations invoke this repeatedly
            // and must never mutate shared template state between passes.
            var vm = CloneTemplate(_template);
            vm.ActualFlowRate = inlet.ActualFlowM3Hr;
            vm.InletTemperature = inlet.TemperatureC;
            vm.InletPressure = inlet.PressurePa;

            foreach (var p in vm.Pollutants)
            {
                if (_pollutantTypeToCode.TryGetValue(p.PollutantType, out var code) &&
                    inlet.PollutantPpmByCode.TryGetValue(code, out var ppm))
                {
                    p.InletConcentration = ppm;
                }
            }

            var result = _engine.RunCalculation(vm);
            LastResult = result;

            // Phase 3's per-pollutant rating feeds the outlet loading when
            // it ran; falls back to the NTU/HTU removal-efficiency number
            // otherwise — same hard-fallback contract as the rest of the
            // engine.
            var outletPpm = new Dictionary<string, double>(inlet.PollutantPpmByCode);
            foreach (var pr in result.PollutantResults)
            {
                if (!_pollutantTypeToCode.TryGetValue(pr.PollutantType, out var code)) continue;

                outletPpm[code] = pr.PhysicallyDerivedRating
                    ? pr.RatedOutletConcentrationPpm
                    : Math.Max(pr.InletConcentrationPpm * (1.0 - pr.RemovalEfficiency / 100.0), 0.0);
            }

            return new ProcessStream
            {
                // Dilute-system assumption (gas volumetric flow essentially
                // unchanged by absorbing a trace pollutant) — same basis
                // ScrubberCalculationEngine already uses throughout.
                ActualFlowM3Hr = inlet.ActualFlowM3Hr,
                TemperatureC = result.LiquidOutletTemperatureK > 0
                    ? result.LiquidOutletTemperatureK - 273.15
                    : inlet.TemperatureC,
                PressurePa = Math.Max(inlet.PressurePa - result.PressureDrop, 0.0),
                PollutantPpmByCode = outletPpm
            };
        }

        private static CreateDesignViewModel CloneTemplate(CreateDesignViewModel src)
        {
            var clone = (CreateDesignViewModel)src.MemberwiseClone();
            clone.Pollutants = src.Pollutants.Select(p => new PollutantInputViewModel
            {
                PollutantType = p.PollutantType,
                InletConcentration = p.InletConcentration,
                TargetOutletConcentration = p.TargetOutletConcentration,
                TargetRemovalEfficiency = p.TargetRemovalEfficiency,
                MolecularWeight = p.MolecularWeight,
                HenrysLawConstant = p.HenrysLawConstant
            }).ToList();
            return clone;
        }
    }
}