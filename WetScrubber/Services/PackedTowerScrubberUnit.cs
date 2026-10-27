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
    ///
    /// Stream handling:
    ///  - Gas outlet temperature is computed from a countercurrent sensible-heat
    ///    balance (it is NOT the liquid temperature).
    ///  - Upstream liquid pollutant loading (kg pollutant / kg liquid) is consumed
    ///    as the inlet liquid mole fraction x2 (single-pollutant towers).
    ///  - Outlet liquid pollutant loading and mass flow include the absorbed mass.
    ///  - Outlet gas volumetric flow is corrected for pressure drop and
    ///    temperature change (ideal gas, molar flow constant).
    /// </summary>
    public sealed class PackedTowerScrubberUnit : IUnitOperation
    {
        private const double GasConstantJMolK = 8.314462;

        public string Name { get; }

        /// <summary>Full result of the most recent Process() call — the
        /// geometry/diagnostics data a flowsheet report needs beyond what
        /// fits in FlowsheetPorts.</summary>
        public CalculationResult? LastResult { get; private set; }

        private readonly ScrubberCalculationEngine _engine;
        private readonly CreateDesignViewModel _template;
        private readonly IReadOnlyDictionary<int, string> _pollutantTypeToCode;
        private readonly double _gasSpecificHeatKJKgK;
        private readonly double _liquidSpecificHeatKJKgK;
        private readonly double _thermalEffectiveness;

        /// <param name="gasSpecificHeatKJKgK">Carrier-gas cp, kJ/(kg·K).</param>
        /// <param name="liquidSpecificHeatKJKgK">Scrubbing-liquid cp, kJ/(kg·K).</param>
        /// <param name="thermalEffectiveness">
        /// Sensible-heat effectiveness of the countercurrent contactor, 0..1.
        /// 1.0 is the infinite-area limit (gas leaves at the liquid inlet
        /// temperature when the liquid has the larger heat-capacity rate).
        /// Evaporation is not modelled.
        /// </param>
        public PackedTowerScrubberUnit(
            string name,
            ScrubberCalculationEngine engine,
            CreateDesignViewModel template,
            IReadOnlyDictionary<int, string> pollutantTypeToCode,
            double gasSpecificHeatKJKgK = 1.005,
            double liquidSpecificHeatKJKgK = 4.18,
            double thermalEffectiveness = 1.0)
        {
            if (!(gasSpecificHeatKJKgK > 0)) throw new ArgumentOutOfRangeException(nameof(gasSpecificHeatKJKgK));
            if (!(liquidSpecificHeatKJKgK > 0)) throw new ArgumentOutOfRangeException(nameof(liquidSpecificHeatKJKgK));
            if (!(thermalEffectiveness >= 0.0 && thermalEffectiveness <= 1.0))
                throw new ArgumentOutOfRangeException(nameof(thermalEffectiveness));

            Name = name;
            _engine = engine;
            _template = template;
            _pollutantTypeToCode = pollutantTypeToCode;
            _gasSpecificHeatKJKgK = gasSpecificHeatKJKgK;
            _liquidSpecificHeatKJKgK = liquidSpecificHeatKJKgK;
            _thermalEffectiveness = thermalEffectiveness;
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
            // from LiquidToGasRatio (L per m3 gas) — so convert the wired
            // mass flow into the equivalent ratio. Falls back to the design
            // template's own ratio when no liquid stream is wired.
            bool liquidWired = inlet.Liquid != null && inlet.Liquid.MassFlowKgS > 0 && gasIn.ActualFlowM3Hr > 0;
            if (liquidWired)
            {
                double liquidFlowM3Hr = inlet.Liquid!.MassFlowKgS / Math.Max(vm.LiquidDensity, 1.0) * 3600.0;
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

            // Upstream liquid loading → inlet liquid mole fraction x2.
            vm.InletLiquidPollutantMoleFraction =
                liquidWired
                    ? ResolveInletLiquidMoleFraction(vm, inlet.Liquid!)
                    : 0.0;

            var result = _engine.RunCalculation(vm);
            LastResult = result;

            // An infeasible result carries no valid geometry or removal.
            // Fail the flowsheet pass instead of propagating zeros.
            if (!result.IsFeasible)
            {
                string detail =
                    result.InfeasibilityReason ?? "Design is infeasible.";

                if (result.MaxAchievableRemovalPercent > 0.0)
                    detail += $" Max achievable removal: {result.MaxAchievableRemovalPercent:F2} %.";

                if (result.MinLGRatio > 0.0)
                    detail += $" Minimum L/G: {result.MinLGRatio:F3}.";

                throw new InvalidOperationException(
                    $"Unit '{Name}': {detail}");
            }

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

            // ── Liquid mass flow in (kg/s) ──
            double liquidInKgS = liquidWired
                ? inlet.Liquid!.MassFlowKgS
                : result.LiquidFlowRateM3Hr * vm.LiquidDensity / 3600.0;

            // ── Gas outlet temperature: countercurrent sensible-heat balance ──
            double tGasInK = gasIn.TemperatureC + 273.15;
            double gasDensityKgM3 =
                gasIn.PressurePa * (vm.CarrierGasMolecularWeightGMol / 1000.0)
                / (GasConstantJMolK * tGasInK);
            double gasMassKgS = gasIn.ActualFlowM3Hr * gasDensityKgM3 / 3600.0;

            double gasCapacityKWK = gasMassKgS * _gasSpecificHeatKJKgK;
            double liquidCapacityKWK = liquidInKgS * _liquidSpecificHeatKJKgK;

            double heatGasToLiquidKW = 0.0;
            double gasOutTemperatureC = gasIn.TemperatureC;

            if (gasCapacityKWK > 0.0 && liquidCapacityKWK > 0.0)
            {
                double liquidInletTemperatureC = vm.LiquidTemperature;
                heatGasToLiquidKW =
                    _thermalEffectiveness
                    * Math.Min(gasCapacityKWK, liquidCapacityKWK)
                    * (gasIn.TemperatureC - liquidInletTemperatureC);
                gasOutTemperatureC = gasIn.TemperatureC - heatGasToLiquidKW / gasCapacityKWK;
            }

            // ── Outlet gas pressure and volumetric flow ──
            double gasOutPressurePa = Math.Max(gasIn.PressurePa - result.PressureDrop, 1.0);
            double tGasOutK = gasOutTemperatureC + 273.15;

            // Molar flow is unchanged by trace absorption; V ∝ T / P.
            double gasOutFlowM3Hr =
                gasIn.ActualFlowM3Hr
                * (gasIn.PressurePa / gasOutPressurePa)
                * (tGasOutK / tGasInK);

            var gasOut = new ProcessStream
            {
                ActualFlowM3Hr = gasOutFlowM3Hr,
                TemperatureC = gasOutTemperatureC,
                PressurePa = gasOutPressurePa,
                PollutantPpmByCode = outletPpm
            };

            // ── Liquid outlet: absorbed mass and pollutant loading ──
            // Total gas molar flow, mol/hr, at the inlet state.
            double gasMolPerHr =
                gasIn.PressurePa * gasIn.ActualFlowM3Hr
                / (GasConstantJMolK * tGasInK);

            var loadingIn = inlet.Liquid?.PollutantLoadingKgKg
                ?? new Dictionary<string, double>();

            var absorbedKgS = new Dictionary<string, double>();
            foreach (var p in vm.Pollutants)
            {
                if (!_pollutantTypeToCode.TryGetValue(p.PollutantType, out var code)) continue;
                if (!gasIn.PollutantPpmByCode.TryGetValue(code, out var ppmIn)) continue;
                if (!outletPpm.TryGetValue(code, out var ppmOut)) continue;

                double absorbedMolPerHr =
                    Math.Max(ppmIn - ppmOut, 0.0) * 1e-6 * gasMolPerHr;

                double kgS = absorbedMolPerHr * (p.MolecularWeight / 1000.0) / 3600.0;
                absorbedKgS[code] = (absorbedKgS.TryGetValue(code, out var prior) ? prior : 0.0) + kgS;
            }

            double totalAbsorbedKgS = absorbedKgS.Values.Sum();
            double liquidOutKgS = liquidInKgS + totalAbsorbedKgS;

            var loadingOut = new Dictionary<string, double>();
            foreach (var code in loadingIn.Keys.Union(absorbedKgS.Keys))
            {
                double inKgS = (loadingIn.TryGetValue(code, out var w) ? w : 0.0) * liquidInKgS;
                double addKgS = absorbedKgS.TryGetValue(code, out var a) ? a : 0.0;
                loadingOut[code] = liquidOutKgS > 0.0 ? (inKgS + addKgS) / liquidOutKgS : 0.0;
            }

            // Engine liquid outlet temperature carries the heat of absorption;
            // the sensible heat taken from the gas is added here.
            double engineLiquidOutC = result.LiquidOutletTemperatureK > 0
                ? result.LiquidOutletTemperatureK - 273.15
                : result.LiquidOutletTemperature;

            double liquidOutTemperatureC =
                liquidCapacityKWK > 0.0
                    ? engineLiquidOutC + heatGasToLiquidKW / liquidCapacityKWK
                    : engineLiquidOutC;

            var liquidOut = new LiquidStream
            {
                MassFlowKgS = liquidOutKgS,
                TemperatureC = liquidOutTemperatureC,
                PollutantLoadingKgKg = loadingOut
            };

            return new FlowsheetPorts { Gas = gasOut, Liquid = liquidOut };
        }

        /// <summary>
        /// Converts the upstream liquid loading (mass fraction, kg pollutant / kg
        /// liquid) of the tower pollutant into the mole fraction x2 the engine
        /// expects. The engine applies one x2 to every pollutant, so loading is
        /// accepted only for a single-pollutant tower.
        /// </summary>
        private double ResolveInletLiquidMoleFraction(
            CreateDesignViewModel vm,
            LiquidStream liquidIn)
        {
            PollutantInputViewModel? loadedPollutant = null;
            double massFraction = 0.0;
            int loadedCount = 0;

            foreach (var p in vm.Pollutants)
            {
                if (_pollutantTypeToCode.TryGetValue(p.PollutantType, out var code) &&
                    liquidIn.PollutantLoadingKgKg.TryGetValue(code, out var w) &&
                    w > 0.0)
                {
                    loadedPollutant = p;
                    massFraction = w;
                    loadedCount++;
                }
            }

            if (loadedCount == 0 || loadedPollutant == null)
                return 0.0;

            if (vm.Pollutants.Count != 1)
                throw new InvalidOperationException(
                    $"Unit '{Name}': upstream liquid pollutant loading is only supported " +
                    "for a single-pollutant tower (the engine applies one inlet liquid " +
                    "mole fraction to every pollutant).");

            if (massFraction >= 1.0)
                throw new InvalidOperationException(
                    $"Unit '{Name}': upstream liquid loading must be a mass fraction below 1.");

            double mwPollutantGMol = loadedPollutant.MolecularWeight;
            double mwSolventGMol = vm.LiquidMolecularWeightGMol;

            if (!(mwPollutantGMol > 0.0) || !(mwSolventGMol > 0.0))
                throw new InvalidOperationException(
                    $"Unit '{Name}': pollutant and solvent molecular weights must be positive " +
                    "to convert liquid loading to a mole fraction.");

            double molPollutant = massFraction / mwPollutantGMol;
            double molSolvent = (1.0 - massFraction) / mwSolventGMol;

            return molPollutant / (molPollutant + molSolvent);
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