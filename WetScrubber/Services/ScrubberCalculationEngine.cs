using WetScrubber.Business.Exceptions;
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
        private const double GasConstant = 8.314462;   // J/(mol·K), CODATA
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
        // exception stops the calculation; there is no fixed-coefficient
        // fallback.
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

        // Packing geometry (area, size, voidage, critical surface tension)
        // comes ONLY from the packing lookup. There are no fallback
        // defaults: missing or invalid data raises an error
        // (see RequirePackingData).
        private const double DefaultGasFilmCoeff = 0.03;     // kGa kmol/m³·hr·kPa
        private const double DefaultLiquidFilmCoeff = 0.01;  // kLa m/hr — fallback path only

        // Packing height bounds. Above the recommended height a bed needs
        // redistribution (split into beds); above the hard limit the
        // result is not a credible single-tower design.
        private const double RecommendedMaxPackingHeightM = 10.0;
        private const double HardMaxPackingHeightM = 30.0;

        // Selected packing's data, validated. Throws when the lookup is
        // missing, the packing is unknown, or any value is not usable.
        private PackingData RequirePackingData(string? packingCode)
        {
            if (_packingLookup == null)
                throw new InvalidOperationException(
                    "Packing data requires a packing lookup.");

            var data =
                _packingLookup.GetByCode(packingCode ?? string.Empty);

            if (data == null)
                throw new InvalidOperationException(
                    $"Packing '{packingCode}' was not found in the packing library.");

            if (!IsPositiveFinite(data.SpecificAreaM2M3))
                throw new InvalidOperationException(
                    $"Packing '{packingCode}' has no valid specific area.");

            if (!IsPositiveFinite(data.NominalSizeM))
                throw new InvalidOperationException(
                    $"Packing '{packingCode}' has no valid nominal size.");

            if (!IsPositiveFinite(data.CriticalSurfaceTensionNM))
                throw new InvalidOperationException(
                    $"Packing '{packingCode}' has no valid critical surface tension.");

            if (double.IsNaN(data.VoidageFraction) ||
                data.VoidageFraction <= 0.0 ||
                data.VoidageFraction >= 1.0)
                throw new InvalidOperationException(
                    $"Packing '{packingCode}' has no valid voidage (must be between 0 and 1).");

            return data;
        }

        private static void RequireSolventAndCarrier(CreateDesignViewModel vm)
        {
            if (!IsPositiveFinite(vm.LiquidMolecularWeightGMol))
                throw new InvalidOperationException(
                    "Solvent molecular weight must be positive.");

            if (!IsPositiveFinite(vm.LiquidSurfaceTensionNM))
                throw new InvalidOperationException(
                    "Liquid surface tension must be positive.");

            if (!IsPositiveFinite(vm.CarrierGasMolecularWeightGMol))
                throw new InvalidOperationException(
                    "Carrier gas molecular weight must be positive.");
        }

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

            // Selected packing + solvent/carrier inputs. Missing or invalid
            // data raises an error; there are no fallback defaults.
            var packing = RequirePackingData(vm.PackingCode);
            RequireSolventAndCarrier(vm);

            // 2. Tower diameter
            result.TowerDiameter = CalculateTowerDiameter(
                gasFlowRateNm3Hr: vm.NormalFlowRate,
                gasTemperatureC: vm.InletTemperature,
                gasPressurePa: vm.InletPressure,
                liquidFlowRateM3Hr: liquidFlowM3Hr,
                gasDensityKgM3: vm.GasDensity,
                liquidDensityKgM3: vm.LiquidDensity,
                packingFactor: 0.0, // unused: flooding uses aT and voidage
                liquidViscosityMPas: vm.LiquidViscosity,

                // Phase 1: real-gas density when this engine instance
                // was built with an EOS + lookup and ComponentProperties
                // has this pollutant. Falls back to the original
                // ideal-gas number otherwise.
                pollutantTypeId: pollutant.PollutantType,
                inletConcentrationPpm: pollutant.InletConcentration,

                packingSpecificAreaM2M3:
                    packing.SpecificAreaM2M3,

                voidageFraction:
                    packing.VoidageFraction
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

            // Reactive equilibrium basis: for an irreversible fast reaction
            // (e.g. SO2/HCl/H2S/Cl2 + NaOH, NH3 + acid) with reagent in
            // stoichiometric sufficiency, the equilibrium back-pressure of
            // the free pollutant is ~0, so the NTU / minimum-L/G equilibrium
            // uses a negligible effective H. The PHYSICAL H (henrysTemp)
            // is still used for Onda coefficients and the enhancement factor.
            // Effective target outlet: explicit outlet ppm, else derived from
            // target removal %. There is NO silent default target. When the
            // packing height is fixed (rating mode) the target is optional;
            // otherwise a missing target is reported as a structured result.
            bool ratingMode =
                vm.PackingHeightOverride > 0.0;

            double? resolvedTargetOutletPpm =
                TryResolveTargetOutletPpm(pollutant);

            if (!ratingMode && resolvedTargetOutletPpm == null)
            {
                return BuildInfeasibleResult(
                    result,
                    vm,
                    liquidFlowM3Hr,
                    "TARGET REQUIRED: enter a target outlet concentration or a " +
                    "target removal between 0 and 100 percent, or fix the " +
                    "packing height to rate an existing tower.",
                    0.0,
                    0.0);
            }

            var reactiveBasis =
                GetReactiveEquilibriumBasis(pollutant, vm);

            double henrysEquilibrium =
                reactiveBasis.UseReactiveEquilibrium
                    ? HenrysConstantUnits.MinimumH
                    : henrysTemp;

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
                    vm.InletPressure,
                    vm.LiquidMolecularWeightGMol);

            // Phase 2: Wilke-Chang + Fuller diffusivities feeding
            // the Onda correlation for physically-derived kG/kL/aW.
            //
            // Missing lookup data or invalid properties throw; there is
            // no fixed-coefficient fallback.
            var rateBased =
                ComputeOndaFilmCoefficients(
                    pollutant,
                    vm,
                    gasMassVelocity,
                    liquidMassVelocity,
                    henrysTemp,
                    vm.PackingCode,
                    actualGasDensity);

            double targetOutletPpm;
            double designPackingHeight;
            double htuM;
            double absorptionFactor;
            double achievedRemovalPercent;

            if (ratingMode)
            {
                // RATING MODE: the packing height is fixed by the user.
                // Report the removal this tower actually achieves.
                htuM =
                    rateBased.GasMolarVelocityKmolM2S /
                    rateBased.OverallKGaKmolM3S;

                absorptionFactor =
                    molarLG / henrysEquilibrium;

                if (!IsPositiveFinite(htuM) ||
                    !IsPositiveFinite(absorptionFactor))
                {
                    return BuildInfeasibleResult(
                        result,
                        vm,
                        liquidFlowM3Hr,
                        "RATING FAILED: HTU or absorption factor is not a " +
                        "positive finite number for the supplied inputs.",
                        0.0,
                        0.0);
                }

                designPackingHeight =
                    vm.PackingHeightOverride;

                achievedRemovalPercent =
                    PackedTowerEfficiencyCalculator.AtHeight(
                        designPackingHeight,
                        htuM,
                        absorptionFactor);

                targetOutletPpm =
                    resolvedTargetOutletPpm
                    ?? pollutant.InletConcentration *
                       (1.0 - achievedRemovalPercent / 100.0);

                result.IsRatingMode = true;

                if (resolvedTargetOutletPpm != null)
                {
                    double achievedOutletPpm =
                        pollutant.InletConcentration *
                        (1.0 - achievedRemovalPercent / 100.0);

                    if (achievedOutletPpm >
                        resolvedTargetOutletPpm.Value * 1.005)
                    {
                        result.Warnings.Add(
                            $"RATING: the fixed packing height of " +
                            $"{designPackingHeight:F2} m achieves " +
                            $"{achievedRemovalPercent:F2} % removal " +
                            $"({achievedOutletPpm:G4} ppm), which does not " +
                            $"meet the target outlet of " +
                            $"{resolvedTargetOutletPpm.Value:G4} ppm.");
                    }
                }
            }
            else
            {
                // DESIGN MODE: size the packing height for the target.
                targetOutletPpm =
                    resolvedTargetOutletPpm!.Value;

                var ntuResult =
                    CalculateNtuHtuRateBased(
                        inletConcentrationPpm:
                            pollutant.InletConcentration,

                        outletConcentrationPpm:
                            targetOutletPpm,

                        henrysLawConstant:
                            henrysEquilibrium,

                        liquidToGasRatioMolar:
                            molarLG,

                        gasMolarVelocityKmolM2S:
                            rateBased.GasMolarVelocityKmolM2S,

                        overallKGaKmolM3S:
                            rateBased.OverallKGaKmolM3S);

                if (!ntuResult.IsFeasible)
                {
                    return BuildInfeasibleResult(
                        result,
                        vm,
                        liquidFlowM3Hr,
                        "INFEASIBLE DESIGN: " + ntuResult.InfeasibilityReason,
                        MolarToVolumetricLiquidToGasRatio(
                            ntuResult.MinimumLiquidToGasMolar,
                            vm.LiquidDensity,
                            vm.InletTemperature,
                            vm.InletPressure,
                            vm.LiquidMolecularWeightGMol),
                        ntuResult.MaxAchievableRemovalPercent);
                }

                htuM =
                    ntuResult.HTU;

                absorptionFactor =
                    ntuResult.AbsorptionFactor;

                designPackingHeight =
                    Math.Ceiling(
                        ntuResult.PackingHeight * 100.0) / 100.0;

                achievedRemovalPercent =
                    PackedTowerEfficiencyCalculator.AtHeight(
                        designPackingHeight,
                        htuM,
                        absorptionFactor);
            }

            if (double.IsNaN(designPackingHeight) ||
                double.IsInfinity(designPackingHeight))
            {
                return BuildInfeasibleResult(
                    result,
                    vm,
                    liquidFlowM3Hr,
                    "INFEASIBLE DESIGN: packing height is not finite.",
                    0.0,
                    0.0);
            }

            if (designPackingHeight > HardMaxPackingHeightM)
            {
                return BuildInfeasibleResult(
                    result,
                    vm,
                    liquidFlowM3Hr,
                    $"INFEASIBLE DESIGN: required packing height " +
                    $"{designPackingHeight:F1} m exceeds the " +
                    $"{HardMaxPackingHeightM:F0} m limit. Increase L/G, " +
                    $"use a higher-performance packing, or split into " +
                    $"multiple towers.",
                    0.0,
                    achievedRemovalPercent);
            }

            if (designPackingHeight > RecommendedMaxPackingHeightM)
            {
                result.Warnings.Add(
                    $"HEIGHT: packing height {designPackingHeight:F2} m " +
                    $"exceeds the recommended {RecommendedMaxPackingHeightM:F0} m " +
                    $"per tower; split into beds with liquid redistribution.");
            }

            result.PackingHeight =
                Math.Round(designPackingHeight, 2);

            result.NTU =
                Math.Round(
                    designPackingHeight /
                    Math.Max(htuM, 1e-9),
                    2);

            result.HTU =
                Math.Round(htuM, 2);

            result.AbsorptionFactor =
                Math.Round(
                    absorptionFactor,
                    3);

            result.RemovalEfficiency =
                Math.Round(
                    achievedRemovalPercent,
                    2);

            // 4. Total tower height =
            // packing x (1 + freeboard factor) + sump/top allowance (DesignBasisSettings)
            result.TowerHeight =
                Math.Round(
                    result.PackingHeight * (1.0 + DesignBasisSettings.FreeboardFactor) + DesignBasisSettings.SumpAndTopAllowanceM,
                    2);

            // 5. Gas velocity inside tower
            result.GasVelocity =
                Math.Round(
                    gasFlowM3S / crossSection,
                    2);

            // 6. Hydraulics — the ACTUALLY SELECTED packing's area, size
            // and voidage. Dry-bed dP (Ergun) and flooding (SSH) come from
            // one implementation: PressureDropFloodingCorrelation.
            double pdSurfaceArea =
                packing.SpecificAreaM2M3;

            double pdNominalSizeM =
                packing.NominalSizeM;

            double pdVoidFraction =
                packing.VoidageFraction;

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

            // INDICATIVE wet-bed dP: Ergun dry dP x holdup wetting factor.
            // Not Billet-Schultes; see CalculateIndicativeWetFactor.
            result.PressureDrop =
                Math.Round(
                    floodResult.DryBedPressureDropPaM *
                    CalculateIndicativeWetFactor(
                        liquidLoadingM3M2Hr:
                            liquidFlowM3Hr /
                            crossSection,

                        liquidDensityKgM3:
                            vm.LiquidDensity,

                        packingSurfaceAreaM2M3:
                            pdSurfaceArea,

                        voidFraction:
                            pdVoidFraction,

                        liquidViscosityPas:
                            vm.LiquidViscosity /
                            1000.0) *
                    result.PackingHeight,
                    2);

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
            // Fan is sized on the packing pressure drop plus a fixed allowance
            // for ducting, inlet/outlet, demister and distributor losses.
            result.AuxiliaryPressureDropPa =
                FanSizingSettings.AuxiliaryPressureDropPa;

            result.SystemPressureDrop =
                result.PressureDrop +
                FanSizingSettings.AuxiliaryPressureDropPa;

            result.FanPowerKW =
                Math.Round(
                    CalculateFanPower(
                        gasFlowM3S,
                        result.SystemPressureDrop),
                    2);

            result.PumpPowerKW =
                Math.Round(
                    CalculatePumpPower(
                        liquidFlowM3Hr,
                        result.TowerHeight + DesignBasisSettings.PackedPumpHeadOffsetM,
                        vm.LiquidDensity),
                    2);

            // 8. L/G minimum ratio check
            double minMolarLG =
                CalculateMinimumLiquidGasRatio(
                    pollutant.InletConcentration,
                    targetOutletPpm,
                    henrysEquilibrium);

            result.MinLGRatio =
                reactiveBasis.UseReactiveEquilibrium
                    ? Math.Round(
                        reactiveBasis.MinLiquidToGasLPerM3,
                        3)
                    : Math.Round(
                        MolarToVolumetricLiquidToGasRatio(
                            minMolarLG,
                            vm.LiquidDensity,
                            vm.InletTemperature,
                            vm.InletPressure,
                            vm.LiquidMolecularWeightGMol),
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
                    henrysEquilibrium,
                    designPackingHeight / Math.Max(htuM, 1e-9),
                    htuM,
                    targetOutletPpm,
                    vm.LiquidDensity,
                    vm.InletTemperature,
                    vm.InletPressure,
                    vm.LiquidMolecularWeightGMol);

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
                    venturi.ThroatDiameter * DesignBasisSettings.VenturiDiameterFactor,
                    3);

            result.TowerHeight =
                Math.Round(
                    venturi.ThroatDiameter * DesignBasisSettings.VenturiHeightFactor,
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
                        DesignBasisSettings.VenturiPumpHeadM,
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
        private static void RequirePositiveInput(string name, double value)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new PropertyOutOfBoundsException(
                    name, value, 0.0, double.MaxValue);
        }

        private CalculationResult RunSprayTowerCalc(
            CreateDesignViewModel vm)
        {
            RequirePositiveInput(nameof(vm.ActualFlowRate), vm.ActualFlowRate);
            RequirePositiveInput(nameof(vm.LiquidToGasRatio), vm.LiquidToGasRatio);
            RequirePositiveInput(nameof(vm.GasDensity), vm.GasDensity);
            RequirePositiveInput(nameof(vm.LiquidDensity), vm.LiquidDensity);

            var result =
                new CalculationResult();

            // Design gas velocity from DesignBasisSettings (unverified placeholder)
            double designVelocity = DesignBasisSettings.SprayTowerDesignVelocityMs;

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
                    gasFlowM3S * DesignBasisSettings.SprayTowerHeightPerM3S + DesignBasisSettings.SprayTowerBaseHeightM,
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
                         -DesignBasisSettings.SprayTowerRemovalCoefficient *
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
                        result.PressureDrop + DesignBasisSettings.SprayTowerAuxiliaryPressureDropPa),
                    2);

            result.PumpPowerKW =
                Math.Round(
                    CalculatePumpPower(
                        vm.ActualFlowRate *
                        vm.LiquidToGasRatio /
                        1000.0,
                        DesignBasisSettings.SprayTowerPumpHeadM,
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
            if (!IsPositiveFinite(inletConcentrationPpm))
                throw new ArgumentOutOfRangeException(
                    nameof(inletConcentrationPpm),
                    inletConcentrationPpm,
                    "Inlet concentration must be positive and finite.");

            if (!IsPositiveFinite(outletConcentrationPpm))
                throw new ArgumentOutOfRangeException(
                    nameof(outletConcentrationPpm),
                    outletConcentrationPpm,
                    "Outlet concentration must be positive and finite.");

            if (!IsPositiveFinite(henrysLawConstant))
                throw new ArgumentOutOfRangeException(
                    nameof(henrysLawConstant),
                    henrysLawConstant,
                    "Henry's constant (H_yx) must be positive and finite.");

            if (!IsPositiveFinite(liquidToGasRatioMolar))
                throw new ArgumentOutOfRangeException(
                    nameof(liquidToGasRatioMolar),
                    liquidToGasRatioMolar,
                    "Molar L/G must be positive and finite.");

            if (!IsPositiveFinite(gasMolarVelocityKmolM2S))
                throw new ArgumentOutOfRangeException(
                    nameof(gasMolarVelocityKmolM2S),
                    gasMolarVelocityKmolM2S,
                    "Gas molar velocity must be positive and finite.");

            if (!IsPositiveFinite(overallKGaKmolM3S))
                throw new ArgumentOutOfRangeException(
                    nameof(overallKGaKmolM3S),
                    overallKGaKmolM3S,
                    "Overall KGa must be positive and finite.");

            double y1 = inletConcentrationPpm / 1e6;
            double y2 = outletConcentrationPpm / 1e6;

            double A = liquidToGasRatioMolar / henrysLawConstant;

            double HTU = gasMolarVelocityKmolM2S / overallKGaKmolM3S;

            // No absorption required.
            if (y2 >= y1)
            {
                return new NtuHtuResult
                {
                    NTU = 0.0,
                    HTU = HTU,
                    PackingHeight = 0.0,
                    AbsorptionFactor = A,
                    RemovalEfficiency = 0.0,
                    IsFeasible = true,
                    MinimumLiquidToGasMolar = 0.0,
                    MaxAchievableRemovalPercent = 0.0
                };
            }

            // Minimum L/G for the target removal (x2 = 0, linear
            // equilibrium): A_min = 1 - y2/y1, L/G_min = H * A_min.
            double minimumA = 1.0 - y2 / y1;
            double minimumLiquidToGas = henrysLawConstant * minimumA;

            double invA = 1.0 / A;
            double ratio = y1 / y2;

            // Colburn log argument: (y1/y2)(1 - 1/A) + 1/A
            double logArgument = 1.0 + (ratio - 1.0) * (1.0 - invA);

            if (A < 1.0 && logArgument <= 0.0)
            {
                return new NtuHtuResult
                {
                    NTU = double.PositiveInfinity,
                    HTU = HTU,
                    PackingHeight = double.PositiveInfinity,
                    AbsorptionFactor = A,
                    RemovalEfficiency = A * 100.0,
                    IsFeasible = false,
                    MinimumLiquidToGasMolar = minimumLiquidToGas,
                    MaxAchievableRemovalPercent = A * 100.0,
                    InfeasibilityReason =
                        $"Target removal {(1.0 - y2 / y1) * 100.0:F4}% is not achievable: " +
                        $"absorption factor A = {A:F6} < 1 limits removal to {A * 100.0:F4}% " +
                        $"at any packing height. Minimum molar L/G for the target = " +
                        $"{minimumLiquidToGas:F6}; supplied = {liquidToGasRatioMolar:F6}."
                };
            }

            double NTU;

            if (Math.Abs(A - 1.0) <= 1e-6)
            {
                // Limit of the Colburn expression as A -> 1.
                NTU = ratio - 1.0;
            }
            else
            {
                NTU = Math.Log(logArgument) / (1.0 - invA);
            }

            if (double.IsNaN(NTU) || double.IsInfinity(NTU) || NTU < 0.0)
            {
                return new NtuHtuResult
                {
                    NTU = double.PositiveInfinity,
                    HTU = HTU,
                    PackingHeight = double.PositiveInfinity,
                    AbsorptionFactor = A,
                    RemovalEfficiency = Math.Min(A, 1.0) * 100.0,
                    IsFeasible = false,
                    MinimumLiquidToGasMolar = minimumLiquidToGas,
                    MaxAchievableRemovalPercent = Math.Min(A, 1.0) * 100.0,
                    InfeasibilityReason =
                        "NTU evaluation produced a non-finite or negative value; " +
                        "the target removal is not achievable with the supplied L/G."
                };
            }

            return new NtuHtuResult
            {
                NTU = NTU,
                HTU = HTU,
                PackingHeight = NTU * HTU,
                AbsorptionFactor = A,
                RemovalEfficiency = (1.0 - y2 / y1) * 100.0,
                IsFeasible = true,
                MinimumLiquidToGasMolar = minimumLiquidToGas,
                MaxAchievableRemovalPercent = A >= 1.0 ? 100.0 : A * 100.0
            };
        }

        private static bool IsPositiveFinite(double value) =>
            !double.IsNaN(value) &&
            !double.IsInfinity(value) &&
            value > 0.0;

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
        // Explicit outlet ppm -> else from target removal %. Returns null
        // when neither is supplied; there is no silent default target.
        private static double? TryResolveTargetOutletPpm(
            PollutantInputViewModel pollutant)
        {
            if (IsPositiveFinite(pollutant.TargetOutletConcentration))
                return pollutant.TargetOutletConcentration;

            if (!double.IsNaN(pollutant.TargetRemovalEfficiency) &&
                pollutant.TargetRemovalEfficiency > 0.0 &&
                pollutant.TargetRemovalEfficiency < 100.0)
            {
                return pollutant.InletConcentration *
                       (1.0 - pollutant.TargetRemovalEfficiency / 100.0);
            }

            return null;
        }

        // Structured infeasible / failed result. Replaces the thrown
        // InvalidOperationException so callers can show the reason.
        private static CalculationResult BuildInfeasibleResult(
            CalculationResult result,
            CreateDesignViewModel vm,
            double liquidFlowM3Hr,
            string reason,
            double minLiquidToGasVolumetric,
            double maxAchievableRemovalPercent)
        {
            result.IsFeasible = false;
            result.InfeasibilityReason = reason;
            result.MaxAchievableRemovalPercent =
                Math.Round(
                    Math.Max(0.0, Math.Min(100.0, maxAchievableRemovalPercent)),
                    2);

            result.MinLGRatio =
                IsPositiveFinite(minLiquidToGasVolumetric)
                    ? Math.Round(minLiquidToGasVolumetric, 3)
                    : 0.0;

            result.ActualLGRatio = vm.LiquidToGasRatio;
            result.LiquidFlowRateM3Hr = Math.Round(liquidFlowM3Hr, 2);
            result.ScrubberType = "Packed Tower";
            result.Warnings.Add(reason);

            return result;
        }

        private readonly struct ReactiveEquilibriumBasis
        {
            public ReactiveEquilibriumBasis(
                bool useReactiveEquilibrium,
                double minLiquidToGasLPerM3)
            {
                UseReactiveEquilibrium = useReactiveEquilibrium;
                MinLiquidToGasLPerM3 = minLiquidToGasLPerM3;
            }

            public bool UseReactiveEquilibrium { get; }

            // Stoichiometric minimum: litres of reagent solution per
            // actual m3 of gas needed to neutralise (y_in - y_out).
            public double MinLiquidToGasLPerM3 { get; }
        }

        // Reaction-limited equilibrium applies only when the pollutant /
        // reagent pair has known stoichiometry AND the supplied reagent
        // (L/G x equivalents/L) covers the stoichiometric demand. Otherwise
        // the physical Henry constant is kept (conservative).
        private ReactiveEquilibriumBasis GetReactiveEquilibriumBasis(
            PollutantInputViewModel pollutant,
            CreateDesignViewModel vm)
        {
            string? code =
                _componentLookup?
                    .GetByPollutantId(pollutant.PollutantType)
                    ?.Code;

            var (kind, eqPerL) = GetReagentSpec(vm);

            double nu =
                GetReagentEquivalentsPerMolePollutant(code, kind);

            if (nu <= 0.0 || eqPerL <= 0.0)
                return new ReactiveEquilibriumBasis(false, 0.0);

            double y1 = pollutant.InletConcentration / 1_000_000.0;
            // No target (rating mode) -> size reagent demand on full y1.
            double y2 =
                (TryResolveTargetOutletPpm(pollutant) ?? 0.0) / 1_000_000.0;

            double gasKmolPerM3 =
                vm.InletPressure /
                (8314.462 * (vm.InletTemperature + 273.15));

            // kmol reagent-eq per m3 gas / (kmol-eq per m3 liquid) -> m3 liquid; x1000 -> L
            double minLPerM3 =
                nu *
                Math.Max(y1 - y2, 0.0) *
                gasKmolPerM3 /
                eqPerL *
                1000.0;

            bool sufficient =
                vm.LiquidToGasRatio >= minLPerM3;

            return new ReactiveEquilibriumBasis(
                sufficient,
                minLPerM3);
        }

        // Equivalents of reagent consumed per mole of absorbed pollutant.
        //   Caustic: SO2 + 2NaOH, H2S + 2NaOH, Cl2 + 2NaOH -> 2 ; HCl + NaOH -> 1
        //   Acid:    NH3 + H+ -> 1
        // Returns 0 (unknown) when the pair is not covered, so the
        // enhancement service falls back to its non-instantaneous path.
        private static double GetReagentEquivalentsPerMolePollutant(
            string? pollutantCode,
            ReagentKind reagentKind)
        {
            if (string.IsNullOrWhiteSpace(pollutantCode))
                return 0.0;

            string code = pollutantCode.Trim().ToUpperInvariant();

            if (reagentKind == ReagentKind.Caustic)
            {
                return code switch
                {
                    "SO2" => 2.0,
                    "H2S" => 2.0,
                    "CL2" => 2.0,
                    "HCL" => 1.0,
                    _ => 0.0
                };
            }

            if (reagentKind == ReagentKind.Acid)
            {
                return code switch
                {
                    "NH3" => 1.0,
                    _ => 0.0
                };
            }

            return 0.0;
        }

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
        private RateBasedFilmResult ComputeOndaFilmCoefficients(
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
                throw new InvalidOperationException("Onda inputs require diffusion, component and packing lookups.");
            }

            {
                string? pollutantCode =
                    _componentLookup
                        .GetByPollutantId(
                            pollutant.PollutantType)
                        ?.Code;

                if (pollutantCode == null)
                    throw new InvalidOperationException($"No component code found for pollutant type {pollutant.PollutantType}.");

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
                    throw new InvalidOperationException($"Missing Wilke-Chang/Onda data for {pollutantCode}, H2O or packing {packingCode}.");
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

                // Mass velocities are per unit area, so a unit tower area
                // is used to carry them through the packing input.
                var ondaPacking =
                    new PackingMassTransferInput
                    {
                        SpecificAreaM2M3 =
                            packingData.SpecificAreaM2M3,
                        NominalSizeM =
                            packingData.NominalSizeM,
                        CriticalSurfaceTensionNM =
                            packingData.CriticalSurfaceTensionNM,
                        LiquidSurfaceTensionNM =
                            DefaultLiquidSurfaceTensionNM,
                        TowerAreaM2 = 1.0,
                        GasMassFlowKgS = gasMassVelocity,
                        LiquidMassFlowKgS = liquidMassVelocity
                    };

                var onda =
                    OndaMassTransferCorrelation.Calculate(
                        ondaPacking,
                        new GasPhaseProperties(
                            gasTempK,
                            actualGasDensityKgM3,
                            vm.GasViscosity,
                            dG),
                        new LiquidPhaseProperties(
                            liquidTempK,
                            vm.LiquidDensity,
                            vm.LiquidViscosity / 1000.0,
                            dL,
                            DefaultLiquidSurfaceTensionNM));

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
                    HenrysConstantUnits.MoleFractionRatioToCgOverCl(
                        henrysLawConstant,
                        liquidTempK,
                        pressureKPa);

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
                                liquidTempK,

                            StoichiometricRatio =
                                GetReagentEquivalentsPerMolePollutant(
                                    pollutantCode,
                                    reagentKind)
                        });

                kLaX *=
                    enhancement.Factor;

                if (!(kGaY > 0) || double.IsInfinity(kGaY))
                    throw new PropertyOutOfBoundsException(
                        "GasFilmVolumetricCoefficient",
                        kGaY,
                        0.0,
                        double.MaxValue);

                if (!(kLaX > 0) || double.IsInfinity(kLaX))
                    throw new PropertyOutOfBoundsException(
                        "LiquidFilmVolumetricCoefficient",
                        kLaX,
                        0.0,
                        double.MaxValue);

                double overallKGa =
                    1.0 /
                    (
                        1.0 / kGaY
                        +
                        henrysLawConstant / kLaX
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
        }


        // ════════════════════════════════════════════════════════════
        //  PHASE 4a — Multi-pollutant iterative solver
        // ════════════════════════════════════════════════════════════
        private MultiPollutantIterativeSolver.SolverOutput
            TryComputeMultiPollutantIterativeSolution(
                CreateDesignViewModel vm,
                double henrysLawConstantReference,
                double towerHeightM,
                double crossSectionM2)
        {
            if (vm.Pollutants.Count == 0)
                throw new ArgumentException("At least one pollutant is required.");

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
                        throw new InvalidOperationException($"No component code found for pollutant type {pollutant.PollutantType}.");

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
                    throw new InvalidOperationException("No pollutant had a resolvable component code.");

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
        }

        // ════════════════════════════════════════════════════════════
        //  PHASE 4b — Multi-pollutant RK45 ODE solver
        // ════════════════════════════════════════════════════════════
        private MultiPollutantOdeSolver.SolverOutput
            TryComputeMultiPollutantOdeSolution(
                CreateDesignViewModel vm,
                double henrysLawConstantReference,
                double towerHeightM,
                double crossSectionM2)
        {
            if (vm.Pollutants.Count == 0)
                throw new ArgumentException("At least one pollutant is required.");

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
                        throw new InvalidOperationException($"No component code found for pollutant type {pollutant.PollutantType}.");

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
                    throw new InvalidOperationException("No pollutant had a resolvable component code.");

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
        }

        public double CalculateMinimumLiquidGasRatio(
            double inletPpm,
            double outletPpm,
            double henrysLawConstant,
            double inletLiquidPpm = 0)
        {
            if (!IsPositiveFinite(inletPpm))
                throw new ArgumentOutOfRangeException(
                    nameof(inletPpm), inletPpm,
                    "Inlet concentration must be positive and finite.");

            if (!IsPositiveFinite(outletPpm))
                throw new ArgumentOutOfRangeException(
                    nameof(outletPpm), outletPpm,
                    "Outlet concentration must be positive and finite.");

            if (!IsPositiveFinite(henrysLawConstant))
                throw new ArgumentOutOfRangeException(
                    nameof(henrysLawConstant), henrysLawConstant,
                    "Henry's constant (H_yx) must be positive and finite.");

            double y1 =
                inletPpm / 1e6;

            double y2 =
                outletPpm / 1e6;

            double x2 =
                inletLiquidPpm / 1e6;

            if (y2 >= y1)
                return 0.0;

            double x1star =
                y1 / henrysLawConstant;

            double capacity =
                x1star - x2;

            if (capacity <= 0.0)
                throw new InvalidOperationException(
                    "INFEASIBLE DESIGN: inlet liquid is at or above equilibrium " +
                    "with the inlet gas; no absorption is possible.");

            return (y1 - y2) / capacity;
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
        //  3. WET-BED FACTOR  (INDICATIVE — not Billet-Schultes)
        // ════════════════════════════════════════════════════════════
        // Multiplier applied to the Ergun dry-bed dP:
        //   hL     = (12/g * (muL/rhoL) * uL * aT^2)^(1/3)   [m3/m3]
        //   factor = (eps / (eps - hL))^3
        // hL is the Billet-Schultes-type liquid hold-up below loading
        // point (dimensionally consistent: aT is squared and the
        // kinematic viscosity is used). The factor is a screening
        // estimate only; a full Billet-Schultes dP (psi, Re, Cp, K,
        // packing constants) is not implemented. hL is limited to
        // 0.5*eps so the factor stays finite.
        public double CalculateIndicativeWetFactor(
            double liquidLoadingM3M2Hr,
            double liquidDensityKgM3,
            double packingSurfaceAreaM2M3,
            double voidFraction,
            double liquidViscosityPas)
        {
            if (!IsPositiveFinite(liquidDensityKgM3) ||
                !IsPositiveFinite(packingSurfaceAreaM2M3) ||
                !IsPositiveFinite(liquidViscosityPas) ||
                double.IsNaN(voidFraction) ||
                voidFraction <= 0.0 ||
                voidFraction >= 1.0 ||
                double.IsNaN(liquidLoadingM3M2Hr) ||
                double.IsInfinity(liquidLoadingM3M2Hr) ||
                liquidLoadingM3M2Hr < 0.0)
            {
                throw new ArgumentException(
                    "Wet-bed factor inputs must be positive, finite and physically valid.");
            }

            double epsilon = voidFraction;
            double ap = packingSurfaceAreaM2M3;

            double uL =
                liquidLoadingM3M2Hr /
                3600.0;

            double nuL =
                liquidViscosityPas /
                liquidDensityKgM3;

            double hL =
                Math.Pow(
                    12.0 /
                    GravityAccel *
                    nuL *
                    uL *
                    ap * ap,
                    1.0 / 3.0);

            hL =
                Math.Min(
                    hL,
                    0.5 * epsilon);

            return
                Math.Pow(
                    epsilon /
                    (epsilon - hL),
                    3.0);
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
            RequirePositiveInput(nameof(gasFlowRateM3S), gasFlowRateM3S);
            RequirePositiveInput(nameof(throatVelocityMs), throatVelocityMs);
            RequirePositiveInput(nameof(liquidToGasRatioLM3), liquidToGasRatioLM3);
            RequirePositiveInput(nameof(gasDensityKgM3), gasDensityKgM3);
            RequirePositiveInput(nameof(particleDensityKgM3), particleDensityKgM3);
            RequirePositiveInput(nameof(particleDiameterMicron), particleDiameterMicron);
            RequirePositiveInput(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositiveInput(nameof(gasViscosityPas), gasViscosityPas);

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
                    throatDiam
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
        }

        // ════════════════════════════════════════════════════════════
        //  6. POWER SIZING
        // ════════════════════════════════════════════════════════════
        public double CalculateFanPower(
            double flowRateM3S,
            double pressureDropPa,
            double? efficiency = null)
            =>
                (
                    flowRateM3S *
                    pressureDropPa
                ) /
                (
                    (efficiency ?? FanSizingSettings.Efficiency) *
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

            if (!IsPositiveFinite(henrysConstant))
                throw new ArgumentOutOfRangeException(
                    nameof(henrysConstant), henrysConstant,
                    "Henry's constant (H_yx) must be positive and finite.");

            if (!IsPositiveFinite(baseInletPpm))
                throw new ArgumentOutOfRangeException(
                    nameof(baseInletPpm), baseInletPpm,
                    "Inlet concentration must be positive and finite.");

            double h = henrysConstant;

            double y1 = baseInletPpm;

            double y2 =
                targetOutletPpm > 0
                    ? targetOutletPpm
                    : y1 * 0.05;

            if (y2 >= y1)
                return results;

            double ratio = y1 / y2;

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

                double ntu;

                if (Math.Abs(A - 1.0) <= 1e-6)
                {
                    ntu = ratio - 1.0;
                }
                else
                {
                    double term =
                        ratio *
                        (
                            1.0 -
                            1.0 / A
                        ) +
                        1.0 / A;

                    if (term <= 0.0)
                        continue; // infeasible at this L/G

                    ntu =
                        (
                            A /
                            (A - 1.0)
                        ) *
                        Math.Log(term);
                }

                if (double.IsNaN(ntu) || double.IsInfinity(ntu) || ntu < 0.0)
                    continue;

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

        // Bed split (used by Results.cshtml). Single bed unless set.
        public int NumberOfBeds { get; set; } = 1;

        private double? _bedHeight;
        public double BedHeight
        {
            get => _bedHeight
                   ?? (NumberOfBeds > 0
                        ? PackingHeight / NumberOfBeds
                        : PackingHeight);
            set => _bedHeight = value;
        }

        // Performance
        public double RemovalEfficiency { get; set; }
        public double PressureDrop { get; set; }

        // Packing dP plus auxiliary allowance; the basis for fan power.
        public double SystemPressureDrop { get; set; }
        public double AuxiliaryPressureDropPa { get; set; }

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

        // Feasibility / mode. False means no valid design was produced;
        // the reason and best achievable removal are reported instead.
        public bool IsFeasible { get; set; } = true;
        public string? InfeasibilityReason { get; set; }
        public double MaxAchievableRemovalPercent { get; set; }

        // True when the packing height was fixed by the user and the
        // result is the removal that tower achieves (rating mode).
        public bool IsRatingMode { get; set; }

        public List<string> Warnings { get; set; } = new();
    }

    public class NtuHtuResult
    {
        public double NTU { get; set; }
        public double HTU { get; set; }
        public double PackingHeight { get; set; }
        public double AbsorptionFactor { get; set; }
        public double RemovalEfficiency { get; set; }

        public bool IsFeasible { get; set; } = true;
        public string? InfeasibilityReason { get; set; }
        public double MinimumLiquidToGasMolar { get; set; }
        public double MaxAchievableRemovalPercent { get; set; }
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