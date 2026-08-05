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

        // ── Phase 2 — Onda kGa/kLa from Wilke-Chang + Fuller diffusivity ──
        // Same all-optional, hard-fallback-on-miss contract as the Phase 1
        // fields above. Missing lookup, missing DiffusionProperty data, or
        // any solver exception falls back to DefaultGasFilmCoeff /
        // DefaultLiquidFilmCoeff — see GetEffectiveFilmCoefficients.
        private readonly IDiffusionPropertyLookup? _diffusionLookup;

        public ScrubberCalculationEngine() { }

        public ScrubberCalculationEngine(
            IEquationOfState eos,
            IComponentPropertyLookup componentLookup,
            IHenrysLawLookup? henrysLawLookup = null,
            IActivityCoefficientModel? activityModel = null,
            INrtlBinaryParameterLookup? nrtlLookup = null,
            IDiffusionPropertyLookup? diffusionLookup = null)
        {
            _eos = eos;
            _componentLookup = componentLookup;
            _henrysLawLookup = henrysLawLookup;
            _activityModel = activityModel;
            _nrtlLookup = nrtlLookup;
            _diffusionLookup = diffusionLookup;
        }

        // ── Packing material defaults (Pall Rings 50mm) ───────────
        private const double DefaultPackingFactor = 66.0;    // Fp, 1/m
        private const double DefaultSurfaceArea = 112.0;   // m²/m³
        private const double DefaultVoidFraction = 0.951;   // ε
        private const double DefaultGasFilmCoeff = 0.03;    // kGa kmol/m³·hr·kPa
        private const double DefaultLiquidFilmCoeff = 0.01;    // kLa m/hr

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
            var pollutant = vm.Pollutants.FirstOrDefault() ?? new PollutantInputViewModel();

            // 1. Liquid flow rate from L/G ratio
            double liquidFlowM3Hr = vm.ActualFlowRate * vm.LiquidToGasRatio / 1000.0;

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
                // Phase 1: real-gas density when this engine instance was
                // built with an EOS + lookup (see the two-arg constructor)
                // and ComponentProperties has this pollutant. Falls back
                // to the original ideal-gas number otherwise — see
                // GetActualGasDensity.
                pollutantTypeId: pollutant.PollutantType,
                inletConcentrationPpm: pollutant.InletConcentration
            );

            // 3. NTU / HTU → packing height
            // Phase 1: real per-species Van't Hoff coefficient +
            // NRTL activity correction when data/wiring exists;
            // falls back to the original single hardcoded
            // tempCoeff=2000 / gamma=1 otherwise (see
            // GetEffectiveHenrysLawConstant).
            double henrysTemp = GetEffectiveHenrysLawConstant(pollutant, vm.InletTemperature);
            // Calculate gas flow and cross-sectional area
            double gasFlowM3S = vm.ActualFlowRate / 3600.0;
            double crossSection = Math.PI * Math.Pow(result.TowerDiameter, 2) / 4.0;

            // Gas mass velocity (kg/m²·s)
            double gasMassVelocity = (gasFlowM3S * vm.GasDensity) / crossSection;
            // Liquid mass velocity (kg/m²·s) — Onda's ReL needs this same
            // per-area basis as gasMassVelocity above.
            double liquidMassVelocity = (liquidFlowM3Hr / 3600.0 * vm.LiquidDensity) / crossSection;

            // Phase 2: Onda kGa/kLa from Wilke-Chang + Fuller diffusivity
            // when DiffusionProperty data exists for this pollutant; hard
            // fallback to DefaultGasFilmCoeff/DefaultLiquidFilmCoeff
            // otherwise — see GetEffectiveFilmCoefficients.
            var filmCoeffs = GetEffectiveFilmCoefficients(
                pollutant, vm, gasMassVelocity, liquidMassVelocity, vm.InletTemperature + 273.15);

            var ntuResult = CalculateNtuHtu(
                inletConcentrationPpm: pollutant.InletConcentration,
                outletConcentrationPpm: pollutant.TargetOutletConcentration,
                henrysLawConstant: henrysTemp,
                liquidToGasRatioMolar: vm.LiquidToGasRatio,
                gasFilmCoeff: filmCoeffs.GasFilmCoeff,
                liquidFilmCoeff: filmCoeffs.LiquidFilmCoeff,
                gasMassVelocity: gasMassVelocity,
                gasDensityKgM3: vm.GasDensity,
                physicallyDerivedCoefficients: filmCoeffs.PhysicallyDerived
            );

            result.PackingHeight = Math.Round(ntuResult.PackingHeight, 2);
            result.NTU = Math.Round(ntuResult.NTU, 2);
            result.HTU = Math.Round(ntuResult.HTU, 2);
            result.AbsorptionFactor = Math.Round(ntuResult.AbsorptionFactor, 3);
            result.RemovalEfficiency = Math.Round(ntuResult.RemovalEfficiency, 2);

            // 4. Total tower height = packing + 30% freeboard + 1m sump + 1m top
            result.TowerHeight = Math.Round(result.PackingHeight * 1.3 + 2.0, 2);

            // 5. Gas velocity inside tower
            //double crossSection = Math.PI * Math.Pow(result.TowerDiameter, 2) / 4.0;
            //double gasFlowM3S   = vm.ActualFlowRate / 3600.0;
            result.GasVelocity = Math.Round(gasFlowM3S / crossSection, 2);

            // 6. Pressure drop
            result.PressureDrop = Math.Round(
                CalculatePressureDrop(
                    gasVelocityMs: result.GasVelocity,
                    liquidLoadingM3M2Hr: liquidFlowM3Hr / crossSection,
                    gasDensityKgM3: vm.GasDensity,
                    liquidDensityKgM3: vm.LiquidDensity,
                    packingSurfaceAreaM2M3: DefaultSurfaceArea,
                    voidFraction: DefaultVoidFraction,
                    liquidViscosityPas: vm.LiquidViscosity / 1000.0
                ) * result.PackingHeight, 2);

            // 7. Power
            result.FanPowerKW = Math.Round(CalculateFanPower(gasFlowM3S, result.PressureDrop + 500), 2);
            result.PumpPowerKW = Math.Round(CalculatePumpPower(liquidFlowM3Hr, result.TowerHeight + 5, vm.LiquidDensity), 2);

            // 8. L/G min ratio check
            result.MinLGRatio = Math.Round(
                CalculateMinimumLiquidGasRatio(
                    pollutant.InletConcentration,
                    pollutant.TargetOutletConcentration,
                    henrysTemp), 3);

            result.ActualLGRatio = vm.LiquidToGasRatio;
            result.LiquidFlowRateM3Hr = Math.Round(liquidFlowM3Hr, 2);
            result.ScrubberType = "Packed Tower";

            // 9. Sensitivity analysis for chart
            result.SensitivityPoints = RunLGRatioSensitivity(
                pollutant.InletConcentration,
                pollutant.HenrysLawConstant,
                ntuResult.NTU,
                ntuResult.HTU);

            return result;
        }

        // ════════════════════════════════════════════════════════════
        //  VENTURI SCRUBBER
        // ════════════════════════════════════════════════════════════
        private CalculationResult RunVenturiCalc(CreateDesignViewModel vm)
        {
            var result = new CalculationResult();
            var pollutant = vm.Pollutants.FirstOrDefault() ?? new PollutantInputViewModel();

            double gasFlowM3S = vm.ActualFlowRate / 3600.0;

            var venturi = CalculateVenturiSizing(
                gasFlowRateM3S: gasFlowM3S,
                throatVelocityMs: 80.0,   // 80 m/s typical
                liquidToGasRatioLM3: vm.LiquidToGasRatio,
                gasDensityKgM3: vm.GasDensity,
                particleDensityKgM3: 1500,
                particleDiameterMicron: 5.0,
                liquidDensityKgM3: vm.LiquidDensity
            );

            result.TowerDiameter = Math.Round(venturi.ThroatDiameter * 2.5, 3); // body = 2.5x throat
            result.TowerHeight = Math.Round(venturi.ThroatDiameter * 8, 2);
            result.PackingHeight = 0;
            result.PressureDrop = Math.Round(venturi.PressureDrop, 0);
            result.RemovalEfficiency = Math.Round(venturi.CollectionEfficiency, 2);
            result.GasVelocity = Math.Round(venturi.ThroatVelocity, 1);
            result.FanPowerKW = Math.Round(CalculateFanPower(gasFlowM3S, result.PressureDrop), 2);
            result.PumpPowerKW = Math.Round(CalculatePumpPower(vm.ActualFlowRate * vm.LiquidToGasRatio / 1000.0, 10, vm.LiquidDensity), 2);
            result.LiquidFlowRateM3Hr = Math.Round(vm.ActualFlowRate * vm.LiquidToGasRatio / 1000.0, 2);
            result.ActualLGRatio = vm.LiquidToGasRatio;
            result.ScrubberType = "Venturi Scrubber";
            result.NTU = 0; result.HTU = 0;

            return result;
        }

        // ════════════════════════════════════════════════════════════
        //  SPRAY TOWER
        // ════════════════════════════════════════════════════════════
        private CalculationResult RunSprayTowerCalc(CreateDesignViewModel vm)
        {
            var result = new CalculationResult();

            // Design gas velocity 0.8 m/s for spray tower
            double designVelocity = 0.8;
            double gasFlowM3S = vm.ActualFlowRate / 3600.0;
            double crossSection = gasFlowM3S / designVelocity;
            double diameter = Math.Sqrt(4.0 * crossSection / Math.PI);

            var pollutant = vm.Pollutants.FirstOrDefault() ?? new PollutantInputViewModel();

            result.TowerDiameter = Math.Round(RoundUpDiameter(diameter), 2);
            result.TowerHeight = Math.Round(gasFlowM3S * 5 + 2.0, 2); // simplified
            result.PackingHeight = 0;
            result.GasVelocity = Math.Round(designVelocity, 2);
            result.RemovalEfficiency = Math.Round(
                (1 - Math.Exp(-0.5 * vm.LiquidToGasRatio)) * 100, 2);
            result.PressureDrop = Math.Round(gasFlowM3S * 50, 0);  // low ΔP
            result.FanPowerKW = Math.Round(CalculateFanPower(gasFlowM3S, result.PressureDrop + 300), 2);
            result.PumpPowerKW = Math.Round(CalculatePumpPower(vm.ActualFlowRate * vm.LiquidToGasRatio / 1000.0, 8, vm.LiquidDensity), 2);
            result.LiquidFlowRateM3Hr = Math.Round(vm.ActualFlowRate * vm.LiquidToGasRatio / 1000.0, 2);
            result.ActualLGRatio = vm.LiquidToGasRatio;
            result.ScrubberType = "Spray Tower";
            result.NTU = 0; result.HTU = 0;

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
            double floodingFactor = 0.75,
            int? pollutantTypeId = null,
            double inletConcentrationPpm = 0)
        {
            double tempK = gasTemperatureC + 273.15;
            double gasDensityAct = GetActualGasDensity(
                gasDensityKgM3, tempK, gasPressurePa, pollutantTypeId, inletConcentrationPpm);
            double gasFlowM3S = gasFlowRateNm3Hr * (tempK / 273.15) * (101325.0 / gasPressurePa) / 3600.0;

            double gasFlowKgS = gasFlowM3S * gasDensityAct;
            double liquidFlowKgS = (liquidFlowRateM3Hr / 3600.0) * liquidDensityKgM3;
            double Flv = (liquidFlowKgS / gasFlowKgS) * Math.Sqrt(gasDensityAct / liquidDensityKgM3);

            double logFlv = Math.Log10(Math.Max(Flv, 0.001));
            double logCsf = -1.668 - 1.085 * logFlv - 0.297 * logFlv * logFlv;
            double Csf = Math.Pow(10, logCsf);

            double viscCorr = Math.Pow(Math.Max(liquidViscosityMPas, 0.1) / 1.0, 0.05);
            double Cs = Csf / Math.Sqrt(packingFactor) * viscCorr;
            double uFlood = Cs * Math.Sqrt((liquidDensityKgM3 - gasDensityAct) / gasDensityAct);
            double uOp = uFlood * floodingFactor;

            double area = gasFlowM3S / Math.Max(uOp, 0.01);
            double diam = Math.Sqrt(4.0 * area / Math.PI);

            return Math.Round(RoundUpDiameter(diam), 3);
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 1 — real-gas density via Peng-Robinson, with a hard
        //  fallback to the original ideal-gas correction. This is the
        //  swap-in point: everything upstream (RunPackedTowerCalc,
        //  CalculateTowerDiameter's signature) is unchanged for callers
        //  that don't pass pollutantTypeId — they get the exact same
        //  ideal-gas number as before.
        // ════════════════════════════════════════════════════════════
        private double GetActualGasDensity(
            double gasDensityKgM3StpUserInput,
            double tempK,
            double gasPressurePa,
            int? pollutantTypeId,
            double inletConcentrationPpm)
        {
            double idealGasFallback = gasDensityKgM3StpUserInput * (273.15 / tempK) * (gasPressurePa / 101325.0);

            if (_eos == null || _componentLookup == null || pollutantTypeId == null)
                return idealGasFallback;

            try
            {
                var mixture = GasMixtureBuilder.BuildPollutantInAirMixture(
                    pollutantTypeId: pollutantTypeId.Value,
                    inletConcentrationPpm: inletConcentrationPpm,
                    lookup: _componentLookup);

                var result = _eos.Evaluate(mixture, tempK, gasPressurePa / 1000.0);
                return result.DensityKgM3;
            }
            catch
            {
                // Missing/incomplete ComponentProperty data, solver
                // edge case, whatever — never let a Phase 1 gap break
                // a design calculation that worked yesterday. Falls
                // back to exactly what this method did before Phase 1
                // existed. (A logged warning here would be a good
                // follow-up once ScrubberCalculationEngine has a
                // logger injected — it doesn't today.)
                return idealGasFallback;
            }
        }

        private double RoundUpDiameter(double d)
        {
            double[] std = { 0.3, 0.45, 0.6, 0.75, 0.9, 1.0, 1.2, 1.5, 1.8, 2.0, 2.4, 3.0, 3.6, 4.0, 4.5, 5.0 };
            foreach (var s in std)
                if (s >= d) return s;
            return Math.Ceiling(d / 0.5) * 0.5;
        }

        // ════════════════════════════════════════════════════════════
        //  2. NTU / HTU  (Colburn equation)
        // ════════════════════════════════════════════════════════════
        //public NtuHtuResult CalculateNtuHtu(
        //    double inletConcentrationPpm,
        //    double outletConcentrationPpm,
        //    double henrysLawConstant,
        //    double liquidToGasRatioMolar,
        //    double gasFilmCoeff,
        //    double liquidFilmCoeff)
        //{
        //    double y1 = Math.Max(inletConcentrationPpm, 0.001)  / 1e6;
        //    double y2 = Math.Max(outletConcentrationPpm, 0.0001) / 1e6;
        //    double A  = liquidToGasRatioMolar / Math.Max(henrysLawConstant, 0.001);

        //    double NTU;
        //    if (Math.Abs(A - 1.0) < 0.01)
        //        NTU = 2.0 * (y1 - y2) / (y1 + y2);
        //    else
        //        NTU = (A / (A - 1)) * Math.Log(Math.Max((y1 / y2) * (1 - 1.0 / A) + 1.0 / A, 0.0001));

        //    NTU = Math.Max(NTU, 0.5);

        //    double KGa = 1.0 / (1.0 / Math.Max(gasFilmCoeff, 0.001)
        //                       + henrysLawConstant / Math.Max(liquidFilmCoeff, 0.001));
        //    double HTU          = Math.Max(0.3, 1.0 / Math.Max(KGa, 0.001));

        //    double packingHeight = NTU * HTU;

        //    return new NtuHtuResult
        //    {
        //        NTU              = NTU,
        //        HTU              = HTU,
        //        PackingHeight    = packingHeight,
        //        AbsorptionFactor = A,
        //        RemovalEfficiency = Math.Min((1.0 - y2 / y1) * 100.0, 99.99)
        //    };
        //}
        public NtuHtuResult CalculateNtuHtu(
    double inletConcentrationPpm,
    double outletConcentrationPpm,
    double henrysLawConstant,
    double liquidToGasRatioMolar,
    double gasFilmCoeff,
    double liquidFilmCoeff,
    double gasMassVelocity,     // NEW (kg/m²·s)
    double gasDensityKgM3,      // NEW
    bool physicallyDerivedCoefficients = false // Phase 2: true when gasFilmCoeff/
                                               // liquidFilmCoeff came from Onda,
                                               // not DefaultGasFilmCoeff/DefaultLiquidFilmCoeff
)
        {
            // ─────────────────────────────────────────────
            // 1. Convert concentrations (ppm → mole fraction)
            // ─────────────────────────────────────────────
            double y1 = Math.Max(inletConcentrationPpm, 0.001) / 1e6;
            double y2 = Math.Max(outletConcentrationPpm, 0.0001) / 1e6;

            // ─────────────────────────────────────────────
            // 2. Absorption factor
            // ─────────────────────────────────────────────
            double A = liquidToGasRatioMolar / Math.Max(henrysLawConstant, 0.001);

            // ─────────────────────────────────────────────
            // 3. NTU calculation (correct)
            // ─────────────────────────────────────────────
            double NTU;
            if (Math.Abs(A - 1.0) < 0.01)
            {
                NTU = 2.0 * (y1 - y2) / (y1 + y2);
            }
            else
            {
                double term = (y1 / y2) * (1 - 1.0 / A) + 1.0 / A;
                NTU = (A / (A - 1)) * Math.Log(Math.Max(term, 0.0001));
            }

            NTU = Math.Max(NTU, 0.5);

            // ─────────────────────────────────────────────
            // 4. Overall mass transfer coefficient (Kya)
            // ─────────────────────────────────────────────
            double KGa = 1.0 / (
                1.0 / Math.Max(gasFilmCoeff, 0.001) +
                henrysLawConstant / Math.Max(liquidFilmCoeff, 0.001)
            );

            // ─────────────────────────────────────────────
            // 5. HTU calculation: HTU = G / (Kya * ρg)
            // ─────────────────────────────────────────────
            double Kya;
            double HTU;

            if (physicallyDerivedCoefficients)
            {
                // Phase 2: gasFilmCoeff/liquidFilmCoeff are real kGa/kLa
                // (kmol/m3·hr·kPa, 1/hr) from Onda — no scale correction,
                // no clamp. If HTU comes out unreasonable now, that means
                // the derivation upstream is wrong and needs fixing at
                // the root, not band-aiding here again (see roadmap
                // Phase 2 sequencing note).
                Kya = KGa;
                HTU = gasMassVelocity / Math.Max(Kya * gasDensityKgM3, 1e-6);
            }
            else
            {
                // Legacy path — unchanged for any caller still on
                // DefaultGasFilmCoeff/DefaultLiquidFilmCoeff (no
                // DiffusionProperty data wired for this pollutant yet).
                Kya = KGa * 100;
                HTU = gasMassVelocity / Math.Max(Kya * gasDensityKgM3, 0.001);
                HTU = Math.Min(Math.Max(HTU, 0.5), 2.0);
            }

            // ─────────────────────────────────────────────
            // 6. Packing height
            // ─────────────────────────────────────────────
            double packingHeight = NTU * HTU;

            return new NtuHtuResult
            {
                NTU = NTU,
                HTU = HTU,
                PackingHeight = packingHeight,
                AbsorptionFactor = A,
                RemovalEfficiency = Math.Min((1.0 - y2 / y1) * 100.0, 99.99)
            };
        }
        public double CalculateMinimumLiquidGasRatio(
            double inletPpm,
            double outletPpm,
            double henrysLawConstant,
            double inletLiquidPpm = 0)
        {
            double y1 = inletPpm / 1e6;
            double y2 = outletPpm / 1e6;
            double x2 = inletLiquidPpm / 1e6;
            double x1star = y1 / Math.Max(henrysLawConstant, 0.001);
            double lgMin = (y1 - y2) / Math.Max(x1star - x2, 0.0001);
            return Math.Max(lgMin, 0.1);
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

            double dryDP = 0.764 * (1 - epsilon) / Math.Pow(epsilon, 3)
                         * ap * gasDensityKgM3 * Math.Pow(uG, 2) / 2.0;

            double uL = liquidLoadingM3M2Hr / 3600.0;
            double hL = Math.Pow(12.0 * liquidViscosityPas * uL * ap
                        / (liquidDensityKgM3 * GravityAccel), 1.0 / 3.0);
            hL = Math.Min(hL, 0.5 * epsilon);

            double epsWet = epsilon - hL;
            double wetFact = Math.Pow(epsilon / Math.Max(epsWet, 0.01), 3.0);

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
            double liquidDensityKgM3 = 1000)
        {
            double throatArea = gasFlowRateM3S / throatVelocityMs;
            double throatDiam = Math.Sqrt(4.0 * throatArea / Math.PI);

            double dp = gasDensityKgM3 * Math.Pow(throatVelocityMs, 2) / 2.0;
            double pressureDrop = dp * (1 + (liquidToGasRatioLM3 / 1000.0) * (liquidDensityKgM3 / gasDensityKgM3));

            double gasVisc = 1.81e-5;
            double dpMeters = particleDiameterMicron * 1e-6;
            double Stk = (particleDensityKgM3 * Math.Pow(dpMeters, 2) * throatVelocityMs)
                             / (18.0 * gasVisc * Math.Max(throatDiam, 0.001));
            double collEff = (1.0 - Math.Exp(-0.7 * Stk * liquidToGasRatioLM3)) * 100.0;

            return new VenturiSizingResult
            {
                ThroatDiameter = throatDiam,
                ThroatArea = throatArea,
                ThroatVelocity = throatVelocityMs,
                PressureDrop = pressureDrop,
                CollectionEfficiency = Math.Min(collEff, 99.9)
            };
        }

        // ════════════════════════════════════════════════════════════
        //  5. HENRY'S LAW  (temperature corrected)
        // ════════════════════════════════════════════════════════════
        public double GetHenrysLawConstant(double H25, double tempCoeff, double temperatureC)
        {
            if (H25 <= 0) H25 = 0.83; // default for SO2
            double T = temperatureC + 273.15;
            double H_T = H25 * Math.Exp(tempCoeff * (1.0 / T - 1.0 / 298.15));
            return Math.Max(H_T, 0.001);
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 1 — effective Henry's Law: per-species Van't Hoff
        //  temperature coefficient (replaces the single hardcoded
        //  tempCoeff=2000) combined with an NRTL activity-coefficient
        //  correction (replaces the implicit gamma=1 ideal-solution
        //  assumption). Both corrections fall back independently and
        //  silently to prior behavior when their data isn't available —
        //  same never-break-an-existing-design contract as
        //  GetActualGasDensity.
        // ════════════════════════════════════════════════════════════
        private double GetEffectiveHenrysLawConstant(PollutantInputViewModel pollutant, double gasTemperatureC)
        {
            string? pollutantCode = _componentLookup?.GetByPollutantId(pollutant.PollutantType)?.Code;

            double tempCoeff = GetVanTHoffTempCoeff(pollutantCode, defaultTempCoeff: 2000);
            double H_T = GetHenrysLawConstant(pollutant.HenrysLawConstant, tempCoeff, gasTemperatureC);
            double gamma = GetSoluteActivityCoefficient(pollutantCode, pollutant.InletConcentration);

            // Modified Henry's Law with an activity correction:
            // y* = gamma_solute * H * x. gamma > 1 (positive deviation)
            // makes the pollutant appear less absorbable than the ideal
            // H alone would predict; gamma = 1 (today's default, since
            // NrtlBinaryParameters ships empty) reproduces the exact
            // pre-Phase-1 number.
            return H_T * gamma;
        }

        private double GetVanTHoffTempCoeff(string? pollutantCode, double defaultTempCoeff)
        {
            if (_henrysLawLookup == null || pollutantCode == null)
                return defaultTempCoeff;

            try
            {
                var data = _henrysLawLookup.GetByPollutantCode(pollutantCode);
                if (data?.HeatOfSolutionKJmol == null)
                    return defaultTempCoeff; // HeatOfSolutionKJmol unsourced — see HenrysLawData.cs

                // tempCoeff [K] = -ΔH_soln[J/mol] / R, matching the
                // exp(tempCoeff*(1/T - 1/298.15)) form GetHenrysLawConstant
                // already uses — this just replaces the constant fed
                // into it with a per-species one.
                return -(data.HeatOfSolutionKJmol.Value * 1000.0) / GasConstant;
            }
            catch
            {
                return defaultTempCoeff;
            }
        }

        private double GetSoluteActivityCoefficient(string? pollutantCode, double inletConcentrationPpm)
        {
            if (_activityModel == null || _nrtlLookup == null || pollutantCode == null)
                return 1.0; // ideal solution

            try
            {
                // Rough proxy for liquid-phase solute mole fraction from
                // the gas-phase inlet ppm — a real value needs the
                // liquid mass balance (Phase 3). Capped at a dilute
                // value deliberately: this is the regime scrubbers
                // normally operate in and where NRTL's correction is
                // best-behaved.
                double xSolute = Math.Min(Math.Max(inletConcentrationPpm, 0.0) / 1_000_000.0, 0.05);

                bool built = LiquidActivityBuilder.TryBuildWaterSoluteBinary(
                    pollutantCode, xSolute, _nrtlLookup,
                    out var water, out var solute, out var binary);

                if (!built)
                    return 1.0; // no (Water, pollutant) NRTL pair on file

                var result = _activityModel.Evaluate(water, solute, binary);
                return result.GammaB; // solute's activity coefficient
            }
            catch
            {
                return 1.0;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 2 — Onda kGa/kLa, with a hard fallback to
        //  DefaultGasFilmCoeff/DefaultLiquidFilmCoeff. Mirrors
        //  GetActualGasDensity's contract exactly: any missing lookup,
        //  missing DiffusionProperty row, or solver exception returns
        //  the pre-Phase-2 constants unchanged, PhysicallyDerived=false.
        // ════════════════════════════════════════════════════════════
        private const double WaterMolecularWeight = 18.015; // g/mol

        private (double GasFilmCoeff, double LiquidFilmCoeff, bool PhysicallyDerived)
            GetEffectiveFilmCoefficients(
                PollutantInputViewModel pollutant,
                CreateDesignViewModel vm,
                double gasMassVelocityKgM2S,
                double liquidMassVelocityKgM2S,
                double temperatureK)
        {
            var fallback = (DefaultGasFilmCoeff, DefaultLiquidFilmCoeff, false);

            if (_diffusionLookup == null || _componentLookup == null)
                return fallback;

            try
            {
                var component = _componentLookup.GetByPollutantId(pollutant.PollutantType);
                if (component == null) return fallback;

                var solute = _diffusionLookup.GetByComponentCode(component.Code);
                var solvent = _diffusionLookup.GetByComponentCode(LiquidActivityBuilder.WaterCode);

                double? dLiquidCm2S = WilkeChangDiffusivity.TryCalculate(
                    solute, solvent, WaterMolecularWeight, vm.LiquidViscosity, temperatureK);
                double? dGasCm2S = GasPhaseDiffusivity.TryCalculate(
                    solute, component.MolecularWeight, temperatureK, vm.InletPressure / 1000.0);

                if (dLiquidCm2S == null || dGasCm2S == null) return fallback;

                var onda = OndaMassTransferCorrelation.Calculate(
                    gasMassVelocityKgM2S: gasMassVelocityKgM2S,
                    liquidMassVelocityKgM2S: liquidMassVelocityKgM2S,
                    gasDensityKgM3: vm.GasDensity,
                    liquidDensityKgM3: vm.LiquidDensity,
                    gasViscosityPas: vm.GasViscosity,
                    liquidViscosityPas: vm.LiquidViscosity / 1000.0, // mPa·s -> Pa·s
                    gasDiffusivityM2S: GasPhaseDiffusivity.CentimeterSqPerSecToMeterSqPerSec(dGasCm2S.Value),
                    liquidDiffusivityM2S: WilkeChangDiffusivity.CentimeterSqPerSecToMeterSqPerSec(dLiquidCm2S.Value),
                    packingSurfaceAreaM2M3: DefaultSurfaceArea,
                    voidFraction: DefaultVoidFraction,
                    temperatureK: temperatureK,
                    pressureKPa: vm.InletPressure / 1000.0);

                return (onda.GasFilmCoeff, onda.LiquidFilmCoeff, true);
            }
            catch
            {
                // Same reasoning as GetActualGasDensity's catch: a Phase 2
                // data/solver gap must never break a design that worked
                // before Phase 2 existed.
                return fallback;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  6. POWER SIZING
        // ════════════════════════════════════════════════════════════
        public double CalculateFanPower(double flowRateM3S, double pressureDropPa, double efficiency = 0.65)
            => (flowRateM3S * pressureDropPa) / (efficiency * 1000);

        public double CalculatePumpPower(double flowRateM3Hr, double pumpHeadM,
            double liquidDensity = 1000, double efficiency = 0.70)
        {
            double flowM3S = flowRateM3Hr / 3600.0;
            return (flowM3S * liquidDensity * GravityAccel * pumpHeadM) / (efficiency * 1000);
        }

        // ════════════════════════════════════════════════════════════
        //  7. SENSITIVITY ANALYSIS  (for charts on Results page)
        // ════════════════════════════════════════════════════════════
        public List<SensitivityPoint> RunLGRatioSensitivity(
            double baseInletPpm, double henrysConstant, double baseNTU, double htu)
        {
            var results = new List<SensitivityPoint>();
            double lgMin = CalculateMinimumLiquidGasRatio(baseInletPpm, baseInletPpm * 0.05, henrysConstant);

            for (double m = 1.2; m <= 3.0; m += 0.2)
            {
                double lg = lgMin * m;
                double A = lg / Math.Max(henrysConstant, 0.001);
                double ntu = A < 1.01 ? baseNTU
                           : (A / (A - 1)) * Math.Log(Math.Max(A / (A - 1) * 20, 0.001));
                double eff = Math.Min(100 * (1 - Math.Exp(-ntu / Math.Max(A, 0.001))), 99.9);

                results.Add(new SensitivityPoint
                {
                    ParameterValue = Math.Round(lg, 2),
                    RemovalEfficiency = Math.Round(eff, 1),
                    PackingHeight = Math.Round(ntu * htu, 2),
                    Label = $"L/G = {lg:F2}"
                });
            }
            return results;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  RESULT DTOs
    // ════════════════════════════════════════════════════════════════
    public class CalculationResult
    {
        public string ScrubberType { get; set; } = string.Empty;

        // Geometry
        public double TowerDiameter { get; set; }
        public double TowerHeight { get; set; }
        public double PackingHeight { get; set; }

        // Performance
        public double RemovalEfficiency { get; set; }
        public double PressureDrop { get; set; }   // Pa total
        public double GasVelocity { get; set; }   // m/s

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
        public double TotalPowerKW => FanPowerKW + PumpPowerKW;

        // Sensitivity analysis points for chart
        public List<SensitivityPoint> SensitivityPoints { get; set; } = new();
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
        public string Label { get; set; } = string.Empty;
    }
}