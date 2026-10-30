using WetScrubber.Business.Common;
using WetScrubber.Business.Cyclone;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.MassTransfer;
using WetScrubber.Business.Particulate;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Business.Venturi;
using WetScrubber.Database.Enums;
using WetScrubber.Models;
using WetScrubber.Business.Plate;

namespace WetScrubber.Services
{
    /// <summary>
    /// Core engineering calculation engine for wet scrubber design.
    /// Methods: Tower diameter (Souders-Brown), NTU/HTU (Colburn),
    /// Pressure drop (Ergun dry bed + indicative wet-bed factor; not
    /// Billet-Schultes), Venturi particle-collection screening model,
    /// Spray Tower, Reactive scrubbing (Hatta), Power sizing.
    /// </summary>
    public class ScrubberCalculationEngine
    {
        private const double GasConstant = 8.314462;   // J/(mol·K), CODATA
        private const double GravityAccel = 9.81;    // m/s²

        private readonly IEquationOfState? _eos;
        private readonly IComponentPropertyLookup? _componentLookup;

        private readonly IHenrysLawLookup? _henrysLawLookup;
        private readonly IActivityCoefficientModel? _activityModel;
        private readonly INrtlBinaryParameterLookup? _nrtlLookup;

        private readonly IDiffusionCoefficientLookup? _diffusionLookup;
        private readonly IPackingLookup? _packingLookup;

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

        private const double RecommendedMaxPackingHeightM = 10.0;
        private const double HardMaxPackingHeightM = 30.0;

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

            if (string.IsNullOrWhiteSpace(vm.LiquidSolventCode))
                throw new InvalidOperationException(
                    "Solvent code must be provided.");

            if (string.IsNullOrWhiteSpace(vm.CarrierGasCode))
                throw new InvalidOperationException(
                    "Carrier gas code must be provided.");

        }

        // ════════════════════════════════════════════════════════════
        //  MAIN ENTRY — run full calculation based on scrubber type
        // ════════════════════════════════════════════════════════════
        public CalculationResult RunCalculation(CreateDesignViewModel vm)
        {
            return vm.ScrubberType switch
            {
                ScrubberType.ImpingementPlate => RunImpingementPlateCalc(vm),
                ScrubberType.PackedTower => RunPackedTowerCalc(vm),
                ScrubberType.VenturiScrubber => RunVenturiCalc(vm),
                ScrubberType.SprayTower => RunSprayTowerCalc(vm),
                ScrubberType.WetCyclone => RunWetCycloneCalc(vm),
                _ => RunPackedTowerCalc(vm)
            };
        }

        // ════════════════════════════════════════════════════════════
        //  PACKED TOWER
        // ════════════════════════════════════════════════════════════
        private CalculationResult RunPackedTowerCalc(CreateDesignViewModel vm)
        {
            var result = new CalculationResult();

            var pollutant =
                vm.Pollutants.FirstOrDefault()
                ?? new PollutantInputViewModel();

            // 1. Liquid flow rate from L/G ratio
            double liquidFlowM3Hr =
                vm.ActualFlowRate *
                vm.LiquidToGasRatio /
                1000.0;

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
                pollutantTypeId: pollutant.PollutantType,
                inletConcentrationPpm: pollutant.InletConcentration,
                packingSpecificAreaM2M3:
                    packing.SpecificAreaM2M3,
                voidageFraction:
                    packing.VoidageFraction,
                sshIntercept:
                    vm.FloodingSshIntercept
            );

            // 3. NTU / HTU → packing height
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

            if (reactiveBasis.UseReactiveEquilibrium)
            {
                result.Warnings.Add(
                    "UPPER BOUND: the reagent is treated as instantaneous and irreversible " +
                    "(effective equilibrium slope at its minimum) because no verified rate " +
                    "constant is available. Packing height is optimistic.");
            }

            double gasFlowM3S =
                vm.ActualFlowRate / 3600.0;

            double crossSection =
                Math.PI *
                Math.Pow(result.TowerDiameter, 2) /
                4.0;

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

            var rateBased =
                ComputeOndaFilmCoefficients(
                    pollutant,
                    vm,
                    gasMassVelocity,
                    liquidMassVelocity,
                    henrysTemp,
                    vm.PackingCode,
                    actualGasDensity,
                    henrysEquilibrium);

            if (rateBased.EnhancementIsUpperBound)
            {
                result.Warnings.Add(
                    "UPPER BOUND: the reactive enhancement factor is the instantaneous-reaction " +
                    "limit without a verified rate constant and is optimistic.");
            }

            double targetOutletPpm;
            double designPackingHeight;
            double htuM;
            double absorptionFactor;
            double achievedRemovalPercent;

            if (ratingMode)
            {
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
                            rateBased.OverallKGaKmolM3S,

                        inletLiquidMoleFraction:
                            vm.InletLiquidPollutantMoleFraction);

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

            // 4. Total tower height
            result.TowerHeight =
                Math.Round(
                    result.PackingHeight * (1.0 + DesignBasisSettings.FreeboardFactor) + DesignBasisSettings.SumpAndTopAllowanceM,
                    2);

            // 5. Gas velocity inside tower
            result.GasVelocity =
                Math.Round(
                    gasFlowM3S / crossSection,
                    2);

            // 6. Hydraulics
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
                        1000.0,

                    sshIntercept:
                        vm.FloodingSshIntercept);

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

            // ── Phase 4: Governing-pollutant sizing and independent target checks ──
            if (vm.Pollutants.Count > 1)
            {
                var requirements = new List<(string PollutantCode, double InletPpm, double TargetOutletPpm, double RequiredHeightM, double RequiredNtu, double AchievedOutletPpm)>();
                foreach (var multiPollutant in vm.Pollutants)
                {
                    string? code = _componentLookup?.GetByPollutantId(multiPollutant.PollutantType)?.Code;
                    if (string.IsNullOrWhiteSpace(code))
                        throw new InvalidOperationException($"No component code found for pollutant type {multiPollutant.PollutantType}.");
                    double? target = TryResolveTargetOutletPpm(multiPollutant);
                    if (!ratingMode && !target.HasValue)
                    {
                        result.IsFeasible = false;
                        result.Warnings.Add($"MULTI-POLLUTANT TARGET MISSING for '{code}': provide a target outlet concentration or target removal to include it in governing-pollutant sizing.");
                        continue;
                    }

                    double pollutantActualGasDensity = GetActualGasDensity(
                        vm.GasDensity, vm.InletTemperature + 273.15, vm.InletPressure,
                        multiPollutant.PollutantType, multiPollutant.InletConcentration);
                    double pollutantHenry = GetEffectiveHenrysLawConstant(
                        multiPollutant, vm.LiquidTemperature, vm.InletPressure / 1000.0);
                    var pollutantReactiveBasis = GetReactiveEquilibriumBasis(multiPollutant, vm);
                    double pollutantEquilibrium = pollutantReactiveBasis.UseReactiveEquilibrium
                        ? HenrysConstantUnits.MinimumH : pollutantHenry;
                    double pollutantGasMassVelocity = gasFlowM3S * pollutantActualGasDensity / crossSection;
                    var pollutantRate = ComputeOndaFilmCoefficients(
                        multiPollutant, vm, pollutantGasMassVelocity, liquidMassVelocity,
                        pollutantHenry, vm.PackingCode, pollutantActualGasDensity, pollutantEquilibrium);
                    double pollutantAbsorptionFactor = molarLG / pollutantEquilibrium;
                    double requiredHeight = result.PackingHeight;
                    double requiredNtu = result.NTU;
                    double pollutantHtu = pollutantRate.GasMolarVelocityKmolM2S /
                                          pollutantRate.OverallKGaKmolM3S;
                    double designTarget = target ?? multiPollutant.InletConcentration *
                        (1.0 - PackedTowerEfficiencyCalculator.AtHeight(result.PackingHeight, pollutantHtu, pollutantAbsorptionFactor) / 100.0);
                    if (!ratingMode && target.HasValue && target.Value >= multiPollutant.InletConcentration)
                    {
                        // A target at or above inlet concentration requires no removal.
                        requiredHeight = 0.0;
                        requiredNtu = 0.0;
                    }
                    else
                    if (!ratingMode && target.HasValue)
                    {
                        var pollutantNtu = CalculateNtuHtuRateBased(
                            inletConcentrationPpm: multiPollutant.InletConcentration,
                            outletConcentrationPpm: target.Value,
                            henrysLawConstant: pollutantEquilibrium,
                            liquidToGasRatioMolar: molarLG,
                            gasMolarVelocityKmolM2S: pollutantRate.GasMolarVelocityKmolM2S,
                            overallKGaKmolM3S: pollutantRate.OverallKGaKmolM3S,
                            inletLiquidMoleFraction: vm.InletLiquidPollutantMoleFraction);
                        if (pollutantNtu.IsFeasible)
                        {
                            requiredHeight = Math.Ceiling(pollutantNtu.PackingHeight * 100.0) / 100.0;
                            requiredNtu = pollutantNtu.NTU;
                            pollutantHtu = pollutantNtu.HTU;
                            pollutantAbsorptionFactor = pollutantNtu.AbsorptionFactor;
                        }
                        else
                        {
                            requiredHeight = double.PositiveInfinity;
                            result.IsFeasible = false;
                            result.Warnings.Add($"MULTI-POLLUTANT SIZING FAILED for '{code}': {pollutantNtu.InfeasibilityReason}");
                        }
                    }
                    double achievedOutlet = multiPollutant.InletConcentration *
                        (1.0 - PackedTowerEfficiencyCalculator.AtHeight(result.PackingHeight, pollutantHtu, pollutantAbsorptionFactor) / 100.0);
                    requirements.Add((
                        code,
                        Math.Max(0.0, multiPollutant.InletConcentration),
                        target ?? designTarget,
                        requiredHeight,
                        requiredNtu,
                        Math.Max(0.0, achievedOutlet)));
                }

                if (requirements.Count > 0 && !ratingMode)
                {
                    var governing = requirements.OrderByDescending(x => x.RequiredHeightM).First();
                    if (!double.IsFinite(governing.RequiredHeightM) || governing.RequiredHeightM > HardMaxPackingHeightM)
                    {
                        result.IsFeasible = false;
                        result.InfeasibilityReason = $"Governing pollutant '{governing.PollutantCode}' requires {governing.RequiredHeightM:G4} m packing, exceeding the {HardMaxPackingHeightM:G4} m design limit.";
                        result.Warnings.Add(result.InfeasibilityReason);
                    }
                    else
                    {
                        result.PackingHeight = Math.Max(result.PackingHeight, governing.RequiredHeightM);
                        result.TowerHeight = Math.Round(result.PackingHeight * (1.0 + DesignBasisSettings.FreeboardFactor) + DesignBasisSettings.SumpAndTopAllowanceM, 2);
                        result.NTU = Math.Round(requirements.Max(x => x.RequiredNtu), 2);
                    }
                }

                foreach (var requirement in requirements)
                {
                    var sourcePollutant = vm.Pollutants.First(x =>
                        string.Equals(_componentLookup?.GetByPollutantId(x.PollutantType)?.Code, requirement.PollutantCode, StringComparison.OrdinalIgnoreCase));
                    double? target = TryResolveTargetOutletPpm(sourcePollutant);
                    double h = requirements.First(x => x.PollutantCode == requirement.PollutantCode).RequiredHeightM;
                    if (target.HasValue && result.PackingHeight + 1e-9 < h)
                    {
                        result.IsFeasible = false;
                        result.Warnings.Add($"POLLUTANT TARGET NOT MET for '{requirement.PollutantCode}': required packing height {h:G4} m exceeds selected height {result.PackingHeight:G4} m.");
                    }
                    else if (ratingMode && target.HasValue && requirement.AchievedOutletPpm > target.Value * 1.005 + 1e-12)
                    {
                        result.IsFeasible = false;
                        result.Warnings.Add($"POLLUTANT TARGET NOT MET for '{requirement.PollutantCode}': predicted outlet {requirement.AchievedOutletPpm:G4} ppm exceeds target {target.Value:G4} ppm at the fixed packing height.");
                    }
                }

                result.PackingHeight = Math.Round(result.PackingHeight, 2);
                result.TowerHeight = Math.Round(result.PackingHeight * (1.0 + DesignBasisSettings.FreeboardFactor) + DesignBasisSettings.SumpAndTopAllowanceM, 2);
                result.PumpPowerKW = Math.Round(CalculatePumpPower(liquidFlowM3Hr, result.TowerHeight + DesignBasisSettings.PackedPumpHeadOffsetM, vm.LiquidDensity), 2);
                result.PressureDrop = Math.Round(floodResult.DryBedPressureDropPaM * CalculateIndicativeWetFactor(
                    liquidLoadingM3M2Hr: liquidFlowM3Hr / crossSection,
                    liquidDensityKgM3: vm.LiquidDensity,
                    packingSurfaceAreaM2M3: pdSurfaceArea,
                    voidFraction: pdVoidFraction,
                    liquidViscosityPas: vm.LiquidViscosity / 1000.0) * result.PackingHeight, 2);
                result.SystemPressureDrop = result.PressureDrop + FanSizingSettings.AuxiliaryPressureDropPa;
                result.FanPowerKW = Math.Round(CalculateFanPower(gasFlowM3S, result.SystemPressureDrop), 2);

                var odeResult =
                    TryComputeMultiPollutantOdeSolution(
                        vm,
                        henrysTemp,
                        result.PackingHeight,
                        crossSection);

                if (odeResult?.Converged == true)
                {
                    double totalRemovalOde =
                        ComputeMoleWeightedRemoval(
                            vm,
                            odeResult
                                .OverallRemovalEfficiency);

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
                        result.PackingHeight,
                        crossSection);

                if (multiResult?.Converged == true)
                {
                    double totalRemoval =
                        ComputeMoleWeightedRemoval(
                            vm,
                            multiResult
                                .OverallRemovalEfficiency);

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

            result.AuxiliaryPressureDropPa =
                FanSizingSettings.AuxiliaryPressureDropPa;

            result.SystemPressureDrop =
                result.PressureDrop + result.AuxiliaryPressureDropPa;

            result.FanPowerKW =
                Math.Round(
                    CalculateFanPower(
                        gasFlowM3S,
                        result.SystemPressureDrop),
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
            result.UsesPackingMetrics = false;
            result.UsesFloodingMetrics = false;
            result.HasMinimumLGRatioEstimate = false;

            result.Warnings.Add(
                "VENTURI MODEL: collection efficiency is the Johnstone correlation with Nukiyama-Tanasawa " +
                $"droplet size ({venturi.DropletDiameterMicron:F0} micron) and Cunningham slip correction; " +
                "pressure drop is the Calvert correlation. Verify against vendor data before final design.");

            result.Warnings.Add(
                "VENTURI SCOPE: this is a particle-collection model and does not predict absorption/removal of " +
                "gaseous pollutants.");

            foreach (var modelWarning in venturi.Warnings)
            {
                // Throat-velocity range is reported below as an infeasibility.
                if (!modelWarning.StartsWith("Throat velocity", StringComparison.Ordinal))
                    result.Warnings.Add("VENTURI: " + modelWarning);
            }

            if (venturi.ThroatVelocity < 60.0 || venturi.ThroatVelocity > 120.0)
            {
                result.IsFeasible = false;
                result.InfeasibilityReason =
                    "Venturi throat velocity is outside the indicative 60-120 m/s range.";
                result.Warnings.Add(result.InfeasibilityReason);
            }

            result.NTU = 0;
            result.HTU = 0;

            return result;
        }

        // ════════════════════════════════════════════════════════════
        //  WET CYCLONE  (particulate only; Lapple d50 + Shepherd-Lapple dP)
        // ════════════════════════════════════════════════════════════
        private CalculationResult RunWetCycloneCalc(
            CreateDesignViewModel vm)
        {
            var result = new CalculationResult();

            double gasFlowM3S = vm.ActualFlowRate / 3600.0;

            var input = new WetCycloneInput
            {
                GasFlowM3s = gasFlowM3S,
                TemperatureK = vm.InletTemperature + 273.15,
                PressurePa = vm.InletPressure,
                GasViscosityPas = vm.GasViscosity,
                GasMolarMassKgMol = vm.CarrierGasMolecularWeightGMol / 1000.0,
                GasDensityKgM3 = GetActualGasDensity(
                    vm.GasDensity,
                    vm.InletTemperature + 273.15,
                    vm.InletPressure,
                    null,
                    0.0),

                Distribution = ParticleSizeDistribution.LogNormal(
                    vm.CycloneParticleMmdMicron * 1e-6,
                    vm.CycloneParticleGeometricStdDev,
                    DesignBasisSettings.CycloneSizeBinCount),
                ParticleDensityKgM3 = vm.CycloneParticleDensityKgM3,

                InletVelocityMs = vm.CycloneInletVelocityMs,
                ParallelUnits = vm.CycloneParallelUnits,

                LiquidToGasRatioLM3 = vm.LiquidToGasRatio,
                LiquidInjectionPressurePa = vm.CycloneInjectionPressureKPa * 1000.0,
                PumpEfficiency = DesignBasisSettings.CyclonePumpEfficiency,
                FanEfficiency = DesignBasisSettings.CycloneFanEfficiency,
                WetCorrectionFactor = DesignBasisSettings.CycloneWetCorrectionFactor,

                TargetEfficiency = vm.CycloneTargetEfficiencyPercent > 0
                    ? vm.CycloneTargetEfficiencyPercent / 100.0
                    : (double?)null,
                MaxPressureDropPa = vm.CycloneMaxPressureDropPa > 0
                    ? vm.CycloneMaxPressureDropPa
                    : DesignBasisSettings.CyclonePressureDropCeilingPa,

                VelocityHardMinMs = DesignBasisSettings.CycloneVelocityHardMinMs,
                VelocityWarnMinMs = DesignBasisSettings.CycloneVelocityWarnMinMs,
                VelocityWarnMaxMs = DesignBasisSettings.CycloneVelocityWarnMaxMs,
                VelocityHardMaxMs = DesignBasisSettings.CycloneVelocityHardMaxMs
            };

            var cyclone = WetCycloneCollectionModel.Calculate(input);

            result.ScrubberType = "Wet Cyclone";
            result.UsesPackingMetrics = false;
            result.UsesFloodingMetrics = false;
            result.HasMinimumLGRatioEstimate = false;
            result.ActualLGRatio = vm.LiquidToGasRatio;

            result.NTU = 0;
            result.HTU = 0;
            result.PackingHeight = 0;

            result.ModelValidationStatus = cyclone.ValidationStatus.ToString();
            result.GasAbsorptionNotModeled = cyclone.GasAbsorptionNotModeled;

            foreach (var finding in cyclone.Findings)
            {
                if (finding.Severity == FindingSeverity.Info)
                    result.Warnings.Add("CYCLONE NOTE: " + finding.Message);
                else if (finding.Severity == FindingSeverity.Warning)
                    result.Warnings.Add("CYCLONE: " + finding.Message);
            }

            if (cyclone.Geometry == null)
            {
                result.IsFeasible = false;
                result.InfeasibilityReason = string.Join(" ", cyclone.InfeasibilityReasons);
                return result;
            }

            result.TowerDiameter = Math.Round(cyclone.BodyDiameterM, 3);
            result.TowerHeight = Math.Round(cyclone.TotalHeightM, 2);
            result.PressureDrop = Math.Round(cyclone.PressureDropPa, 0);
            result.RemovalEfficiency = Math.Round(cyclone.OverallEfficiencyPercent, 2);
            result.GasVelocity = Math.Round(cyclone.InletVelocityMs, 1);

            result.AuxiliaryPressureDropPa = FanSizingSettings.AuxiliaryPressureDropPa;
            result.SystemPressureDrop = result.PressureDrop + result.AuxiliaryPressureDropPa;

            result.FanPowerKW = Math.Round(
                CalculateFanPower(
                    gasFlowM3S,
                    result.SystemPressureDrop,
                    DesignBasisSettings.CycloneFanEfficiency),
                2);

            result.PumpPowerKW = Math.Round(cyclone.PumpPowerW / 1000.0, 2);
            result.LiquidFlowRateM3Hr = Math.Round(cyclone.WaterRateM3s * 3600.0, 2);

            result.CycloneCutDiameterMicron = Math.Round(cyclone.CutDiameterM * 1e6, 2);
            result.CycloneParallelUnits = cyclone.ParallelUnits;
            result.CycloneInletAreaM2 = Math.Round(cyclone.InletAreaM2, 4);
            result.CycloneGasOutletDiameterM = Math.Round(cyclone.GasOutletDiameterM, 3);
            result.CycloneEffectiveTurns = Math.Round(cyclone.EffectiveTurns, 2);
            result.CycloneWaterRateLMin = Math.Round(cyclone.WaterRateLMin, 1);

            result.Warnings.Add(
                "CYCLONE MODEL: cut diameter and fractional efficiency are the Lapple correlation, " +
                "pressure drop is Shepherd-Lapple; no wet-wall or spray enhancement is applied. " +
                "Model is not yet validated against benchmarks. Verify against vendor data before final design.");

            result.Warnings.Add(
                "CYCLONE SCOPE: this is a particle-collection model and does not predict absorption/removal of " +
                "gaseous pollutants.");

            if (!cyclone.IsFeasible)
            {
                result.IsFeasible = false;
                result.InfeasibilityReason = string.Join(" ", cyclone.InfeasibilityReasons);
                result.Warnings.Add(result.InfeasibilityReason);
            }

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

        private static CalculationResult BuildSprayInfeasibleResult(
            CalculationResult result,
            CreateDesignViewModel vm,
            string reason)
        {
            result.IsFeasible = false;
            result.InfeasibilityReason = reason;
            result.ActualLGRatio = vm.LiquidToGasRatio;
            result.LiquidFlowRateM3Hr =
                Math.Round(
                    vm.ActualFlowRate * vm.LiquidToGasRatio / 1000.0,
                    2);
            result.ScrubberType = "Spray Tower";
            result.UsesPackingMetrics = false;
            result.UsesFloodingMetrics = false;
            result.HasMinimumLGRatioEstimate = false;
            result.Warnings.Add(reason);

            return result;
        }

        private CalculationResult RunSprayTowerCalc(
            CreateDesignViewModel vm)
        {
            RequirePositiveInput(nameof(vm.ActualFlowRate), vm.ActualFlowRate);
            RequirePositiveInput(nameof(vm.LiquidToGasRatio), vm.LiquidToGasRatio);
            RequirePositiveInput(nameof(vm.GasDensity), vm.GasDensity);
            RequirePositiveInput(nameof(vm.LiquidDensity), vm.LiquidDensity);
            RequirePositiveInput(nameof(vm.GasViscosity), vm.GasViscosity);

            var result = new CalculationResult();
            double gasFlowM3S = vm.ActualFlowRate / 3600.0;
            double designVelocity = DesignBasisSettings.SprayTowerDesignVelocityMs;
            RequirePositiveInput(nameof(designVelocity), designVelocity);
            // Size to the design velocity, then calculate performance using the
            // actual area of the rounded-up installed vessel.
            double sizingAreaM2 = gasFlowM3S / designVelocity;
            double sizingDiameterM = Math.Sqrt(4.0 * sizingAreaM2 / Math.PI);
            double installedDiameterM = RoundUpDiameter(sizingDiameterM);
            double areaM2 = Math.PI * installedDiameterM * installedDiameterM / 4.0;
            double actualGasVelocity = gasFlowM3S / areaM2;
            double liquidFlowM3S = vm.LiquidToGasRatio * gasFlowM3S / 1000.0;
            double liquidFlux = liquidFlowM3S / areaM2;
            const double dropletDiameterM = 0.001;
            const double relativeVelocityMS = 2.0;
            const double towerHeightCapM = 15.0;
            const double lossCoefficient = 1.5;

            var pollutantInputs = vm.Pollutants;
            var densityBasisPollutant = pollutantInputs.FirstOrDefault();
            double actualGasDensity = GetActualGasDensity(
                vm.GasDensity,
                vm.InletTemperature + 273.15,
                vm.InletPressure,
                densityBasisPollutant?.PollutantType,
                densityBasisPollutant?.InletConcentration ?? 0.0);
            if (pollutantInputs.Count == 0)
                return BuildSprayInfeasibleResult(
                    result,
                    vm,
                    "INFEASIBLE DESIGN: at least one pollutant is required to size a spray tower.");

            var transferResults = new List<(PollutantInputViewModel Pollutant, string Code, double Diffusivity, double AbsorptionFactor, SprayTowerTransferResult Transfer, double TargetPpm)>();
            foreach (var pollutant in pollutantInputs)
            {
                string? code = _componentLookup?.GetByPollutantId(pollutant.PollutantType)?.Code;
                if (string.IsNullOrWhiteSpace(code))
                    throw new InvalidOperationException($"No component code found for pollutant type {pollutant.PollutantType}.");

                double diffusivity = FullerGasDiffusivity.Calculate(
                    codeA: code,
                    molecularWeightA: pollutant.MolecularWeight,
                    codeB: vm.CarrierGasCode,
                    molecularWeightB: vm.CarrierGasMolecularWeightGMol,
                    temperatureK: vm.InletTemperature + 273.15,
                    pressureKPa: vm.InletPressure / 1000.0);
                if (GetReactiveEquilibriumBasis(pollutant, vm).UseReactiveEquilibrium)
                {
                    const string reactiveNote =
                        "UPPER BOUND: the reagent is treated as instantaneous and irreversible " +
                        "(effective equilibrium slope at its minimum) because no verified rate " +
                        "constant is available. Spray height is optimistic.";

                    if (!result.Warnings.Contains(reactiveNote))
                        result.Warnings.Add(reactiveNote);
                }
                double effectiveHenry =
                         GetReactiveEquilibriumBasis(pollutant, vm).UseReactiveEquilibrium
                            ? HenrysConstantUnits.MinimumH
                            : GetEffectiveHenrysLawConstant(
                                pollutant, vm.LiquidTemperature, vm.InletPressure / 1000.0);
                double molarLG = CalculateMolarLiquidToGasRatio(
                    vm.LiquidToGasRatio, vm.LiquidDensity, vm.InletTemperature,
                    vm.InletPressure, vm.LiquidMolecularWeightGMol);
                double absorptionFactor = molarLG / effectiveHenry;
                if (!IsPositiveFinite(absorptionFactor))
                    throw new InvalidOperationException($"Invalid spray-tower absorption factor for '{code}'.");

                double? targetPpm = TryResolveTargetOutletPpm(pollutant);
                if (!targetPpm.HasValue || !(pollutant.InletConcentration > 0.0))
                    return BuildSprayInfeasibleResult(
                        result,
                        vm,
                        $"TARGET REQUIRED: enter a target outlet concentration or a target removal " +
                        $"between 0 and 100 percent for '{code}'.");

                double targetFraction = targetPpm.Value / pollutant.InletConcentration;
                if (targetFraction >= 1.0)
                {
                    result.Warnings.Add(
                        $"SPRAY TOWER TARGET for '{code}' is at or above the inlet concentration; no removal is required.");
                    targetFraction = 0.999999;
                }

                SprayTowerTransferResult transfer;
                try
                {
                    transfer = SprayTowerDropletTransfer.Calculate(
                        dropletDiameterM: dropletDiameterM,
                        dropletVsGasRelativeVelocityMs: relativeVelocityMS,
                        gasVelocityMs: actualGasVelocity,
                        liquidVolumetricFluxM3M2S: liquidFlux,
                        gasDensityKgM3: actualGasDensity,
                        liquidDensityKgM3: vm.LiquidDensity,
                        gasViscosityPas: vm.GasViscosity,
                        gasDiffusivityM2S: diffusivity,
                        absorptionFactor: absorptionFactor,
                        targetOutletFraction: targetFraction,
                        towerHeightCapM: towerHeightCapM);
                }
                catch (ArgumentOutOfRangeException ex)
                    when (ex.ParamName == "liquidVolumetricFluxM3M2S")
                {
                    return BuildSprayInfeasibleResult(
                        result,
                        vm,
                        "INFEASIBLE DESIGN: " + ex.Message);
                }

                transferResults.Add((pollutant, code, diffusivity, absorptionFactor, transfer, targetPpm.Value));
            }

            var governingItem = transferResults
                .OrderByDescending(x => x.Transfer.RequiredHeightM)
                .First();

            double governingHeight = governingItem.Transfer.RequiredHeightM;
            if (!double.IsFinite(governingHeight) || governingHeight <= 0.0)
                governingHeight = towerHeightCapM;

            double installedHeight = Math.Min(
                towerHeightCapM,
                Math.Ceiling(governingHeight * 100.0) / 100.0);

            if (transferResults.Count > 1)
            {
                result.Warnings.Add(
                    $"GOVERNING POLLUTANT: spray height is sized on '{governingItem.Code}'; " +
                    "overall removal is concentration-weighted.");
            }

            double achievedMaxNtu = 0.0;
            double inletTotal = transferResults.Sum(x => x.Pollutant.InletConcentration);
            double removedTotal = 0.0;
            foreach (var item in transferResults)
            {
                var achieved = SprayTowerDropletTransfer.Calculate(
                    dropletDiameterM, relativeVelocityMS, actualGasVelocity, liquidFlux,
                    actualGasDensity, vm.LiquidDensity, vm.GasViscosity, item.Diffusivity,
                    item.AbsorptionFactor, 1e-6, installedHeight,
                    evaluateAtTowerHeight: true);
                achievedMaxNtu = Math.Max(achievedMaxNtu, achieved.NTU);
                double outletPpm = item.Pollutant.InletConcentration * achieved.OutletFraction;
                removedTotal += item.Pollutant.InletConcentration * (1.0 - achieved.OutletFraction);
                if (outletPpm > item.TargetPpm * 1.005)
                {
                    result.IsFeasible = false;
                    result.Warnings.Add($"SPRAY TOWER TARGET NOT MET for '{item.Code}': predicted outlet {outletPpm:G4} ppm exceeds target {item.TargetPpm:G4} ppm.");
                }
                if (!item.Transfer.IsFeasible)
                {
                    result.IsFeasible = false;
                    result.Warnings.Add($"SPRAY TOWER HEIGHT LIMIT for '{item.Code}': {item.Transfer.InfeasibilityReason}");
                }
            }
            result.RemovalEfficiency = Math.Round(100.0 * removedTotal / inletTotal, 2);

            result.TowerDiameter = Math.Round(installedDiameterM, 2);
            result.TowerHeight = Math.Round(installedHeight, 2);
            result.PackingHeight = 0.0;
            result.GasVelocity = Math.Round(actualGasVelocity, 2);
            // These are droplet-transfer diagnostics, not packed-bed Colburn
            // NTU/HTU. Keep the legacy packed-bed fields empty for this type.
            result.NTU = 0.0;
            result.HTU = 0.0;
            result.SprayTowerNTU = transferResults.Count > 0 ? Math.Round(achievedMaxNtu, 3) : 0.0;
            result.SprayTowerHTU = transferResults.Count > 0 && result.SprayTowerNTU > 0.0
                ? Math.Round(installedHeight / result.SprayTowerNTU, 3)
                : 0.0;
            result.PressureDrop = Math.Round(0.5 * actualGasDensity * actualGasVelocity * actualGasVelocity * lossCoefficient, 2);
            result.AuxiliaryPressureDropPa = DesignBasisSettings.SprayTowerAuxiliaryPressureDropPa;
            result.SystemPressureDrop = result.PressureDrop + result.AuxiliaryPressureDropPa;
            result.FanPowerKW = Math.Round(CalculateFanPower(gasFlowM3S, result.SystemPressureDrop), 2);
            result.LiquidFlowRateM3Hr = Math.Round(vm.ActualFlowRate * vm.LiquidToGasRatio / 1000.0, 2);
            result.ActualLGRatio = vm.LiquidToGasRatio;
            // Packed-bed flooding and minimum-L/G correlations do not apply to
            // this spray-tower screening model; zero means not calculated.
            result.MinLGRatio = 0.0;
            result.PercentFlood = 0.0;
            result.FloodingGasVelocity = 0.0;
            result.ExceedsRecommendedFlood = false;
            result.UsesPackingMetrics = false;
            result.UsesFloodingMetrics = false;
            result.HasMinimumLGRatioEstimate = false;
            result.PumpPowerKW = Math.Round(CalculatePumpPower(result.LiquidFlowRateM3Hr, DesignBasisSettings.SprayTowerPumpHeadM, vm.LiquidDensity), 2);
            result.ScrubberType = "Spray Tower";
            result.Warnings.Add("SPRAY TOWER SCREENING MODEL: gas-film-controlled droplet absorption uses an assumed 1 mm droplet diameter and 2 m/s gas-droplet relative velocity. Liquid-film resistance, reaction enhancement, droplet-size distribution, coalescence, and entrainment are not modeled; validate inputs and correlations before design.");
            result.Warnings.Add("SPRAY TOWER PRESSURE DROP: uses K=1.5 dynamic pressure plus the configured auxiliary allowance; this is preliminary, not a validated wet-tower correlation.");
            if (installedHeight >= towerHeightCapM)
            {
                result.IsFeasible = false;
                result.InfeasibilityReason = "Required spray-tower height reaches or exceeds the 15 m screening cap.";
                result.Warnings.Add(result.InfeasibilityReason);
            }
            return result;
        }


        private CalculationResult RunImpingementPlateCalc(
            CreateDesignViewModel vm)
        {
            if (vm == null)
                throw new ArgumentNullException(nameof(vm));

            // Resolve gas and liquid flows in m³/hr.
            double gasFlowM3Hr =
                vm.ImpingementPlateGasFlowM3Hr > 0.0
                    ? vm.ImpingementPlateGasFlowM3Hr
                    : vm.ActualFlowRate;

            double liquidFlowM3Hr =
                vm.ImpingementPlateLiquidFlowM3Hr > 0.0
                    ? vm.ImpingementPlateLiquidFlowM3Hr
                    : gasFlowM3Hr * vm.LiquidToGasRatio / 1000.0;

            double gasDensity =
                vm.ImpingementPlateGasDensityKgM3 > 0.0
                    ? vm.ImpingementPlateGasDensityKgM3
                    : vm.GasDensity;

            double liquidDensity =
                vm.ImpingementPlateLiquidDensityKgM3 > 0.0
                    ? vm.ImpingementPlateLiquidDensityKgM3
                    : vm.LiquidDensity;

            double columnDiameterM =
                vm.ImpingementPlateColumnDiameterM;

            int stages = vm.ImpingementPlateStages;

            if (stages <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(vm.ImpingementPlateStages),
                    "Impingement plate stages must be greater than zero.");

            if (!(gasFlowM3Hr > 0.0) ||
                double.IsNaN(gasFlowM3Hr) ||
                double.IsInfinity(gasFlowM3Hr))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(vm.ImpingementPlateGasFlowM3Hr),
                    "Gas flow must be positive and finite.");
            }

            if (!(liquidFlowM3Hr >= 0.0) ||
                double.IsNaN(liquidFlowM3Hr) ||
                double.IsInfinity(liquidFlowM3Hr))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(vm.ImpingementPlateLiquidFlowM3Hr),
                    "Liquid flow must be finite and non-negative.");
            }

            // Convert m³/hr to m³/s for the engineering model.
            double gasFlowM3S = gasFlowM3Hr / 3600.0;
            double liquidFlowM3S = liquidFlowM3Hr / 3600.0;

            // Run the repository's plate hydraulic correlation.
            // Geometry correlation defaults are preliminary screening values.
            PlateHydraulicsResult hydraulics =
                ImpingementPlateHydraulics.Evaluate(
                    new PlateHydraulicsInput
                    {
                        GasFlowM3s = gasFlowM3S,
                        LiquidFlowM3s = liquidFlowM3S,
                        GasDensityKgM3 = gasDensity,
                        LiquidDensityKgM3 = liquidDensity,
                        SurfaceTensionNm = vm.LiquidSurfaceTensionNM,
                        ColumnDiameterM = columnDiameterM
                    });

            double totalPressureDropPa =
                hydraulics.PressureDropPerPlatePa * stages;

            // ── Gas absorption screening (Kremser, equilibrium stages) ──
            // Fraction absorbed = (A^(N+1) - A) / (A^(N+1) - 1), A = (L/G)molar / H.
            // N = plate count x assumed stage efficiency. The efficiency is an
            // UNCONFIRMED screening value; replace with vendor/benchmark data.
            const double plateStageEfficiency = 0.50;
            double effectiveStages = stages * plateStageEfficiency;

            double inletTotalAbs = 0.0;
            double removedTotalAbs = 0.0;
            bool absorptionComputed = false;
            var absorptionNotes = new List<string>();

            if (vm.Pollutants != null &&
                IsPositiveFinite(vm.LiquidMolecularWeightGMol))
            {
                double molarLG =
                    CalculateMolarLiquidToGasRatio(
                        vm.LiquidToGasRatio,
                        liquidDensity,
                        vm.InletTemperature,
                        vm.InletPressure,
                        vm.LiquidMolecularWeightGMol);

                foreach (var pol in vm.Pollutants)
                {
                    if (!(pol.InletConcentration > 0.0))
                        continue;

                    double henry =
                        GetEffectiveHenrysLawConstant(
                            pol,
                            vm.LiquidTemperature,
                            vm.InletPressure / 1000.0);

                    if (!IsPositiveFinite(henry) || !IsPositiveFinite(molarLG))
                        continue;

                    double a = molarLG / henry;
                    double fraction;

                    if (Math.Abs(a - 1.0) < 1e-9)
                    {
                        fraction = effectiveStages / (effectiveStages + 1.0);
                    }
                    else
                    {
                        double aN1 = Math.Pow(a, effectiveStages + 1.0);
                        fraction = (aN1 - a) / (aN1 - 1.0);
                    }

                    if (double.IsNaN(fraction) || double.IsInfinity(fraction))
                        continue;

                    fraction = Math.Max(0.0, Math.Min(1.0, fraction));

                    inletTotalAbs += pol.InletConcentration;
                    removedTotalAbs += pol.InletConcentration * fraction;
                    absorptionComputed = true;

                    absorptionNotes.Add(
                        $"absorption factor A = {a:F2}, effective stages = " +
                        $"{effectiveStages:F1}, removal = {fraction * 100.0:F1}%");
                }
            }

            double absorptionRemovalPercent =
                absorptionComputed && inletTotalAbs > 0.0
                    ? Math.Round(100.0 * removedTotalAbs / inletTotalAbs, 2)
                    : 0.0;

            var result = new CalculationResult
            {
                ScrubberType = "Impingement Plate",

                // Geometry and plate-bank hydraulics.
                TowerDiameter = columnDiameterM,
                PackingHeight = 0.0,
                NumberOfBeds = 1,

                ImpingementPlateStages = stages,
                ImpingementPlateActiveAreaM2 =
                    hydraulics.ActiveAreaM2,
                ImpingementPlateGasVelocityMPerS =
                    hydraulics.NetVelocityMs,
                ImpingementPlateFloodingVelocityMPerS =
                    hydraulics.FloodingVelocityMs,
                ImpingementPlatePercentFlood =
                    hydraulics.PercentFlood,
                ImpingementPlatePressureDropPa =
                    totalPressureDropPa,

                // Generic result fields consumed by the existing results view.
                GasVelocity = Math.Round(hydraulics.HoleVelocityMs, 3),
                PressureDrop = Math.Round(totalPressureDropPa, 2),
                SystemPressureDrop = Math.Round(totalPressureDropPa, 2),
                TowerHeight = Math.Round(stages * 0.45, 2),

                LiquidFlowRateM3Hr = Math.Round(liquidFlowM3Hr, 3),
                ActualLGRatio = gasFlowM3Hr > 0.0
                    ? liquidFlowM3Hr * 1000.0 / gasFlowM3Hr
                    : 0.0,

                FanPowerKW = Math.Round(CalculateFanPower(gasFlowM3S, totalPressureDropPa), 2),
                PumpPowerKW = Math.Round(CalculatePumpPower(liquidFlowM3Hr, DesignBasisSettings.SprayTowerPumpHeadM, liquidDensity), 2),

                PercentFlood = Math.Round(hydraulics.PercentFlood, 2),
                FloodingGasVelocity =
                    Math.Round(hydraulics.FloodingVelocityMs, 3),
                ExceedsRecommendedFlood =
                    hydraulics.ExceedsRecommendedFlood,

                // Packed-bed correlations do not apply to this scrubber.
                UsesPackingMetrics = false,
                UsesFloodingMetrics = true,
                HasMinimumLGRatioEstimate = false,
                NTU = 0.0,
                HTU = 0.0,
                MinLGRatio = 0.0,

                // No plate removal correlation is connected to the supplied
                // particle-size distribution or gas-absorption inputs here.
                RemovalEfficiency = absorptionRemovalPercent,
                ImpingementPlateRemovalEfficiencyPercent = absorptionRemovalPercent,
                ImpingementPlateCutDiameterMicron =
                    vm.ImpingementPlateCutDiameterMicron,

                ModelValidationStatus = "NotYetValidated",
                GasAbsorptionNotModeled = !absorptionComputed,

                IsFeasible =
                    !hydraulics.IsFlooding &&
                    !hydraulics.ExcessiveBackup
            };

            result.Warnings.Add(
                "PRELIMINARY PLATE MODEL: hydraulic correlations and default " +
                "geometry parameters require validation against authoritative " +
                "design data or vendor benchmarks.");

            if (absorptionComputed)
            {
                result.Warnings.Add(
                    "GAS ABSORPTION SCREEN: Kremser equilibrium-stage estimate with an " +
                    $"assumed {plateStageEfficiency * 100.0:F0}% plate stage efficiency " +
                    "(unconfirmed). Liquid-phase reaction, gas-film resistance and " +
                    "entrainment are not modeled; " + string.Join("; ", absorptionNotes) + ".");
            }
            else
            {
                result.Warnings.Add(
                    "REMOVAL EFFICIENCY: gas absorption could not be computed from the " +
                    "supplied inputs, so removal is reported as 0 (not calculated).");
            }

            result.Warnings.Add(
                $"Hydraulic screening: hole velocity = " +
                $"{hydraulics.HoleVelocityMs:F2} m/s; pressure drop per plate = " +
                $"{hydraulics.PressureDropPerPlatePa:F1} Pa; total plate-bank " +
                $"pressure drop = {totalPressureDropPa:F1} Pa.");

            if (hydraulics.IsWeeping)
            {
                result.Warnings.Add(
                    "WEEPING SCREEN: hole F-factor is below the configured " +
                    "screening limit.");
            }

            if (hydraulics.ExceedsRecommendedFlood)
            {
                // Matches the default recommended flooding threshold used by
                // the plate hydraulics screening model.
                const double recommendedFloodPercent = 70.0;
                result.Warnings.Add(
                    $"Flooding screen: estimated flooding is " +
                    $"{hydraulics.PercentFlood:F1}%, above the recommended " +
                    $"{recommendedFloodPercent:F0}% limit.");
            }

            if (hydraulics.IsFlooding)
            {
                result.IsFeasible = false;
                result.InfeasibilityReason =
                    "Estimated plate flooding exceeds 100%.";

                result.Warnings.Add(result.InfeasibilityReason);
            }

            if (hydraulics.ExcessiveBackup)
            {
                result.IsFeasible = false;

                result.InfeasibilityReason =
                    "Estimated liquid backup exceeds the configured " +
                    "fraction of plate spacing.";

                result.Warnings.Add(result.InfeasibilityReason);
            }

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
            double voidageFraction = 0,
            double sshIntercept =
                WetScrubber.Business.MassTransfer
                    .PressureDropFloodingCorrelation.DefaultSshIntercept)
        {
            if (!IsPositiveFinite(packingSpecificAreaM2M3))
                throw new ArgumentOutOfRangeException(
                    nameof(packingSpecificAreaM2M3),
                    packingSpecificAreaM2M3,
                    "Packing specific area must be positive and finite.");

            if (double.IsNaN(voidageFraction) ||
                voidageFraction <= 0.0 ||
                voidageFraction >= 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(voidageFraction),
                    voidageFraction,
                    "Packing voidage must be between 0 and 1.");

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
                packingSpecificAreaM2M3;

            double eps =
                voidageFraction;

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
                            1000.0,
                        sshIntercept: sshIntercept);

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
            double overallKGaKmolM3S,
            double inletLiquidMoleFraction = 0.0)
        {
            if (double.IsNaN(inletLiquidMoleFraction) ||
                double.IsInfinity(inletLiquidMoleFraction) ||
                inletLiquidMoleFraction < 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(inletLiquidMoleFraction),
                    inletLiquidMoleFraction,
                    "Inlet liquid mole fraction must be finite and non-negative.");

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

            // Liquid inlet loading x2: gas in equilibrium with the entering
            // liquid is y2* = H_yx * x2. Colburn is applied to (y - y2*).
            double yEquilibriumAtInletLiquid =
                henrysLawConstant * inletLiquidMoleFraction;

            double y1Effective = y1 - yEquilibriumAtInletLiquid;
            double y2Effective = y2 - yEquilibriumAtInletLiquid;

            if (y1Effective <= 0.0 || y2Effective <= 0.0)
            {
                double maxRemovalPercent =
                    y1Effective > 0.0
                        ? (1.0 - yEquilibriumAtInletLiquid / y1) * 100.0
                        : 0.0;

                return new NtuHtuResult
                {
                    NTU = double.PositiveInfinity,
                    HTU = HTU,
                    PackingHeight = double.PositiveInfinity,
                    AbsorptionFactor = A,
                    RemovalEfficiency = maxRemovalPercent,
                    IsFeasible = false,
                    MinimumLiquidToGasMolar = double.PositiveInfinity,
                    MaxAchievableRemovalPercent = maxRemovalPercent,
                    InfeasibilityReason =
                        "Target outlet is at or below the equilibrium with the entering " +
                        $"liquid (H_yx * x2 = {yEquilibriumAtInletLiquid:G6}); the best " +
                        $"achievable removal is {maxRemovalPercent:F4}% at any packing height."
                };
            }

            double minimumA = 1.0 - y2Effective / y1Effective;
            double minimumLiquidToGas = henrysLawConstant * minimumA;

            double invA = 1.0 / A;
            double ratio = y1Effective / y2Effective;

            double logArgument = 1.0 + (ratio - 1.0) * (1.0 - invA);

            // Best achievable removal at infinite height (reduces to
            // min(A,1)*100 when x2 = 0).
            double maxAchievablePercent =
                A >= 1.0
                    ? (1.0 - yEquilibriumAtInletLiquid / y1) * 100.0
                    : (1.0 - (yEquilibriumAtInletLiquid
                              + y1Effective * (1.0 - A)) / y1) * 100.0;

            if (A < 1.0 && logArgument <= 0.0)
            {
                return new NtuHtuResult
                {
                    NTU = double.PositiveInfinity,
                    HTU = HTU,
                    PackingHeight = double.PositiveInfinity,
                    AbsorptionFactor = A,
                    RemovalEfficiency = maxAchievablePercent,
                    IsFeasible = false,
                    MinimumLiquidToGasMolar = minimumLiquidToGas,
                    MaxAchievableRemovalPercent = maxAchievablePercent,
                    InfeasibilityReason =
                        $"Target removal {(1.0 - y2 / y1) * 100.0:F4}% is not achievable: " +
                        $"absorption factor A = {A:F6} < 1 limits removal to {maxAchievablePercent:F4}% " +
                        $"at any packing height. Minimum molar L/G for the target = " +
                        $"{minimumLiquidToGas:F6}; supplied = {liquidToGasRatioMolar:F6}."
                };
            }

            double NTU;

            if (Math.Abs(A - 1.0) <= 1e-6)
            {
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
                    RemovalEfficiency = maxAchievablePercent,
                    IsFeasible = false,
                    MinimumLiquidToGasMolar = minimumLiquidToGas,
                    MaxAchievableRemovalPercent = maxAchievablePercent,
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
                MaxAchievableRemovalPercent = maxAchievablePercent
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

            public bool EnhancementIsUpperBound { get; set; }
        }

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

        /// <summary>
        /// Henry correction relative to the reference temperature at which the
        /// supplied Henry constant was already evaluated (liquid inlet).
        /// Avoids applying the 25 C based correction on top of an already
        /// temperature-corrected constant.
        /// </summary>
        private double GetHenrysLawRelativeCorrectionFactor(
            string pollutantCode,
            double temperatureC,
            double referenceTemperatureC)
        {
            double atT =
                GetHenrysLawTemperatureCorrectionFactor(
                    pollutantCode,
                    temperatureC);

            double atRef =
                GetHenrysLawTemperatureCorrectionFactor(
                    pollutantCode,
                    referenceTemperatureC);

            if (!(atRef > 0.0) ||
                double.IsInfinity(atRef) ||
                double.IsNaN(atT))
            {
                throw new InvalidOperationException(
                    $"Invalid Henry temperature correction for '{pollutantCode}'.");
            }

            return atT / atRef;
        }

        /// <summary>
        /// Mole-weighted overall removal (percent) from per-pollutant removal
        /// percentages keyed by component code. Inlet ppm is the weight.
        /// </summary>
        private double ComputeMoleWeightedRemoval(
            CreateDesignViewModel vm,
            IReadOnlyDictionary<string, double> removalPercentByCode)
        {
            double totalInlet = 0.0;
            double totalRemoved = 0.0;

            foreach (var pollutant in vm.Pollutants)
            {
                string? code =
                    _componentLookup?
                        .GetByPollutantId(
                            pollutant.PollutantType)
                        ?.Code;

                if (code == null ||
                    !removalPercentByCode.TryGetValue(
                        code,
                        out double removalPercent))
                {
                    throw new InvalidOperationException(
                        $"No removal result for pollutant type {pollutant.PollutantType}.");
                }

                double inlet = pollutant.InletConcentration;

                if (double.IsNaN(inlet) ||
                    double.IsInfinity(inlet) ||
                    inlet < 0.0)
                {
                    throw new InvalidOperationException(
                        $"Invalid inlet concentration for pollutant type {pollutant.PollutantType}.");
                }

                totalInlet += inlet;
                totalRemoved += inlet * removalPercent / 100.0;
            }

            if (!(totalInlet > 0.0))
            {
                throw new InvalidOperationException(
                    "Total inlet concentration must be positive.");
            }

            return totalRemoved / totalInlet * 100.0;
        }

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

            public double MinLiquidToGasLPerM3 { get; }
        }

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
            double y2 =
                (TryResolveTargetOutletPpm(pollutant) ?? 0.0) / 1_000_000.0;

            double gasKmolPerM3 =
                vm.InletPressure /
                (8314.462 * (vm.InletTemperature + 273.15));

            var sufficiency =
                ReagentSufficiencyService.Evaluate(
                    new ReagentSufficiencyInput
                    {
                        Reagent = kind,
                        ReagentEquivalentsPerL = eqPerL,
                        StoichiometricRatio = nu,
                        InletGasMoleFraction = y1,
                        OutletGasMoleFraction = y2,
                        GasKmolPerM3 = gasKmolPerM3,
                        LiquidToGasLPerM3 = vm.LiquidToGasRatio
                    });

            return new ReactiveEquilibriumBasis(
                sufficiency.Sufficient,
                sufficiency.RequiredLiquidToGasLPerM3);
        }

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
            double actualGasDensityKgM3,
            double effectiveEquilibriumSlope = double.NaN)
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
                        .GetByCode(vm.LiquidSolventCode);

                var packingData =
                    _packingLookup
                        .GetByCode(packingCode);

                if (soluteData?
                        .MolarVolumeAtBoilingPointCm3Mol == null ||
                    solventData?
                        .AssociationFactor == null ||
                    packingData == null)
                {
                    throw new InvalidOperationException($"Missing Wilke-Chang/Onda data for {pollutantCode}, solvent {vm.LiquidSolventCode} or packing {packingCode}.");
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
                            vm.LiquidMolecularWeightGMol,
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
                            vm.CarrierGasCode,
                        molecularWeightB:
                            vm.CarrierGasMolecularWeightGMol,
                        temperatureK:
                            gasTempK,
                        pressureKPa:
                            pressureKPa);

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
                            vm.LiquidSurfaceTensionNM,
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
                            vm.LiquidSurfaceTensionNM));

                double kGaY =
                    onda.GasFilmCoeffKmolM2SPa *
                    vm.InletPressure *
                    onda.WettedAreaM2M3;

                // Liquid molar density (kmol/m3) = rho (kg/m3) / MW (kg/kmol)
                double liquidMolarDensityKmolM3 =
                    vm.LiquidDensity /
                    vm.LiquidMolecularWeightGMol;

                double kLaX =
                    onda.LiquidFilmCoeffMS *
                    liquidMolarDensityKmolM3 *
                    onda.WettedAreaM2M3;

                double equilibriumSlopeForResistance =
                    IsPositiveFinite(effectiveEquilibriumSlope)
                        ? effectiveEquilibriumSlope
                        : henrysLawConstant;

                double overallKGa =
                    1.0 /
                    (
                        1.0 / kGaY
                        +
                        equilibriumSlopeForResistance / kLaX
                    );

                // The minimum-H slope is used as an optimistic upper-bound
                // assumption when reactive equilibrium is treated as
                // instantaneous and irreversible.
                bool enhancementIsUpperBound =
                    effectiveEquilibriumSlope == HenrysConstantUnits.MinimumH;

                double soluteMoleFraction =
                    pollutant.InletConcentration /
                    1_000_000.0;

                double mixtureMW =
                    soluteMoleFraction *
                    pollutant.MolecularWeight
                    +
                    (1 -
                     soluteMoleFraction) *
                    vm.CarrierGasMolecularWeightGMol;

                return new RateBasedFilmResult
                {
                    GasMolarVelocityKmolM2S =
                        gasMassVelocity /
                        mixtureMW,

                    OverallKGaKmolM3S =
                        overallKGa,

                    EnhancementIsUpperBound =
                        enhancementIsUpperBound
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
                                    GetHenrysLawRelativeCorrectionFactor(
                                        pollutantCode,
                                        t,
                                        vm.LiquidTemperature)
                        });
                }

                if (pollutantInputs.Count == 0)
                    throw new InvalidOperationException("No pollutant had a resolvable component code.");

                double gasFlowM3S =
                    vm.ActualFlowRate /
                    3600.0;

                var densityBasisPollutant = vm.Pollutants[0];
                double actualGasDensity = GetActualGasDensity(
                    vm.GasDensity,
                    vm.InletTemperature + 273.15,
                    vm.InletPressure,
                    densityBasisPollutant.PollutantType,
                    densityBasisPollutant.InletConcentration);

                double gasMassFlowKgS =
                    gasFlowM3S *
                    actualGasDensity;

                double liquidFlowM3S =
                    vm.LiquidToGasRatio *
                    gasFlowM3S /
                    1000.0;

                double liquidMassFlowKgS =
                    liquidFlowM3S *
                    vm.LiquidDensity;

                var packingData =
                    RequirePackingData(vm.PackingCode);

                double packingSpecificAreaM2M3 =
                    packingData.SpecificAreaM2M3;

                double packingNominalSizeM =
                    packingData.NominalSizeM;

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
                            actualGasDensity,

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
                                    GetHenrysLawRelativeCorrectionFactor(
                                        pollutantCode,
                                        t,
                                        vm.LiquidTemperature)
                        });
                }

                if (pollutantInputs.Count == 0)
                    throw new InvalidOperationException("No pollutant had a resolvable component code.");

                double gasFlowM3S =
                    vm.ActualFlowRate /
                    3600.0;

                var densityBasisPollutant = vm.Pollutants[0];
                double actualGasDensity = GetActualGasDensity(
                    vm.GasDensity,
                    vm.InletTemperature + 273.15,
                    vm.InletPressure,
                    densityBasisPollutant.PollutantType,
                    densityBasisPollutant.InletConcentration);

                double gasMassFlowKgS =
                    gasFlowM3S *
                    actualGasDensity;

                double liquidFlowM3S =
                    vm.LiquidToGasRatio *
                    gasFlowM3S /
                    1000.0;

                double liquidMassFlowKgS =
                    liquidFlowM3S *
                    vm.LiquidDensity;

                var packingData =
                    RequirePackingData(vm.PackingCode);

                double packingSpecificAreaM2M3 =
                    packingData.SpecificAreaM2M3;

                double packingNominalSizeM =
                    packingData.NominalSizeM;

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
                            actualGasDensity,

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

            double y1 = inletPpm / 1e6;
            double y2 = outletPpm / 1e6;

            double x2 = inletLiquidPpm / 1e6;

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
            double gasPressurePa,
            double solventMolecularWeightGMol)
        {
            if (!IsPositiveFinite(solventMolecularWeightGMol))
                throw new ArgumentOutOfRangeException(
                    nameof(solventMolecularWeightGMol),
                    solventMolecularWeightGMol,
                    "Solvent molecular weight must be positive and finite.");

            const double RGas =
                8314.462;

            double tempK =
                gasTemperatureC +
                273.15;

            double gasKmolPerM3 =
                gasPressurePa /
                (RGas * tempK);

            double liquidKmolPerM3Gas =
                (litersPerM3Gas / 1000.0) *
                liquidDensityKgM3 /
                solventMolecularWeightGMol;

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
            double gasPressurePa,
            double solventMolecularWeightGMol)
        {
            if (!IsPositiveFinite(solventMolecularWeightGMol))
                throw new ArgumentOutOfRangeException(
                    nameof(solventMolecularWeightGMol),
                    solventMolecularWeightGMol,
                    "Solvent molecular weight must be positive and finite.");

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
                solventMolecularWeightGMol /
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
        //  4. VENTURI SIZING  (Johnstone efficiency, Calvert dP)
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

            double throatArea = gasFlowRateM3S / throatVelocityMs;
            double throatDiam = Math.Sqrt(4.0 * throatArea / Math.PI);

            // Johnstone efficiency, Calvert pressure drop and Nukiyama-Tanasawa
            // droplet size (see VenturiCollectionModel for the correlations).
            var collection =
                VenturiCollectionModel.Calculate(
                    throatVelocityMs: throatVelocityMs,
                    liquidToGasRatioLM3: liquidToGasRatioLM3,
                    particleDiameterM: particleDiameterMicron * 1e-6,
                    particleDensityKgM3: particleDensityKgM3,
                    gasViscosityPas: gasViscosityPas,
                    liquidDensityKgM3: liquidDensityKgM3,
                    geometricStdDev: DesignBasisSettings.VenturiParticleGeometricStdDev);
            if (!double.IsFinite(collection.CollectionEfficiencyPercent) ||
                collection.CollectionEfficiencyPercent < 0.0 ||
                collection.CollectionEfficiencyPercent > 100.0)
                throw new InvalidOperationException("Venturi model produced an invalid collection efficiency.");

            if (!double.IsFinite(collection.PressureDropPa) || collection.PressureDropPa <= 0.0)
                throw new InvalidOperationException("Venturi model produced an invalid pressure drop.");

            var sizing = new VenturiSizingResult
            {
                ThroatDiameter = throatDiam,
                ThroatArea = throatArea,
                ThroatVelocity = throatVelocityMs,
                PressureDrop = collection.PressureDropPa,
                CollectionEfficiency = collection.CollectionEfficiencyPercent,
                DropletDiameterMicron = collection.DropletDiameterM * 1e6
            };

            sizing.Warnings.AddRange(collection.Warnings);

            return sizing;
        }

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
                double targetOutletPpm,
                double liquidDensityKgM3,
                double gasTemperatureC,
                double gasPressurePa,
                double solventMolecularWeightGMol)
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
                        gasPressurePa,
                        solventMolecularWeightGMol);

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

        // Applicability flags let the results UI hide correlations that do not
        // belong to the selected scrubber type instead of displaying zeros as data.
        public bool UsesPackingMetrics { get; set; } = true;
        public bool UsesFloodingMetrics { get; set; } = true;
        public bool HasMinimumLGRatioEstimate { get; set; } = true;

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

        // Spray-tower droplet-transfer diagnostics; distinct from packed-bed NTU/HTU.
        public double SprayTowerNTU { get; set; }
        public double SprayTowerHTU { get; set; }

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

        // Feasibility / mode.
        public bool IsFeasible { get; set; } = true;
        public string? InfeasibilityReason { get; set; }
        public double MaxAchievableRemovalPercent { get; set; }

        // True when the packing height was fixed by the user.
        public bool IsRatingMode { get; set; }
        // Impingement plate results
        public int ImpingementPlateStages { get; set; }
        public double ImpingementPlateActiveAreaM2 { get; set; }
        public double ImpingementPlateGasVelocityMPerS { get; set; }
        public double ImpingementPlateFloodingVelocityMPerS { get; set; }
        public double ImpingementPlatePercentFlood { get; set; }
        public double ImpingementPlatePressureDropPa { get; set; }
        public double ImpingementPlateRemovalEfficiencyPercent { get; set; }
        public double ImpingementPlateCutDiameterMicron { get; set; }
        public double ImpingementPlateParticulateEfficiencyPercent { get; set; }
        public double ImpingementPlatePredictedCutDiameterMicron { get; set; }
        public bool ImpingementPlateRemovalIsReactiveUpperBound { get; set; }
        public List<string> Warnings { get; set; } = new();

        // ── Wet cyclone results (zero / null for other scrubber types) ──
        public double CycloneCutDiameterMicron { get; set; }
        public int CycloneParallelUnits { get; set; }
        public double CycloneInletAreaM2 { get; set; }
        public double CycloneGasOutletDiameterM { get; set; }
        public double CycloneEffectiveTurns { get; set; }
        public double CycloneWaterRateLMin { get; set; }

        // Model status flags (set by models that report them).
        public string ModelValidationStatus { get; set; } = string.Empty;
        public bool GasAbsorptionNotModeled { get; set; }
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
        public double DropletDiameterMicron { get; set; }
        public List<string> Warnings { get; set; } = new();
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