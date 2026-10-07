using WetScrubber.Business.MassTransfer;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Database.Enums;
using WetScrubber.Models;

namespace WetScrubber.Services
{
    /// <summary>
    /// Core engineering calculation engine for wet scrubber design.
    /// Methods: Tower diameter (Souders-Brown), NTU/HTU (Colburn),
    /// Pressure drop (Billet-Schultes), Venturi (Calvert),
    /// Spray Tower, Reactive scrubbing (Hatta), Power sizing.
    /// </summary>
    public class ScrubberCalculationEngine
    {
        private const double GasConstant = 8.314;   // J/(mol·K)
        private const double GravityAccel = 9.81;    // m/s²

        // ── Phase 1 — real-gas density (Peng-Robinson) ──────────────
        // Both null by default so `new ScrubberCalculationEngine()`
        // (the existing call in ScrubberController) keeps behaving
        // exactly as before — ideal-gas density correction only.
        // Pass both in via the second constructor to switch a given
        // instance over to real-gas density wherever pollutant
        // critical-property data exists; any lookup miss or EOS
        // failure falls back to the ideal-gas line automatically
        // (see GetActualGasDensity), it never throws up into a design.
        private readonly IEquationOfState? _eos;
        private readonly IComponentPropertyLookup? _componentLookup;

        // ── Phase 1 — Van 't Hoff Henry's Law + NRTL activity ───────
        // Same "all optional, hard fallback on miss" contract as _eos
        // above. _henrysLawLookup missing/no-data-for-species falls
        // back to the original hardcoded tempCoeff=2000. _activityModel
        // / _nrtlLookup missing/no pair found falls back to gamma=1
        // (ideal solution) — expected today since NrtlBinaryParameters
        // ships empty (see NrtlBinaryParameter.cs).
        private readonly IHenrysLawLookup? _henrysLawLookup;
        private readonly IActivityCoefficientModel? _activityModel;
        private readonly INrtlBinaryParameterLookup? _nrtlLookup;

        // ── Phase 2 — Onda rate-based film coefficients ─────────────
        // Same optional/fallback contract. Missing lookup, missing
        // DiffusionProperty rows, or any Onda/Wilke-Chang/Fuller
        // exception falls back to CalculateNtuHtu's fixed
        // DefaultGasFilmCoeff/DefaultLiquidFilmCoeff — never breaks an
        // existing design.
        private readonly IDiffusionCoefficientLookup? _diffusionLookup;
        private readonly IPackingLookup? _packingLookup;

        // ── Centralized Van 't Hoff math ─────────────────────────────
        // Single implementation used by GetHenrysLawConstant,
        // GetVanTHoffTempCoeff-derived callers, and the three
        // HenrysLawTemperatureCorrectionFn lambdas below — those four
        // call sites used to each carry their own copy of
        // "-(heatOfSolutionKJmol*1000)/R" + "exp(coeff*(1/T-1/298.15))".
        // Not injectable via constructor (deliberately no new public
        // surface here) — always available, same as _eos/_componentLookup
        // being the only things that actually change engine behavior.
        private readonly IHenrysLawCalculator _henrysLawCalculator =
            new HenrysLawCalculator();

        public ScrubberCalculationEngine() { }

        public ScrubberCalculationEngine(
            IEquationOfState eos,
            IComponentPropertyLookup componentLookup,
            IHenrysLawLookup? henrysLawLookup = null,
            IActivityCoefficientModel? activityModel = null,
            INrtlBinaryParameterLookup? nrtlLookup = null,
            IDiffusionCoefficientLookup? diffusionLookup = null,
            IPackingLookup? packingLookup = null)
        {
            _eos = eos;
            _componentLookup = componentLookup;
            _henrysLawLookup = henrysLawLookup;
            _activityModel = activityModel;
            _nrtlLookup = nrtlLookup;
            _diffusionLookup = diffusionLookup;
            _packingLookup = packingLookup;
        }

        // ── Packing material defaults (Pall Rings 50mm) ───────────
        private const double DefaultPackingFactor = 66.0;    // Fp, 1/m
        private const double DefaultSurfaceArea = 112.0;     // m²/m³
        private const double DefaultVoidFraction = 0.951;    // ε
        private const double DefaultGasFilmCoeff = 0.03;     // kGa kmol/m³·hr·kPa
        private const double DefaultLiquidFilmCoeff = 0.01;  // kLa m/hr — fallback path only

        // Phase 2 — Onda correlation packing/fluid defaults. Same
        // "standard textbook value, not independently sourced" caveat
        // as DefaultPackingFactor/DefaultSurfaceArea above.
        private const double DefaultNominalPackingSizeM = 0.05;
        private const double DefaultPackingCriticalSurfaceTensionNM = 0.075;
        private const double DefaultLiquidSurfaceTensionNM = 0.0728;
        private const double WaterMolarDensityKmolM3 = 55.3;
        private const double SolventMolecularWeightGMol = 18.02;

        // ════════════════════════════════════════════════════════════
        //  MAIN ENTRY — run full calculation based on scrubber type
        // ════════════════════════════════════════════════════════════
        public CalculationResult RunCalculation(CreateDesignViewModel vm)
        {
            return vm.ScrubberType switch
            {
                ScrubberType.PackedTower => RunPackedTowerCalc(vm),
                ScrubberType.VenturiScrubber => RunVenturiCalc(vm),
                ScrubberType.SprayTower => RunSprayTowerCalc(vm),
                _ => RunPackedTowerCalc(vm)
            };
        }

        // ════════════════════════════════════════════════════════════
        //  PACKED TOWER
        // ════════════════════════════════════════════════════════════
        private CalculationResult RunPackedTowerCalc(CreateDesignViewModel vm)
        {
            var result = new CalculationResult();

            // Use first pollutant for primary calculation
            var pollutant =
                vm.Pollutants.FirstOrDefault()
                ?? new PollutantInputViewModel();

            // 1. Liquid flow rate from L/G ratio
            double liquidFlowM3Hr =
                vm.ActualFlowRate *
                vm.LiquidToGasRatio /
                1000.0;

            // 2. Tower diameter
            result.TowerDiameter = CalculateTowerDiameter(
                gasFlowRateNm3Hr: vm.NormalFlowRate,
                gasTemperatureC: vm.InletTemperature,
                gasPressurePa: vm.InletPressure,
                liquidFlowRateM3Hr: liquidFlowM3Hr,
                gasDensityKgM3: vm.GasDensity,
                liquidDensityKgM3: vm.LiquidDensity,
                packingFactor: DefaultPackingFactor,
                liquidViscosityMPas: vm.LiquidViscosity,

                // Phase 1: real-gas density when this engine instance
                // was built with an EOS + lookup and ComponentProperties
                // has this pollutant. Falls back to the original
                // ideal-gas number otherwise.
                pollutantTypeId: pollutant.PollutantType,
                inletConcentrationPpm: pollutant.InletConcentration,

                packingSpecificAreaM2M3:
                    _packingLookup?
                        .GetByCode(vm.PackingCode)?
                        .SpecificAreaM2M3
                    ?? DefaultSurfaceArea,

                voidageFraction: DefaultVoidFraction
            );

            // 3. NTU / HTU → packing height
            //
            // Phase 1: real per-species Van't Hoff coefficient +
            // NRTL activity correction when data/wiring exists.
            //
            // The effective Henry constant is evaluated at the
            // liquid temperature and actual operating pressure.
            double actualGasDensity = GetActualGasDensity(
                vm.GasDensity,
                vm.InletTemperature + 273.15,
                vm.InletPressure,
                pollutant.PollutantType,
                pollutant.InletConcentration);

            double henrysTemp =
                GetEffectiveHenrysLawConstant(
                    pollutant,
                    vm.LiquidTemperature,
                    vm.InletPressure / 1000.0);

            // Calculate gas flow and cross-sectional area
            double gasFlowM3S =
                vm.ActualFlowRate / 3600.0;

            double crossSection =
                Math.PI *
                Math.Pow(result.TowerDiameter, 2) /
                4.0;

            // Gas / liquid mass velocities (kg/m²·s)
            double gasMassVelocity =
                (gasFlowM3S * actualGasDensity) /
                crossSection;

            double liquidMassVelocity =
                (liquidFlowM3Hr / 3600.0 * vm.LiquidDensity) /
                crossSection;

            double molarLG =
                CalculateMolarLiquidToGasRatio(
                    vm.LiquidToGasRatio,
                    vm.LiquidDensity,
                    vm.InletTemperature,
                    vm.InletPressure);

            // Phase 2: Wilke-Chang + Fuller diffusivities feeding
            // the Onda correlation for physically-derived kG/kL/aW.
            //
            // Falls back to CalculateNtuHtu (old fixed-coefficient
            // path) whenever required lookup data is unavailable.
            var rateBased =
                TryComputeOndaFilmCoefficients(
                    pollutant,
                    vm,
                    gasMassVelocity,
                    liquidMassVelocity,
                    henrysTemp,
                    vm.PackingCode,
                    actualGasDensity);

            var ntuResult =
                rateBased != null
                    ? CalculateNtuHtuRateBased(
                        inletConcentrationPpm:
                            pollutant.InletConcentration,

                        outletConcentrationPpm:
                            pollutant.TargetOutletConcentration,

                        henrysLawConstant:
                            henrysTemp,

                        liquidToGasRatioMolar:
                            molarLG,

                        gasMolarVelocityKmolM2S:
                            rateBased.GasMolarVelocityKmolM2S,

                        overallKGaKmolM3S:
                            rateBased.OverallKGaKmolM3S)
                    : CalculateNtuHtu(
                        inletConcentrationPpm:
                            pollutant.InletConcentration,

                        outletConcentrationPpm:
                            pollutant.TargetOutletConcentration,

                        henrysLawConstant:
                            henrysTemp,

                        liquidToGasRatioMolar:
                            molarLG,

                        gasFilmCoeff:
                            DefaultGasFilmCoeff,

                        liquidFilmCoeff:
                            DefaultLiquidFilmCoeff,

                        gasMassVelocity:
                            gasMassVelocity,

                        gasDensityKgM3:
                            actualGasDensity
                    );

            double designPackingHeight =
                vm.PackingHeightOverride > 0
                    ? vm.PackingHeightOverride
                    : Math.Ceiling(
                        ntuResult.PackingHeight * 100.0) / 100.0;

            result.PackingHeight =
                Math.Round(designPackingHeight, 2);

            result.NTU =
                Math.Round(
                    designPackingHeight /
                    Math.Max(ntuResult.HTU, 1e-9),
                    2);

            result.HTU =
                Math.Round(ntuResult.HTU, 2);

            result.AbsorptionFactor =
                Math.Round(
                    ntuResult.AbsorptionFactor,
                    3);

            result.RemovalEfficiency =
                Math.Round(
                    PackedTowerEfficiencyCalculator.AtHeight(
                        designPackingHeight,
                        ntuResult.HTU,
                        ntuResult.AbsorptionFactor),
                    2);

            // 4. Total tower height =
            // packing + 30% freeboard + 1m sump + 1m top
            result.TowerHeight =
                Math.Round(
                    result.PackingHeight * 1.3 + 2.0,
                    2);

            // 5. Gas velocity inside tower
            result.GasVelocity =
                Math.Round(
                    gasFlowM3S / crossSection,
                    2);

            // 6. Pressure drop — use the ACTUALLY SELECTED packing's
            // surface area/voidage, not the hardcoded default.
            var pdPackingData =
                _packingLookup?
                    .GetByCode(vm.PackingCode);

            double pdSurfaceArea =
                pdPackingData?.SpecificAreaM2M3
                ?? DefaultSurfaceArea;

            double pdNominalSizeM =
                pdPackingData?.NominalSizeM
                ?? DefaultNominalPackingSizeM;

            double pdVoidFraction =
                DefaultVoidFraction;

            result.PressureDrop =
                Math.Round(
                    CalculatePressureDrop(
                        gasVelocityMs:
                            result.GasVelocity,

                        liquidLoadingM3M2Hr:
                            liquidFlowM3Hr /
                            crossSection,

                        gasDensityKgM3:
                            actualGasDensity,

                        liquidDensityKgM3:
                            vm.LiquidDensity,

                        packingSurfaceAreaM2M3:
                            pdSurfaceArea,

                        voidFraction:
                            pdVoidFraction,

                        liquidViscosityPas:
                            vm.LiquidViscosity /
                            1000.0
                    ) *
                    result.PackingHeight,
                    2);

            // 6b. Flooding check — Sherwood-Shipley-Holloway.
            var floodResult =
                PressureDropFloodingCorrelation.Calculate(
                    packingSpecificAreaM2M3:
                        pdSurfaceArea,

                    voidageFraction:
                        pdVoidFraction,

                    nominalPackingSizeM:
                        pdNominalSizeM,

                    gasMassVelocityKgM2S:
                        gasMassVelocity,

                    liquidMassVelocityKgM2S:
                        liquidMassVelocity,

                    gasDensityKgM3:
                        actualGasDensity,

                    liquidDensityKgM3:
                        vm.LiquidDensity,

                    gasViscosityPas:
                        vm.GasViscosity,

                    liquidViscosityPas:
                        vm.LiquidViscosity /
                        1000.0);

            result.PercentFlood =
                Math.Round(
                    floodResult.PercentFlood,
                    1);

            result.FloodingGasVelocity =
                Math.Round(
                    floodResult.FloodingGasVelocityMS,
                    3);

            result.ExceedsRecommendedFlood =
                floodResult.ExceedsRecommendedFlood;

            // 7. Power
            result.FanPowerKW =
                Math.Round(
                    CalculateFanPower(
                        gasFlowM3S,
                        result.PressureDrop + 500),
                    2);

            result.PumpPowerKW =
                Math.Round(
                    CalculatePumpPower(
                        liquidFlowM3Hr,
                        result.TowerHeight + 5,
                        vm.LiquidDensity),
                    2);

            // 8. L/G minimum ratio check
            double minMolarLG =
                CalculateMinimumLiquidGasRatio(
                    pollutant.InletConcentration,
                    pollutant.TargetOutletConcentration,
                    henrysTemp);

            result.MinLGRatio =
                Math.Round(
                    MolarToVolumetricLiquidToGasRatio(
                        minMolarLG,
                        vm.LiquidDensity,
                        vm.InletTemperature,
                        vm.InletPressure),
                    3);

            result.ActualLGRatio =
                vm.LiquidToGasRatio;

            result.LiquidFlowRateM3Hr =
                Math.Round(
                    liquidFlowM3Hr,
                    2);

            result.ScrubberType =
                "Packed Tower";

            // 9. Sensitivity analysis for chart
            result.SensitivityPoints =
                RunLGRatioSensitivity(
                    pollutant.InletConcentration,
                    henrysTemp,
                    ntuResult.NTU,
                    ntuResult.HTU,
                    pollutant.TargetOutletConcentration,
                    vm.LiquidDensity,
                    vm.InletTemperature,
                    vm.InletPressure);

            // ── Phase 4a: Multi-pollutant iterative solver ─────────
            if (vm.Pollutants.Count > 1)
            {
                var odeResult =
                    TryComputeMultiPollutantOdeSolution(
                        vm,
                        henrysTemp,
                        result.TowerHeight,
                        crossSection);

                if (odeResult?.Converged == true)
                {
                    double totalRemovalOde =
                        odeResult
                            .OverallRemovalEfficiency
                            .Values
                            .Average();

                    result.RemovalEfficiency =
                        Math.Round(
                            totalRemovalOde,
                            2);

                    result.LiquidOutletTemperature =
                        Math.Round(
                            odeResult.LiquidOutletTemperatureC,
                            1);

                    result.HeatAbsorbedKW =
                        Math.Round(
                            odeResult.TotalHeatAbsorbedKW,
                            2);

                    return result;
                }

                var multiResult =
                    TryComputeMultiPollutantIterativeSolution(
                        vm,
                        henrysTemp,
                        result.TowerHeight,
                        crossSection);

                if (multiResult?.Converged == true)
                {
                    double totalRemoval =
                        multiResult
                            .OverallRemovalEfficiency
                            .Values
                            .Average();

                    result.RemovalEfficiency =
                        Math.Round(
                            totalRemoval,
                            2);

                    result.LiquidOutletTemperature =
                        Math.Round(
                            multiResult.LiquidOutletTemperatureC,
                            1);

                    result.HeatAbsorbedKW =
                        Math.Round(
                            multiResult.TotalHeatAbsorbedKW,
                            2);

                    return result;
                }
            }

            // Fallback: single-pollutant
            if (vm.Pollutants.Count == 0)
                return result;

            return result;
        }

        // ════════════════════════════════════════════════════════════
        //  VENTURI SCRUBBER
        // ════════════════════════════════════════════════════════════
        private CalculationResult RunVenturiCalc(
            CreateDesignViewModel vm)
        {
            var result =
                new CalculationResult();

            var pollutant =
                vm.Pollutants.FirstOrDefault()
                ?? new PollutantInputViewModel();

            double gasFlowM3S =
                vm.ActualFlowRate / 3600.0;

            var venturi =
                CalculateVenturiSizing(
                    gasFlowRateM3S:
                        gasFlowM3S,

                    throatVelocityMs:
                        vm.VenturiThroatVelocityMs,

                    liquidToGasRatioLM3:
                        vm.LiquidToGasRatio,

                    gasDensityKgM3:
                        vm.GasDensity,

                    particleDensityKgM3:
                        vm.VenturiParticleDensityKgM3,

                    particleDiameterMicron:
                        vm.VenturiParticleDiameterMicron,

                    liquidDensityKgM3:
                        vm.LiquidDensity,

                    gasViscosityPas:
                        vm.GasViscosity
                );

            result.TowerDiameter =
                Math.Round(
                    venturi.ThroatDiameter * 2.5,
                    3);

            result.TowerHeight =
                Math.Round(
                    venturi.ThroatDiameter * 8,
                    2);

            result.PackingHeight = 0;

            result.PressureDrop =
                Math.Round(
                    venturi.PressureDrop,
                    0);

            result.RemovalEfficiency =
                Math.Round(
                    venturi.CollectionEfficiency,
                    2);

            result.GasVelocity =
                Math.Round(
                    venturi.ThroatVelocity,
                    1);

            result.FanPowerKW =
    Math.Round(
        CalculateFanPower(
            gasFlowM3S,
            result.PressureDrop),
        2);

            result.PumpPowerKW =
                Math.Round(
                    CalculatePumpPower(
                        vm.ActualFlowRate *
                        vm.LiquidToGasRatio /
                        1000.0,
                        10,
                        vm.LiquidDensity),
                    2);

            result.LiquidFlowRateM3Hr =
                Math.Round(
                    vm.ActualFlowRate *
                    vm.LiquidToGasRatio /
                    1000.0,
                    2);

            result.ActualLGRatio =
                vm.LiquidToGasRatio;

            result.ScrubberType =
                "Venturi Scrubber";

            result.NTU = 0;
            result.HTU = 0;

            return result;
        }

        // ════════════════════════════════════════════════════════════
        //  SPRAY TOWER
        // ════════════════════════════════════════════════════════════
        private CalculationResult RunSprayTowerCalc(
            CreateDesignViewModel vm)
        {
            var result =
                new CalculationResult();

            // Design gas velocity 0.8 m/s for spray tower
            double designVelocity = 0.8;

            double gasFlowM3S =
                vm.ActualFlowRate / 3600.0;

            double crossSection =
                gasFlowM3S / designVelocity;

            double diameter =
                Math.Sqrt(
                    4.0 *
                    crossSection /
                    Math.PI);

            var pollutant =
                vm.Pollutants.FirstOrDefault()
                ?? new PollutantInputViewModel();

            result.TowerDiameter =
                Math.Round(
                    RoundUpDiameter(diameter),
                    2);

            result.TowerHeight =
                Math.Round(
                    gasFlowM3S * 5 + 2.0,
                    2);

            result.PackingHeight = 0;

            result.GasVelocity =
                Math.Round(
                    designVelocity,
                    2);

            result.RemovalEfficiency =
                Math.Round(
                    (1 -
                     Math.Exp(
                         -0.5 *
                         vm.LiquidToGasRatio)) *
                    100,
                    2);

            // Pressure drop — velocity-head approximation.
            const double SprayTowerLossCoefficient = 1.5;

            double dynamicPressurePa =
                vm.GasDensity *
                Math.Pow(
                    designVelocity,
                    2) /
                2.0;

            result.PressureDrop =
                Math.Round(
                    dynamicPressurePa *
                    SprayTowerLossCoefficient,
                    0);

            result.FanPowerKW =
                Math.Round(
                    CalculateFanPower(
                        gasFlowM3S,
                        result.PressureDrop + 300),
                    2);

            result.PumpPowerKW =
                Math.Round(
                    CalculatePumpPower(
                        vm.ActualFlowRate *
                        vm.LiquidToGasRatio /
                        1000.0,
                        8,
                        vm.LiquidDensity),
                    2);

            result.LiquidFlowRateM3Hr =
                Math.Round(
                    vm.ActualFlowRate *
                    vm.LiquidToGasRatio /
                    1000.0,
                    2);

            result.ActualLGRatio =
                vm.LiquidToGasRatio;

            result.ScrubberType =
                "Spray Tower";

            result.NTU = 0;
            result.HTU = 0;

            return result;
        }

        // ════════════════════════════════════════════════════════════
        //  1. TOWER DIAMETER  (Souders-Brown / Fair's GPDC)
        // ════════════════════════════════════════════════════════════
        public double CalculateTowerDiameter(
            double gasFlowRateNm3Hr,
            double gasTemperatureC,
            double gasPressurePa,
            double liquidFlowRateM3Hr,
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double packingFactor,
            double liquidViscosityMPas,
            double floodingFactor = 0.65,
            int? pollutantTypeId = null,
            double inletConcentrationPpm = 0,
            double packingSpecificAreaM2M3 = 0,
            double voidageFraction = 0)
        {
            double tempK =
                gasTemperatureC + 273.15;

            double gasDensityAct =
                GetActualGasDensity(
                    gasDensityKgM3,
                    tempK,
                    gasPressurePa,
                    pollutantTypeId,
                    inletConcentrationPpm);

            double gasFlowM3S =
                gasFlowRateNm3Hr *
                (tempK / 273.15) *
                (101325.0 / gasPressurePa) /
                3600.0;

            double gasFlowKgS =
                gasFlowM3S *
                gasDensityAct;

            double liquidFlowKgS =
                (liquidFlowRateM3Hr / 3600.0) *
                liquidDensityKgM3;

            double aT =
                packingSpecificAreaM2M3 > 0
                    ? packingSpecificAreaM2M3
                    : DefaultSurfaceArea;

            double eps =
                voidageFraction > 0 &&
                voidageFraction < 1
                    ? voidageFraction
                    : DefaultVoidFraction;

            double uFlood =
                WetScrubber.Business.MassTransfer
                    .PressureDropFloodingCorrelation
                    .FloodingVelocity(
                        packingSpecificAreaM2M3: aT,
                        voidageFraction: eps,
                        flowRatio:
                            liquidFlowKgS /
                            Math.Max(
                                gasFlowKgS,
                                1e-9),
                        gasDensityKgM3:
                            gasDensityAct,
                        liquidDensityKgM3:
                            liquidDensityKgM3,
                        liquidViscosityPas:
                            liquidViscosityMPas /
                            1000.0);

            double uOp =
                uFlood *
                floodingFactor;

            double area =
                gasFlowM3S /
                Math.Max(
                    uOp,
                    0.01);

            double diam =
                Math.Sqrt(
                    4.0 *
                    area /
                    Math.PI);

            return Math.Round(
                RoundUpDiameter(diam),
                3);
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 1 — real-gas density via Peng-Robinson
        // ════════════════════════════════════════════════════════════
        private double GetActualGasDensity(
            double gasDensityKgM3StpUserInput,
            double tempK,
            double gasPressurePa,
            int? pollutantTypeId,
            double inletConcentrationPpm)
        {
            double idealGasFallback =
                gasDensityKgM3StpUserInput *
                (273.15 / tempK) *
                (gasPressurePa / 101325.0);

            if (_eos == null ||
                _componentLookup == null ||
                pollutantTypeId == null)
            {
                return idealGasFallback;
            }

            try
            {
                var mixture =
                    GasMixtureBuilder
                        .BuildPollutantInAirMixture(
                            pollutantTypeId:
                                pollutantTypeId.Value,
                            inletConcentrationPpm:
                                inletConcentrationPpm,
                            lookup:
                                _componentLookup);

                var result =
                    _eos.Evaluate(
                        mixture,
                        tempK,
                        gasPressurePa / 1000.0);

                return result.DensityKgM3;
            }
            catch
            {
                return idealGasFallback;
            }
        }

        private double RoundUpDiameter(double d)
        {
            double[] std =
            {
                0.3,
                0.45,
                0.6,
                0.75,
                0.9,
                1.0,
                1.2,
                1.5,
                1.8,
                2.0,
                2.4,
                3.0,
                3.6,
                4.0,
                4.5,
                5.0
            };

            foreach (var s in std)
            {
                if (s >= d)
                    return s;
            }

            return Math.Ceiling(d / 0.5) * 0.5;
        }

        // ════════════════════════════════════════════════════════════
        //  2. NTU / HTU  (Colburn equation)
        // ════════════════════════════════════════════════════════════

        public NtuHtuResult CalculateNtuHtu(
            double inletConcentrationPpm,
            double outletConcentrationPpm,
            double henrysLawConstant,
            double liquidToGasRatioMolar,
            double gasFilmCoeff,
            double liquidFilmCoeff,
            double gasMassVelocity,
            double gasDensityKgM3)
        {
            double y1 =
                Math.Max(
                    inletConcentrationPpm,
                    0.001) /
                1e6;

            double y2 =
                Math.Max(
                    outletConcentrationPpm,
                    0.0001) /
                1e6;

            double A =
                liquidToGasRatioMolar /
                Math.Max(
                    henrysLawConstant,
                    HenrysConstantUnits.MinimumH);

            double NTU;

            if (Math.Abs(A - 1.0) < 0.01)
            {
                NTU =
                    (y1 - y2) /
                    y2;
            }
            else
            {
                double term =
                    (y1 / y2) *
                    (1 - 1.0 / A) +
                    1.0 / A;

                NTU =
                    (A / (A - 1)) *
                    Math.Log(
                        Math.Max(
                            term,
                            0.0001));
            }

            NTU =
                Math.Max(
                    NTU,
                    0.5);

            double KGa =
                1.0 /
                (
                    1.0 /
                    Math.Max(
                        gasFilmCoeff,
                        0.001)
                    +
                    henrysLawConstant /
                    Math.Max(
                        liquidFilmCoeff,
                        0.001)
                );

            // Keep the existing legacy scale correction for the
            // fixed-coefficient fallback path. The rate-based Onda
            // calculation does not use this path.
            double Kya =
                KGa * 100;

            double HTU =
                gasMassVelocity /
                Math.Max(
                    Kya *
                    gasDensityKgM3,
                    0.001);

            // Keep the existing legacy clamp for compatibility with
            // the fixed-coefficient fallback calculation.
            HTU =
                Math.Min(
                    Math.Max(
                        HTU,
                        0.5),
                    2.0);

            double packingHeight =
                NTU * HTU;

            return new NtuHtuResult
            {
                NTU = NTU,
                HTU = HTU,
                PackingHeight = packingHeight,
                AbsorptionFactor = A,
                RemovalEfficiency =
                    Math.Min(
                        (1.0 - y2 / y1) *
                        100.0,
                        99.99)
            };
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 2 — rate-based NTU/HTU using Onda-derived film
        //  coefficients.
        // ════════════════════════════════════════════════════════════
        public NtuHtuResult CalculateNtuHtuRateBased(
            double inletConcentrationPpm,
            double outletConcentrationPpm,
            double henrysLawConstant,
            double liquidToGasRatioMolar,
            double gasMolarVelocityKmolM2S,
            double overallKGaKmolM3S)
        {
            double y1 =
                Math.Max(
                    inletConcentrationPpm,
                    0.001) /
                1e6;

            double y2 =
                Math.Max(
                    outletConcentrationPpm,
                    0.0001) /
                1e6;

            double A =
                liquidToGasRatioMolar /
                Math.Max(
                    henrysLawConstant,
                    0.001);

            double NTU;

            if (Math.Abs(A - 1.0) < 0.01)
            {
                NTU =
                    (y1 - y2) /
                    y2;
            }
            else
            {
                double term =
                    (y1 / y2) *
                    (1 - 1.0 / A) +
                    1.0 / A;

                NTU =
                    (A / (A - 1)) *
                    Math.Log(
                        Math.Max(
                            term,
                            0.0001));
            }

            NTU =
                Math.Max(
                    NTU,
                    0.5);

            double HTU =
                gasMolarVelocityKmolM2S /
                Math.Max(
                    overallKGaKmolM3S,
                    1e-9);

            double packingHeight =
                NTU * HTU;

            return new NtuHtuResult
            {
                NTU = NTU,
                HTU = HTU,
                PackingHeight = packingHeight,
                AbsorptionFactor = A,
                RemovalEfficiency =
                    Math.Min(
                        (1.0 - y2 / y1) *
                        100.0,
                        99.99)
            };
        }

        private sealed class RateBasedFilmResult
        {
            public double GasMolarVelocityKmolM2S { get; set; }

            public double OverallKGaKmolM3S { get; set; }
        }

        // Shared by all Henry's-law temperature-correction callers.
        private double GetHenrysLawTemperatureCorrectionFactor(
            string pollutantCode,
            double temperatureC)
        {
            var data =
                _henrysLawLookup?
                    .GetByPollutantCode(
                        pollutantCode);

            return
                _henrysLawCalculator
                    .GetTemperatureCorrectedHenrysConstant(
                        referenceHenrysConstantAt25C: 1.0,
                        heatOfSolutionKJmol:
                            data?.HeatOfSolutionKJmol,
                        temperatureC:
                            temperatureC,
                        fallbackTempCoeffK:
                            0.0);
        }

        // Reagent class and equivalents/L inferred from liquid pH and wt%.
        // pH >= 8 -> NaOH
        // pH <= 6 -> H2SO4
        private (ReagentKind Kind, double MolesPerLiter) GetReagentSpec(
           CreateDesignViewModel vm)
        {
            double massFraction = Math.Max(vm.LiquidConcentration, 0.0) / 100.0;
            double gramsPerL = massFraction * vm.LiquidDensity;

            if (vm.LiquidPH >= 8.0)
                return (ReagentKind.Caustic, gramsPerL / 40.00);

            if (vm.LiquidPH <= 6.0)
                return (ReagentKind.Acid, 2.0 * gramsPerL / 98.08);

            return (ReagentKind.None, 0.0);
        }
        // ════════════════════════════════════════════════════════════
        //  PHASE 2 — Onda-derived film coefficients
        // ════════════════════════════════════════════════════════════
        private RateBasedFilmResult? TryComputeOndaFilmCoefficients(
            PollutantInputViewModel pollutant,
            CreateDesignViewModel vm,
            double gasMassVelocity,
            double liquidMassVelocity,
            double henrysLawConstant,
            string packingCode,
            double actualGasDensityKgM3)
        {
            if (_diffusionLookup == null ||
                _componentLookup == null ||
                _packingLookup == null)
            {
                return null;
            }

            try
            {
                string? pollutantCode =
                    _componentLookup
                        .GetByPollutantId(
                            pollutant.PollutantType)
                        ?.Code;

                if (pollutantCode == null)
                    return null;

                var soluteData =
                    _diffusionLookup
                        .GetByCode(pollutantCode);

                var solventData =
                    _diffusionLookup
                        .GetByCode("H2O");

                var packingData =
                    _packingLookup
                        .GetByCode(packingCode);

                if (soluteData?
                        .MolarVolumeAtBoilingPointCm3Mol == null ||
                    solventData?
                        .AssociationFactor == null ||
                    packingData == null)
                {
                    return null;
                }

                double liquidTempK =
                    vm.LiquidTemperature +
                    273.15;

                double gasTempK =
                    vm.InletTemperature +
                    273.15;

                double pressureKPa =
                    vm.InletPressure / 1000.0;

                double dL =
                    WilkeChangDiffusivity.Calculate(
                        soluteMolarVolumeCm3Mol:
                            soluteData
                                .MolarVolumeAtBoilingPointCm3Mol
                                .Value,
                        solventAssociationFactor:
                            solventData
                                .AssociationFactor
                                .Value,
                        solventMolecularWeightGMol:
                            SolventMolecularWeightGMol,
                        solventViscosityCp:
                            vm.LiquidViscosity,
                        temperatureK:
                            liquidTempK);

                double dG =
                    FullerGasDiffusivity.Calculate(
                        codeA:
                            pollutantCode,
                        molecularWeightA:
                            pollutant.MolecularWeight,
                        codeB:
                            "Air",
                        molecularWeightB:
                            28.97,
                        temperatureK:
                            gasTempK,
                        pressureKPa:
                            pressureKPa);

                var onda =
                    OndaMassTransferCorrelation.Calculate(
                        packingSpecificAreaM2M3:
                            packingData
                                .SpecificAreaM2M3,
                        nominalPackingSizeM:
                            packingData
                                .NominalSizeM,
                        criticalSurfaceTensionNM:
                            packingData
                                .CriticalSurfaceTensionNM,
                        liquidSurfaceTensionNM:
                            DefaultLiquidSurfaceTensionNM,
                        liquidMassVelocityKgM2S:
                            liquidMassVelocity,
                        gasMassVelocityKgM2S:
                            gasMassVelocity,
                        liquidDensityKgM3:
                            vm.LiquidDensity,
                        gasDensityKgM3:
                            actualGasDensityKgM3,
                        liquidViscosityPas:
                            vm.LiquidViscosity /
                            1000.0,
                        gasViscosityPas:
                            vm.GasViscosity,
                        liquidDiffusivityM2S:
                            dL,
                        gasDiffusivityM2S:
                            dG,
                        temperatureK:
                            gasTempK,
                        pressureKPa:
                            pressureKPa);

                // Convert film coefficients to the mole-fraction basis
                // used by the engine's y = H*x Henry-law convention.
                double kGaY =
                    onda.GasFilmCoeffKmolM2SPa *
                    vm.InletPressure *
                    onda.WettedAreaM2M3;

                double kLaX =
                    onda.LiquidFilmCoeffMS *
                    WaterMolarDensityKmolM3 *
                    onda.WettedAreaM2M3;

                // Henry's-law concentration conversion is based on
                // liquid temperature and actual operating pressure.
                double hCgCl =
                    henrysLawConstant *
                    (
                        pressureKPa /
                        (8.314 *
                         liquidTempK)
                    ) /
                    WaterMolarDensityKmolM3;

                var (
                    reagentKind,
                    reagentEqPerL) =
                    GetReagentSpec(vm);

                var enhancement =
                    ReactiveEnhancementService.Compute(
                        new ReactiveEnhancementInput
                        {
                            PollutantCode =
                                pollutantCode,

                            Reagent =
                                reagentKind,

                            ReagentConcentrationEqPerL =
                                reagentEqPerL,

                            LiquidFilmCoeffMS =
                                onda.LiquidFilmCoeffMS,

                            PollutantLiquidDiffusivityM2S =
                                dL,

                            HenrysDimensionless =
                                hCgCl,

                            GasPartialPressureKPa =
                                pollutant.InletConcentration /
                                1_000_000.0 *
                                pressureKPa,

                            TemperatureK =
                                liquidTempK
                        });

                kLaX *=
                    enhancement.Factor;

                double overallKGa =
                    1.0 /
                    (
                        1.0 /
                        Math.Max(
                            kGaY,
                            1e-9)
                        +
                        henrysLawConstant /
                        Math.Max(
                            kLaX,
                            1e-9)
                    );

                double soluteMoleFraction =
                    pollutant.InletConcentration /
                    1_000_000.0;

                double mixtureMW =
                    soluteMoleFraction *
                    pollutant.MolecularWeight
                    +
                    (1 -
                     soluteMoleFraction) *
                    28.97;

                return new RateBasedFilmResult
                {
                    GasMolarVelocityKmolM2S =
                        gasMassVelocity /
                        mixtureMW,

                    OverallKGaKmolM3S =
                        overallKGa
                };
            }
            catch
            {
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 3 — Iterative tower solver with heat feedback
        // ════════════════════════════════════════════════════════════
        private IterativeTowerSolver.SolverOutput?
            TryComputeIterativeTowerSolution(
                PollutantInputViewModel pollutant,
                CreateDesignViewModel vm,
                double henrysLawConstant)
        {
            try
            {
                string? pollutantCode =
                    _componentLookup?
                        .GetByPollutantId(
                            pollutant.PollutantType)
                        ?.Code;

                if (pollutantCode == null)
                    return null;

                double gasFlowM3S =
                    vm.ActualFlowRate /
                    3600.0;

                double gasMassFlowKgS =
                    gasFlowM3S *
                    vm.GasDensity;

                double liquidFlowM3S =
                    vm.LiquidToGasRatio *
                    gasFlowM3S /
                    1000.0;

                double liquidMassFlowKgS =
                    liquidFlowM3S *
                    vm.LiquidDensity;

                var solverInput =
                    new IterativeTowerSolver.SolverInput
                    {
                        GasInletPpm =
                            pollutant
                                .InletConcentration,

                        GasOutletTargetPpm =
                            pollutant
                                .TargetOutletConcentration,

                        GasTemperatureC =
                            vm.InletTemperature,

                        GasMassFlowKgS =
                            gasMassFlowKgS,

                        LiquidInletTempC =
                            vm.LiquidTemperature,

                        LiquidMassFlowKgS =
                            liquidMassFlowKgS,

                        LiquidDensityKgM3 =
                            vm.LiquidDensity,

                        HenrysLawConstantReference =
                            henrysLawConstant,

                        HeatOfAbsorptionKJKmol =
                            HeatOfAbsorption
                                .GetByPollutantCode(
                                    pollutantCode),

                        PollutantMolecularWeight =
                            pollutant.MolecularWeight,

                        HenrysLawTemperatureCorrectionFn =
                            t =>
                                GetHenrysLawTemperatureCorrectionFactor(
                                    pollutantCode,
                                    t)
                    };

                return
                    IterativeTowerSolver
                        .SolveIterative(
                            solverInput,
                            numSegments: 5);
            }
            catch
            {
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 4a — Multi-pollutant iterative solver
        // ════════════════════════════════════════════════════════════
        private MultiPollutantIterativeSolver.SolverOutput?
            TryComputeMultiPollutantIterativeSolution(
                CreateDesignViewModel vm,
                double henrysLawConstantReference,
                double towerHeightM,
                double crossSectionM2)
        {
            if (vm.Pollutants.Count == 0)
                return null;

            try
            {
                var pollutantInputs =
                    new List<
                        MultiPollutantIterativeSolver
                            .PollutantInput>();

                foreach (var pollutant
                    in vm.Pollutants)
                {
                    string? pollutantCode =
                        _componentLookup?
                            .GetByPollutantId(
                                pollutant.PollutantType)
                            ?.Code;

                    if (pollutantCode == null)
                        continue;

                    double effectiveHenry =
                        GetEffectiveHenrysLawConstant(
                            pollutant,
                            vm.LiquidTemperature,
                            vm.InletPressure / 1000.0);

                    double molarVolume =
                        _diffusionLookup?
                            .GetByCode(
                                pollutantCode)
                            ?.MolarVolumeAtBoilingPointCm3Mol
                            ?? 0.0;

                    pollutantInputs.Add(
                        new MultiPollutantIterativeSolver
                            .PollutantInput
                        {
                            Code =
                                pollutantCode,

                            InletPpm =
                                pollutant.InletConcentration,

                            MolecularWeight =
                                pollutant.MolecularWeight,

                            MolarVolumeCm3Mol =
                                molarVolume,

                            HenrysLawConstant =
                                effectiveHenry,

                            HeatOfAbsorptionKJKmol =
                                HeatOfAbsorption
                                    .GetByPollutantCode(
                                        pollutantCode),

                            HenrysLawTemperatureCorrectionFn =
                                t =>
                                    GetHenrysLawTemperatureCorrectionFactor(
                                        pollutantCode,
                                        t)
                        });
                }

                if (pollutantInputs.Count == 0)
                    return null;

                double gasFlowM3S =
                    vm.ActualFlowRate /
                    3600.0;

                double gasMassFlowKgS =
                    gasFlowM3S *
                    vm.GasDensity;

                double liquidFlowM3S =
                    vm.LiquidToGasRatio *
                    gasFlowM3S /
                    1000.0;

                double liquidMassFlowKgS =
                    liquidFlowM3S *
                    vm.LiquidDensity;

                var packingData =
                    _packingLookup?
                        .GetByCode(
                            vm.PackingCode);

                double packingSpecificAreaM2M3 =
                    packingData?
                        .SpecificAreaM2M3
                    ?? DefaultSurfaceArea;

                double packingNominalSizeM =
                    packingData?
                        .NominalSizeM
                    ?? DefaultNominalPackingSizeM;

                var solverInput =
                    new MultiPollutantIterativeSolver
                        .SolverInput
                    {
                        Pollutants =
                            pollutantInputs,

                        GasTemperatureC =
                            vm.InletTemperature,

                        GasMassFlowKgS =
                            gasMassFlowKgS,

                        LiquidInletTempC =
                            vm.LiquidTemperature,

                        LiquidMassFlowKgS =
                            liquidMassFlowKgS,

                        LiquidDensityKgM3 =
                            vm.LiquidDensity,

                        GasDensityKgM3 =
                            vm.GasDensity,

                        TowerHeightM =
                            towerHeightM,

                        TowerAreaM2 =
                            crossSectionM2,

                        PackingSpecificAreaM2M3 =
                            packingSpecificAreaM2M3,

                        PackingNominalSizeM =
                            packingNominalSizeM,

                        LiquidViscosityPas =
                            vm.LiquidViscosity /
                            1000.0,

                        GasViscosityPas =
                            vm.GasViscosity
                    };

                var (
                    iterReagent,
                    iterReagentEq) =
                    GetReagentSpec(vm);

                solverInput.Reagent =
                    iterReagent;

                solverInput.ReagentEqPerL =
                    iterReagentEq;

                return
                    MultiPollutantIterativeSolver
                        .SolveIterative(
                            solverInput,
                            numSegments: 5);
            }
            catch
            {
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 4b — Multi-pollutant RK45 ODE solver
        // ════════════════════════════════════════════════════════════
        private MultiPollutantOdeSolver.SolverOutput?
            TryComputeMultiPollutantOdeSolution(
                CreateDesignViewModel vm,
                double henrysLawConstantReference,
                double towerHeightM,
                double crossSectionM2)
        {
            if (vm.Pollutants.Count == 0)
                return null;

            try
            {
                var pollutantInputs =
                    new List<
                        MultiPollutantIterativeSolver
                            .PollutantInput>();

                foreach (var pollutant
                    in vm.Pollutants)
                {
                    string? pollutantCode =
                        _componentLookup?
                            .GetByPollutantId(
                                pollutant.PollutantType)
                            ?.Code;

                    if (pollutantCode == null)
                        continue;

                    double effectiveHenry =
                        GetEffectiveHenrysLawConstant(
                            pollutant,
                            vm.LiquidTemperature,
                            vm.InletPressure / 1000.0);

                    double molarVolume =
                        _diffusionLookup?
                            .GetByCode(
                                pollutantCode)
                            ?.MolarVolumeAtBoilingPointCm3Mol
                            ?? 0.0;

                    pollutantInputs.Add(
                        new MultiPollutantIterativeSolver
                            .PollutantInput
                        {
                            Code =
                                pollutantCode,

                            InletPpm =
                                pollutant.InletConcentration,

                            MolecularWeight =
                                pollutant.MolecularWeight,

                            MolarVolumeCm3Mol =
                                molarVolume,

                            HenrysLawConstant =
                                effectiveHenry,

                            HeatOfAbsorptionKJKmol =
                                HeatOfAbsorption
                                    .GetByPollutantCode(
                                        pollutantCode),

                            HenrysLawTemperatureCorrectionFn =
                                t =>
                                    GetHenrysLawTemperatureCorrectionFactor(
                                        pollutantCode,
                                        t)
                        });
                }

                if (pollutantInputs.Count == 0)
                    return null;

                double gasFlowM3S =
                    vm.ActualFlowRate /
                    3600.0;

                double gasMassFlowKgS =
                    gasFlowM3S *
                    vm.GasDensity;

                double liquidFlowM3S =
                    vm.LiquidToGasRatio *
                    gasFlowM3S /
                    1000.0;

                double liquidMassFlowKgS =
                    liquidFlowM3S *
                    vm.LiquidDensity;

                var packingData =
                    _packingLookup?
                        .GetByCode(
                            vm.PackingCode);

                double packingSpecificAreaM2M3 =
                    packingData?
                        .SpecificAreaM2M3
                    ?? DefaultSurfaceArea;

                double packingNominalSizeM =
                    packingData?
                        .NominalSizeM
                    ?? DefaultNominalPackingSizeM;

                var odeInput =
                    new MultiPollutantOdeSolver
                        .SolverInput
                    {
                        Pollutants =
                            pollutantInputs,

                        GasTemperatureC =
                            vm.InletTemperature,

                        GasMassFlowKgS =
                            gasMassFlowKgS,

                        LiquidInletTempC =
                            vm.LiquidTemperature,

                        LiquidMassFlowKgS =
                            liquidMassFlowKgS,

                        LiquidDensityKgM3 =
                            vm.LiquidDensity,

                        GasDensityKgM3 =
                            vm.GasDensity,

                        TowerHeightM =
                            towerHeightM,

                        TowerAreaM2 =
                            crossSectionM2,

                        PackingSpecificAreaM2M3 =
                            packingSpecificAreaM2M3,

                        PackingNominalSizeM =
                            packingNominalSizeM
                    };

                return
                    MultiPollutantOdeSolver
                        .SolveOde(
                            odeInput);
            }
            catch
            {
                return null;
            }
        }

        public double CalculateMinimumLiquidGasRatio(
            double inletPpm,
            double outletPpm,
            double henrysLawConstant,
            double inletLiquidPpm = 0)
        {
            double y1 =
                inletPpm / 1e6;

            double y2 =
                outletPpm / 1e6;

            double x2 =
                inletLiquidPpm / 1e6;

            double x1star =
                y1 /
                Math.Max(
                    henrysLawConstant,
                    0.001);

            double lgMin =
                (y1 - y2) /
                Math.Max(
                    x1star - x2,
                    1e-9);

            return Math.Max(
                lgMin,
                1e-6);
        }

        // Volumetric L/G (L liquid per m3 actual gas)
        // -> molar L/G (mol liquid / mol gas)
        public double CalculateMolarLiquidToGasRatio(
            double litersPerM3Gas,
            double liquidDensityKgM3,
            double gasTemperatureC,
            double gasPressurePa)
        {
            const double MwWater =
                18.015;

            const double RGas =
                8314.462;

            double tempK =
                gasTemperatureC +
                273.15;

            double liquidKmolPerM3Gas =
                (litersPerM3Gas / 1000.0) *
                liquidDensityKgM3 /
                MwWater;

            double gasKmolPerM3 =
                gasPressurePa /
                (RGas * tempK);

            return
                liquidKmolPerM3Gas /
                Math.Max(
                    gasKmolPerM3,
                    1e-12);
        }

        // Molar L/G (mol/mol)
        // -> volumetric L/G (L per m3 actual gas)
        public double MolarToVolumetricLiquidToGasRatio(
            double molarLG,
            double liquidDensityKgM3,
            double gasTemperatureC,
            double gasPressurePa)
        {
            const double MwWater =
                18.015;

            const double RGas =
                8314.462;

            double tempK =
                gasTemperatureC +
                273.15;

            double gasKmolPerM3 =
                gasPressurePa /
                (RGas * tempK);

            double liquidM3PerM3Gas =
                molarLG *
                gasKmolPerM3 *
                MwWater /
                Math.Max(
                    liquidDensityKgM3,
                    1.0);

            return
                liquidM3PerM3Gas *
                1000.0;
        }

        // ════════════════════════════════════════════════════════════
        //  3. PRESSURE DROP  (Billet-Schultes 1999)
        // ════════════════════════════════════════════════════════════
        public double CalculatePressureDrop(
            double gasVelocityMs,
            double liquidLoadingM3M2Hr,
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double packingSurfaceAreaM2M3,
            double voidFraction,
            double liquidViscosityPas)
        {
            double epsilon = voidFraction;
            double ap = packingSurfaceAreaM2M3;
            double uG = gasVelocityMs;

            double dryDP =
                0.764 *
                (1 - epsilon) /
                Math.Pow(epsilon, 3) *
                ap *
                gasDensityKgM3 *
                Math.Pow(uG, 2) /
                2.0;

            double uL =
                liquidLoadingM3M2Hr /
                3600.0;

            double hL =
                Math.Pow(
                    12.0 *
                    liquidViscosityPas *
                    uL *
                    ap /
                    (
                        liquidDensityKgM3 *
                        GravityAccel
                    ),
                    1.0 / 3.0);

            hL =
                Math.Min(
                    hL,
                    0.5 * epsilon);

            double epsWet =
                epsilon - hL;

            double wetFact =
                Math.Pow(
                    epsilon /
                    Math.Max(
                        epsWet,
                        0.01),
                    3.0);

            return dryDP * wetFact;
        }

        // ════════════════════════════════════════════════════════════
        //  4. VENTURI SIZING  (Calvert correlation)
        // ════════════════════════════════════════════════════════════
        public VenturiSizingResult CalculateVenturiSizing(
            double gasFlowRateM3S,
            double throatVelocityMs,
            double liquidToGasRatioLM3,
            double gasDensityKgM3,
            double particleDensityKgM3,
            double particleDiameterMicron,
            double liquidDensityKgM3 = 1000,
            double gasViscosityPas = 1.81e-5)
        {
            double throatArea =
                gasFlowRateM3S /
                throatVelocityMs;

            double throatDiam =
                Math.Sqrt(
                    4.0 *
                    throatArea /
                    Math.PI);

            double dp =
                gasDensityKgM3 *
                Math.Pow(
                    throatVelocityMs,
                    2) /
                2.0;

            double pressureDrop =
                dp *
                (
                    1 +
                    (
                        liquidToGasRatioLM3 /
                        1000.0
                    ) *
                    (
                        liquidDensityKgM3 /
                        gasDensityKgM3
                    )
                );

            double gasVisc =
                gasViscosityPas;

            double dpMeters =
                particleDiameterMicron *
                1e-6;

            double Stk =
                (
                    particleDensityKgM3 *
                    Math.Pow(
                        dpMeters,
                        2) *
                    throatVelocityMs
                ) /
                (
                    18.0 *
                    gasVisc *
                    Math.Max(
                        throatDiam,
                        0.001)
                );

            double collEff =
                (
                    1.0 -
                    Math.Exp(
                        -0.7 *
                        Stk *
                        liquidToGasRatioLM3)
                ) *
                100.0;

            return new VenturiSizingResult
            {
                ThroatDiameter =
                    throatDiam,

                ThroatArea =
                    throatArea,

                ThroatVelocity =
                    throatVelocityMs,

                PressureDrop =
                    pressureDrop,

                CollectionEfficiency =
                    Math.Min(
                        collEff,
                        99.9)
            };
        }

        // ════════════════════════════════════════════════════════════
        //  5. HENRY'S LAW  (temperature corrected)
        // ════════════════════════════════════════════════════════════
        public double GetHenrysLawConstant(
            double H25,
            double tempCoeff,
            double temperatureC)
        {
            return
                _henrysLawCalculator
                    .GetTemperatureCorrectedHenrysConstant(
                        referenceHenrysConstantAt25C:
                            H25,
                        heatOfSolutionKJmol:
                            null,
                        temperatureC:
                            temperatureC,
                        fallbackTempCoeffK:
                            tempCoeff);
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 1 — effective Henry's Law
        // ════════════════════════════════════════════════════════════
        private double GetEffectiveHenrysLawConstant(
            PollutantInputViewModel pollutant,
            double liquidTemperatureC,
            double pressureKPa = 101.325)
        {
            string? pollutantCode =
                _componentLookup?
                    .GetByPollutantId(
                        pollutant.PollutantType)
                    ?.Code;

            double tempCoeff =
                GetVanTHoffTempCoeff(
                    pollutantCode,
                    defaultTempCoeff: 2000);

            double H_T =
                GetHenrysLawConstant(
                    pollutant.HenrysLawConstant,
                    tempCoeff,
                    liquidTemperatureC);

            double gamma =
                GetSoluteActivityCoefficient(
                    pollutantCode,
                    pollutant.InletConcentration);

            return
                HenrysConstantUnits
                    .CgOverClToMoleFractionRatio(
                        H_T * gamma,
                        liquidTemperatureC +
                            273.15,
                        pressureKPa);
        }

        private double GetVanTHoffTempCoeff(
            string? pollutantCode,
            double defaultTempCoeff)
        {
            if (_henrysLawLookup == null ||
                pollutantCode == null)
            {
                return defaultTempCoeff;
            }

            try
            {
                var data =
                    _henrysLawLookup
                        .GetByPollutantCode(
                            pollutantCode);

                if (data?.HeatOfSolutionKJmol == null)
                    return defaultTempCoeff;

                return
                    -(
                        data
                            .HeatOfSolutionKJmol
                            .Value *
                        1000.0
                    ) /
                    GasConstant;
            }
            catch
            {
                return defaultTempCoeff;
            }
        }

        private double GetSoluteActivityCoefficient(
            string? pollutantCode,
            double inletConcentrationPpm)
        {
            if (_activityModel == null ||
                _nrtlLookup == null ||
                pollutantCode == null)
            {
                return 1.0;
            }

            try
            {
                double xSolute =
                    Math.Min(
                        Math.Max(
                            inletConcentrationPpm,
                            0.0) /
                        1_000_000.0,
                        0.05);

                bool built =
                    LiquidActivityBuilder
                        .TryBuildWaterSoluteBinary(
                            pollutantCode,
                            xSolute,
                            _nrtlLookup,
                            out var water,
                            out var solute,
                            out var binary);

                if (!built)
                    return 1.0;

                var result =
                    _activityModel.Evaluate(
                        water,
                        solute,
                        binary);

                return result.GammaB;
            }
            catch
            {
                return 1.0;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  6. POWER SIZING
        // ════════════════════════════════════════════════════════════
        public double CalculateFanPower(
            double flowRateM3S,
            double pressureDropPa,
            double efficiency = 0.65)
            =>
                (
                    flowRateM3S *
                    pressureDropPa
                ) /
                (
                    efficiency *
                    1000
                );

        public double CalculatePumpPower(
            double flowRateM3Hr,
            double pumpHeadM,
            double liquidDensity = 1000,
            double efficiency = 0.70)
        {
            double flowM3S =
                flowRateM3Hr /
                3600.0;

            return
                (
                    flowM3S *
                    liquidDensity *
                    GravityAccel *
                    pumpHeadM
                ) /
                (
                    efficiency *
                    1000
                );
        }

        // ════════════════════════════════════════════════════════════
        //  7. SENSITIVITY ANALYSIS
        // ════════════════════════════════════════════════════════════
        public List<SensitivityPoint>
            RunLGRatioSensitivity(
                double baseInletPpm,
                double henrysConstant,
                double baseNTU,
                double htu,
                double targetOutletPpm = 0,
                double liquidDensityKgM3 = 1000,
                double gasTemperatureC = 25,
                double gasPressurePa = 101325)
        {
            var results =
                new List<SensitivityPoint>();

            double h =
                Math.Max(
                    henrysConstant,
                    0.001);

            double y1 =
                Math.Max(
                    baseInletPpm,
                    0.001);

            double y2 =
                targetOutletPpm > 0
                    ? targetOutletPpm
                    : y1 * 0.05;

            double ratio =
                Math.Max(
                    y1 / y2,
                    1.0001);

            double lgMinMolar =
                CalculateMinimumLiquidGasRatio(
                    y1,
                    y2,
                    h);

            for (
                double m = 1.2;
                m <= 3.0001;
                m += 0.2)
            {
                double lgMolar =
                    lgMinMolar * m;

                double A =
                    lgMolar / h;

                double ntu =
                    baseNTU;

                if (Math.Abs(A - 1.0) >= 0.01)
                {
                    double term =
                        ratio *
                        (
                            1.0 -
                            1.0 / A
                        ) +
                        1.0 / A;

                    ntu =
                        (
                            A /
                            (A - 1.0)
                        ) *
                        Math.Log(
                            Math.Max(
                                term,
                                0.0001));

                    ntu =
                        Math.Max(
                            ntu,
                            0.5);
                }
                else
                {
                    ntu =
                        Math.Max(
                            ratio - 1.0,
                            0.5);
                }

                double eff =
                    PackedTowerEfficiencyCalculator
                        .AtHeight(
                            ntu * htu,
                            htu,
                            A);

                double lgVol =
                    MolarToVolumetricLiquidToGasRatio(
                        lgMolar,
                        liquidDensityKgM3,
                        gasTemperatureC,
                        gasPressurePa);

                results.Add(
                    new SensitivityPoint
                    {
                        ParameterValue =
                            Math.Round(
                                lgVol,
                                2),

                        RemovalEfficiency =
                            Math.Round(
                                eff,
                                1),

                        PackingHeight =
                            Math.Round(
                                ntu * htu,
                                2),

                        Label =
                            $"L/G = {lgVol:F2}"
                    });
            }

            return results;
        }

        private double SizePollutantSimultaneousHeight(
            List<PollutantInputViewModel> pollutants,
            CreateDesignViewModel vm)
        {
            double packingHeight = 5.0;
            int layerCount = 20;

            double gasMolarFlux =
                vm.NormalFlowRate /
                3600.0 *
                vm.GasDensity /
                28.97;

            double liquidMolarFlux =
                vm.LiquidToGasRatio *
                gasMolarFlux;

            double tempK =
                vm.InletTemperature +
                273.15;

            double y =
                pollutants[0]
                    .InletConcentration /
                1000000.0;

            double T =
                tempK;

            double dz =
                packingHeight /
                layerCount;

            for (int i = 0;
                 i < layerCount;
                 i++)
            {
                double kGa =
                    GetEffectiveFilmCoefficients(
                        pollutants[0],
                        vm,
                        1.0,
                        1.0,
                        T)
                    .GasFilmCoeff;

                double H =
                    GetEffectiveHenrysLawConstant(
                        pollutants[0],
                        T - 273.15,
                        vm.InletPressure / 1000.0);

                double yStar =
                    H * 0.001;

                double dy =
                    -(
                        kGa *
                        vm.InletPressure /
                        101.325 *
                        (y - yStar) /
                        Math.Max(
                            gasMolarFlux,
                            0.001)
                    ) *
                    dz;

                y =
                    Math.Max(
                        y + dy,
                        0);

                T =
                    Math.Min(
                        T + 0.1,
                        373.15);
            }

            return packingHeight;
        }

        private (
            double GasFilmCoeff,
            double LiquidFilmCoeff,
            bool PhysicallyDerived)
            GetEffectiveFilmCoefficients(
                PollutantInputViewModel pollutant,
                CreateDesignViewModel vm,
                double gasMassVelocity,
                double liquidMassVelocity,
                double temperatureK)
        {
            return (
                0.1,
                0.01,
                false);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  RESULT DTOs
    // ════════════════════════════════════════════════════════════════
    public class CalculationResult
    {
        public string ScrubberType { get; set; } =
            string.Empty;

        // Geometry
        public double TowerDiameter { get; set; }
        public double TowerHeight { get; set; }
        public double PackingHeight { get; set; }

        // Performance
        public double RemovalEfficiency { get; set; }
        public double PressureDrop { get; set; }
        public double GasVelocity { get; set; }

        // Flooding
        public double PercentFlood { get; set; }
        public double FloodingGasVelocity { get; set; }
        public bool ExceedsRecommendedFlood { get; set; }

        // Transfer unit data
        public double NTU { get; set; }
        public double HTU { get; set; }
        public double AbsorptionFactor { get; set; }

        // Liquid
        public double MinLGRatio { get; set; }
        public double ActualLGRatio { get; set; }
        public double LiquidFlowRateM3Hr { get; set; }

        // Power
        public double FanPowerKW { get; set; }
        public double PumpPowerKW { get; set; }

        public double TotalPowerKW =>
            FanPowerKW +
            PumpPowerKW;

        // Phase 3 — Energy balance
        public double LiquidOutletTemperature
        {
            get;
            set;
        } = 25.0;

        public double HeatAbsorbedKW
        {
            get;
            set;
        } = 0.0;

        // Sensitivity analysis
        public List<SensitivityPoint>
            SensitivityPoints
        {
            get;
            set;
        } = new();

        public double LiquidOutletTemperatureK
        {
            get;
            set;
        }

        public List<PollutantResult>
            PollutantResults
        {
            get;
            set;
        } = new();

        public int PollutantType
        {
            get;
            set;
        }

        public double PackingHeightM
        {
            get;
            set;
        }
    }

    public class NtuHtuResult
    {
        public double NTU { get; set; }
        public double HTU { get; set; }
        public double PackingHeight { get; set; }
        public double AbsorptionFactor { get; set; }
        public double RemovalEfficiency { get; set; }
    }

    public class VenturiSizingResult
    {
        public double ThroatDiameter { get; set; }
        public double ThroatArea { get; set; }
        public double ThroatVelocity { get; set; }
        public double PressureDrop { get; set; }
        public double CollectionEfficiency { get; set; }
    }

    public class SensitivityPoint
    {
        public double ParameterValue { get; set; }
        public double RemovalEfficiency { get; set; }
        public double PackingHeight { get; set; }
        public string Label { get; set; } =
            string.Empty;
    }
}