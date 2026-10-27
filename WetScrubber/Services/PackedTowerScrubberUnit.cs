using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Flowsheet;
using WetScrubber.Models;

namespace WetScrubber.Services
{
    /// <summary>
    /// Adapts ScrubberCalculationEngine into a Flowsheet IUnitOperation.
    /// P4 revision:
    ///  - gas outlet temperature from a sensible-heat balance (not the liquid temperature)
    ///  - outlet liquid pollutant loading from the gas-side absorbed mass
    ///  - upstream liquid loading (x2) consumed and carried into the outlet liquid
    ///  - outlet gas volumetric flow corrected for pressure drop and temperature
    /// </summary>
    public sealed class PackedTowerScrubberUnit : IUnitOperation
    {
        private const double R_J_MOL_K = 8.314462;
        private const double CarrierMwKgKmol = 28.97;
        private const double CarrierCpJKgK = 1005.0;
        private const double LiquidCpJKgK = 4186.0;
        private const double MinAbsPressurePa = 1.0;

        public string Name { get; }

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
            var liquidIn = inlet.Liquid ?? new LiquidStream();

            var vm = CloneTemplate(_template);
            vm.ActualFlowRate = gasIn.ActualFlowM3Hr;
            vm.InletTemperature = gasIn.TemperatureC;
            vm.InletPressure = gasIn.PressurePa;

            bool liquidWired = liquidIn.MassFlowKgS > 0 && gasIn.ActualFlowM3Hr > 0;
            if (liquidWired)
            {
                double liquidFlowM3Hr = liquidIn.MassFlowKgS / Math.Max(vm.LiquidDensity, 1.0) * 3600.0;
                vm.LiquidToGasRatio = liquidFlowM3Hr * 1000.0 / gasIn.ActualFlowM3Hr;
                vm.LiquidTemperature = liquidIn.TemperatureC;
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

            if (!result.IsFeasible)
            {
                string detail = result.InfeasibilityReason ?? "Design is infeasible.";

                if (result.MaxAchievableRemovalPercent > 0.0)
                    detail += $" Max achievable removal: {result.MaxAchievableRemovalPercent:F2} %.";

                if (result.MinLGRatio > 0.0)
                    detail += $" Minimum L/G: {result.MinLGRatio:F3}.";

                throw new InvalidOperationException($"Unit '{Name}': {detail}");
            }

            // ── Outlet gas pollutant ppm ────────────────────────────────────
            var outletPpm = new Dictionary<string, double>(gasIn.PollutantPpmByCode);
            var absorbedPpmByCode = new Dictionary<string, double>();
            var mwByCode = new Dictionary<string, double>();

            foreach (var pr in result.PollutantResults)
            {
                if (!_pollutantTypeToCode.TryGetValue(pr.PollutantType, out var code)) continue;

                double outPpm = pr.PhysicallyDerivedRating
                    ? pr.RatedOutletConcentrationPpm
                    : Math.Max(pr.InletConcentrationPpm * (1.0 - pr.RemovalEfficiency / 100.0), 0.0);

                outPpm = Math.Min(Math.Max(outPpm, 0.0), pr.InletConcentrationPpm);
                outletPpm[code] = outPpm;
                absorbedPpmByCode[code] = pr.InletConcentrationPpm - outPpm;

                var input = vm.Pollutants.FirstOrDefault(p => p.PollutantType == pr.PollutantType);
                if (input != null && input.MolecularWeight > 0)
                    mwByCode[code] = input.MolecularWeight;
            }

            // ── Liquid outlet temperature and flow ──────────────────────────
            double liquidOutTempC = result.LiquidOutletTemperatureK > 0
                ? result.LiquidOutletTemperatureK - 273.15
                : result.LiquidOutletTemperature;

            double liquidOutMassFlowKgS = liquidWired
                ? liquidIn.MassFlowKgS
                : result.LiquidFlowRateM3Hr * vm.LiquidDensity / 3600.0;

            // ── Gas outlet temperature from sensible-heat balance ───────────
            double gasInVolM3S = gasIn.ActualFlowM3Hr / 3600.0;
            double gasInTempK = gasIn.TemperatureC + 273.15;
            double gasMolarFlowMolS = gasIn.PressurePa * gasInVolM3S / (R_J_MOL_K * gasInTempK);
            double gasMassFlowKgS = gasMolarFlowMolS * CarrierMwKgKmol / 1000.0;

            double gasOutTempC = gasIn.TemperatureC;
            if (gasMassFlowKgS > 1e-12 && liquidOutMassFlowKgS > 0)
            {
                double liquidInTempC = liquidWired ? liquidIn.TemperatureC : vm.LiquidTemperature;
                double heatToLiquidW = liquidOutMassFlowKgS * LiquidCpJKgK * (liquidOutTempC - liquidInTempC);
                gasOutTempC = gasIn.TemperatureC - heatToLiquidW / (gasMassFlowKgS * CarrierCpJKgK);

                double lo = Math.Min(gasIn.TemperatureC, liquidInTempC);
                double hi = Math.Max(gasIn.TemperatureC, liquidInTempC);
                gasOutTempC = Math.Min(Math.Max(gasOutTempC, lo), hi);
            }

            // ── Outlet pressure and volumetric flow ─────────────────────────
            double gasOutPressurePa = Math.Max(gasIn.PressurePa - result.PressureDrop, MinAbsPressurePa);
            double gasOutTempK = gasOutTempC + 273.15;
            double gasOutFlowM3Hr = gasIn.ActualFlowM3Hr
                * (gasOutTempK / gasInTempK)
                * (gasIn.PressurePa / gasOutPressurePa);

            var gasOut = new ProcessStream
            {
                ActualFlowM3Hr = gasOutFlowM3Hr,
                TemperatureC = gasOutTempC,
                PressurePa = gasOutPressurePa,
                PollutantPpmByCode = outletPpm
            };

            // ── Liquid pollutant loading (x2 in, absorbed added, x1 out) ────
            var loadingOut = new Dictionary<string, double>();
            foreach (var kv in liquidIn.PollutantLoadingKgKg)
                loadingOut[kv.Key] = kv.Value;

            if (liquidOutMassFlowKgS > 1e-12)
            {
                foreach (var kv in absorbedPpmByCode)
                {
                    if (!mwByCode.TryGetValue(kv.Key, out double mw)) continue;

                    double absorbedKgS = kv.Value * 1e-6 * gasMolarFlowMolS * mw / 1000.0;
                    double upstreamKgKg = liquidIn.PollutantLoadingKgKg.TryGetValue(kv.Key, out var x2) ? x2 : 0.0;
                    double inMassKgS = upstreamKgKg * liquidOutMassFlowKgS;

                    loadingOut[kv.Key] = (inMassKgS + absorbedKgS) / liquidOutMassFlowKgS;
                }
            }

            var liquidOut = new LiquidStream
            {
                MassFlowKgS = liquidOutMassFlowKgS,
                TemperatureC = liquidOutTempC,
                PollutantLoadingKgKg = loadingOut
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