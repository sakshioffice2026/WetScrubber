using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Flowsheet;
using WetScrubber.Models;

namespace WetScrubber.Services
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
        /// fits in FlowsheetPorts.</summary>
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

        public FlowsheetPorts Process(FlowsheetPorts inlet)
        {
            var gasIn = inlet.Gas;

            // Clone per call — recycle iterations invoke this repeatedly
            // and must never mutate shared template state between passes.
            var vm = CloneTemplate(_template);
            vm.ActualFlowRate = gasIn.ActualFlowM3Hr;
            vm.InletTemperature = gasIn.TemperatureC;
            vm.InletPressure = gasIn.PressurePa;

            // Wire the real liquid stream in when one has been connected.
            // The engine has no absolute-flow input — everything derives
            // from LiquidToGasRatio (L per m3 gas, see
            // ScrubberCalculationEngine's `ActualFlowRate * LiquidToGasRatio
            // / 1000`) — so convert the wired mass flow into the
            // equivalent ratio instead. Falls back to the design
            // template's own ratio when no liquid stream is wired.
            bool liquidWired = inlet.Liquid != null && inlet.Liquid.MassFlowKgS > 0 && gasIn.ActualFlowM3Hr > 0;
            if (liquidWired)
            {
                double liquidFlowM3Hr = inlet.Liquid.MassFlowKgS / Math.Max(vm.LiquidDensity, 1.0) * 3600.0;
                vm.LiquidToGasRatio = liquidFlowM3Hr * 1000.0 / gasIn.ActualFlowM3Hr;
                vm.LiquidTemperature = inlet.Liquid.TemperatureC;
            }

            foreach (var p in vm.Pollutants)
            {
                if (_pollutantTypeToCode.TryGetValue(p.PollutantType, out var code) &&
                    gasIn.PollutantPpmByCode.TryGetValue(code, out var ppm))
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
            var outletPpm = new Dictionary<string, double>(gasIn.PollutantPpmByCode);
            foreach (var pr in result.PollutantResults)
            {
                if (!_pollutantTypeToCode.TryGetValue(pr.PollutantType, out var code)) continue;

                outletPpm[code] = pr.PhysicallyDerivedRating
                    ? pr.RatedOutletConcentrationPpm
                    : Math.Max(pr.InletConcentrationPpm * (1.0 - pr.RemovalEfficiency / 100.0), 0.0);
            }

            var gasOut = new ProcessStream
            {
                // Dilute-system assumption (gas volumetric flow essentially
                // unchanged by absorbing a trace pollutant) — same basis
                // ScrubberCalculationEngine already uses throughout.
                ActualFlowM3Hr = gasIn.ActualFlowM3Hr,
                TemperatureC = result.LiquidOutletTemperatureK > 0
                    ? result.LiquidOutletTemperatureK - 273.15
                    : gasIn.TemperatureC,
                PressurePa = Math.Max(gasIn.PressurePa - result.PressureDrop, 0.0),
                PollutantPpmByCode = outletPpm
            };

            // NOTE: ScrubberCalculationEngine doesn't yet compute a
            // per-pollutant liquid-phase mass balance (CalculationResult
            // has no field for it), so outlet liquid loading can't be
            // populated honestly here yet — only mass flow and
            // temperature, which the engine does compute, carry through.
            // A liquid recycle wired through this adapter will correctly
            // pick up temperature buildup but not pollutant loading until
            // that engine gains one (natural next step alongside a full
            // energy balance).
            var liquidOut = new LiquidStream
            {
                MassFlowKgS = liquidWired
                    ? inlet.Liquid.MassFlowKgS
                    : result.LiquidFlowRateM3Hr * vm.LiquidDensity / 3600.0,
                TemperatureC = result.LiquidOutletTemperatureK > 0
                    ? result.LiquidOutletTemperatureK - 273.15
                    : result.LiquidOutletTemperature,
                PollutantLoadingKgKg = new Dictionary<string, double>()
            };

            return new FlowsheetPorts { Gas = gasOut, Liquid = liquidOut };
        }

        private static CreateDesignViewModel CloneTemplate(CreateDesignViewModel src)
        {
            var clone = src.Clone();
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