using System;
using System.Linq;
using WetScrubber.Business.Services;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Models;
using WetScrubber.Repositories.Contracts;
using WetScrubber.Repositories.Repositories;

namespace WetScrubber.Services
{
    // UI orchestration layer for the Chemistry Calculation page.
    // Never touches ApplicationDbContext directly (matches ChemistryController's
    // pattern) — reads master data through IUnitOfWork and the authoritative
    // Henry's-law lookup, hands the numbers to
    // ChemistryCalculationIntegration, and flattens the result into the
    // view-friendly ChemistryReportViewModel. No engineering number is
    // computed here; this class only wires inputs/outputs together.
    public class ChemistryUIService
    {
        private readonly UnitOfWorks _uow;
        private readonly IHenrysLawLookup _henrysLawLookup;

        public ChemistryUIService(
            IUnitOfWork uow,
            IHenrysLawLookup henrysLawLookup)
        {
            _uow = uow as UnitOfWorks
                ?? throw new ArgumentException(
                    "The supplied IUnitOfWork must be a UnitOfWorks instance.",
                    nameof(uow));

            _henrysLawLookup = henrysLawLookup
                ?? throw new ArgumentNullException(nameof(henrysLawLookup));
        }

        // ── GET Calculation: build the empty form with dropdowns ───────
        public ChemistryCalculationFormViewModel BuildForm()
        {
            var vm = new ChemistryCalculationFormViewModel();
            PopulateDropdowns(vm);
            return vm;
        }

        public void PopulateDropdowns(ChemistryCalculationFormViewModel vm)
        {
            vm.Pollutants = _uow.pollutantRepository.GetAll(activeOnly: true);
            vm.Liquids = _uow.scrubbingLiquidRepository.GetAll(activeOnly: true);
        }

        // ── POST Calculation: run the engine, map to a report VM ───────
        public ChemistryReportViewModel RunCalculation(
            ChemistryCalculationFormViewModel form)
        {
            var pollutant =
                _uow.pollutantRepository.GetById(form.PollutantId);

            var liquid =
                _uow.scrubbingLiquidRepository.GetById(form.ScrubbingLiquidId);

            if (pollutant == null || liquid == null)
                throw new InvalidOperationException(
                    "Pollutant or scrubbing liquid not found.");

            // The authoritative Henry's-law record is keyed by pollutant
            // code and stores H_ReferenceAt25C as dimensionless Cg/Cl
            // volatility-form data. Do not use Pollutant.DefaultHenrysLawConstant
            // here because that value does not guarantee the same convention.
            var henryData =
                _henrysLawLookup.GetByPollutantCode(pollutant.Code);

            if (henryData == null)
            {
                throw new InvalidOperationException(
                    $"No active authoritative Henry's Law record exists for pollutant '{pollutant.Code}'. " +
                    "Add the pollutant to HenrysLawData before running a chemistry calculation.");
            }

            if (henryData.H_ReferenceAt25C <= 0)
            {
                throw new InvalidOperationException(
                    $"The authoritative Henry's Law constant for pollutant '{pollutant.Code}' " +
                    $"must be positive; received {henryData.H_ReferenceAt25C}.");
            }

            // Primary reaction for the pollutant/liquid pair supplies the
            // reagent stoichiometric ratio when a curated reaction exists.
            // A positive ratio is required; otherwise use the physical 1:1
            // fallback.
            var reaction =
                _uow.chemicalReactionRepository
                    .GetPrimaryForPair(
                        form.PollutantId,
                        form.ScrubbingLiquidId);

            double reagentStoichiometricRatio =
                reaction?.StoichiometricRatio > 0
                    ? reaction.StoichiometricRatio
                    : 1.0;

            var input =
                new ChemistryCalculationIntegration.ChemistryCalculationInput
                {
                    PollutantCode = pollutant.Code,
                    PollutantCAS = pollutant.Code,
                    PollutantMolecularWeightKgKmol =
                        pollutant.DefaultMolecularWeight,

                    InletGasMoleFractionPollutant =
                        form.InletConcentrationPpmv / 1_000_000.0,

                    InletGasFlowKmolPerHr =
                        form.InletGasFlowKmolPerHr,

                    InletGasDensityKgM3 =
                        form.InletGasDensityKgM3,

                    InletGasViscosityPas =
                        form.InletGasViscosityPas,

                    InletGasDiffusivityM2S =
                        form.InletGasDiffusivityM2S,

                    InletLiquidFlowKmolPerHr =
                        form.InletLiquidFlowKmolPerHr,

                    InletLiquidMoleFraction =
                        form.InletLiquidMoleFraction,

                    InletLiquidDensityKgM3 =
                        form.InletLiquidDensityKgM3,

                    InletLiquidViscosityPas =
                        form.InletLiquidViscosityPas,

                    InletLiquidDiffusivityM2S =
                        form.InletLiquidDiffusivityM2S,

                    SolventCode = "H2O",

                    ReagentCode =
                        liquid.Code,

                    ReagentConcentrationMolPerL =
                        form.ReagentConcentrationMolPerL,

                    ReagentStoichiometricRatio =
                        reagentStoichiometricRatio,

                    TemperatureC =
                        form.TemperatureC,

                    PressureKPa =
                        form.PressureKPa,

                    // IMPORTANT:
                    // This is now the authoritative Henry's-law database
                    // value in volatility form (Cg/Cl), not the legacy
                    // Pollutant.DefaultHenrysLawConstant.
                    HenrysConstantAt25C =
                        henryData.H_ReferenceAt25C,

                    HeatOfSolutionKJmol =
                        henryData.HeatOfSolutionKJmol,

                    HenryConvention =
                        HenrysLawConvention.LiquidReferenced,

                    PackingHeightM =
                        form.PackingHeightM,

                    TargetRemovalEfficiencyPercent =
                        form.TargetRemovalEfficiencyPercent,

                    IncludeReactiveAbsorption =
                        form.IncludeReactiveAbsorption,

                    ReactionRateConstantS_Inv =
                        form.ReactionRateConstantS_Inv,

                    BulkReagentConcentrationMolL =
                        form.BulkReagentConcentrationMolL
                };

            var result =
                ChemistryCalculationIntegration
                    .ExecuteFullCalculation(input);

            return MapToReportViewModel(
                result,
                pollutant,
                liquid,
                reaction);
        }

        // ── Helpers ──────────────────────────────────────────────────
        private static ChemistryReportViewModel MapToReportViewModel(
            ChemistryCalculationIntegration.ChemistryCalculationResult result,
            Database.Pollutant pollutant,
            Database.ScrubbingLiquid liquid,
            Database.ChemicalReaction? reaction)
        {
            var r = result.Report;

            double reagentStoichiometricRatio =
                reaction?.StoichiometricRatio > 0
                    ? reaction.StoichiometricRatio
                    : 1.0;

            var vm =
                new ChemistryReportViewModel
                {
                    PollutantName =
                        pollutant.DisplayName,

                    PollutantFormula =
                        pollutant.Formula,

                    LiquidName =
                        liquid.DisplayName,

                    LiquidFormula =
                        liquid.Formula,

                    IsValid =
                        result.IsValid,

                    ReadyForIndustrialUse =
                        result.ReadyForIndustrialUse,

                    AllFindings =
                        result.AllFindings?.ToList() ?? new(),

                    GeneratedAtUtc =
                        r?.GeneratedAtUtc ?? DateTime.UtcNow,

                    ReagentStoichiometricRatio =
                        reagentStoichiometricRatio
                };

            if (r?.Conditions != null)
            {
                vm.InletConcentrationValue =
                    r.Conditions.InletConcentrationValue;

                vm.InletConcentrationUnits =
                    r.Conditions.InletConcentrationUnits;

                vm.OutletConcentrationValue =
                    r.Conditions.OutletConcentrationValue;

                vm.OutletConcentrationUnits =
                    r.Conditions.OutletConcentrationUnits;

                vm.RemovalEfficiencyPercent =
                    r.Conditions.RemovalEfficiencyPercent;

                vm.GasFlowKmolPerHr =
                    r.Conditions.GasFlowKmolPerHr;

                vm.LiquidFlowKmolPerHr =
                    r.Conditions.LiquidFlowKmolPerHr;

                vm.LiquidToGasRatio =
                    r.Conditions.LiquidToGasRatio;

                vm.ReagentConcentrationMolPerL =
                    r.Conditions.ReagentConcentrationMolPerL;

                vm.TemperatureC =
                    r.Conditions.TemperatureC;

                vm.PressureKPa =
                    r.Conditions.PressureKPa;
            }

            if (r?.ModelSelections != null)
            {
                vm.HenryLawModel =
                    r.ModelSelections.HenryLawModel;

                vm.HenryConvention =
                    r.ModelSelections.HenryConvention;

                vm.ActivityModel =
                    r.ModelSelections.ActivityModel;

                vm.ReactionModel =
                    r.ModelSelections.ReactionModel;

                vm.MassTransferModel =
                    r.ModelSelections.MassTransferModel;

                vm.SaltingOutConsidered =
                    r.ModelSelections.SaltingOutConsidered;

                vm.ReactiveAbsorptionModeled =
                    r.ModelSelections.ReactiveAbsorptionModeled;
            }

            if (r?.Equilibrium != null)
            {
                vm.DrivingForceInletMolFraction =
                    r.Equilibrium.DrivingForceInletMolFraction;

                vm.DrivingForceOutletMolFraction =
                    r.Equilibrium.DrivingForceOutletMolFraction;

                vm.PinchPointDetected =
                    r.Equilibrium.PinchPointDetected;

                vm.PinchWarning =
                    r.Equilibrium.PinchWarning;
            }

            if (r?.MassTransfer != null)
            {
                vm.GasSideResistanceFraction =
                    r.MassTransfer.GasSideResistanceFraction;

                vm.LiquidSideResistanceFraction =
                    r.MassTransfer.LiquidSideResistanceFraction;

                vm.ControllingResistance =
                    r.MassTransfer.ControllingResistance;

                vm.EnhancementFactorFromReaction =
                    r.MassTransfer.EnhancementFactorFromReaction;
            }

            if (r?.Reagent != null)
            {
                vm.AbsorbedPollutantKmolPerHr =
                    r.Reagent.AbsorbedPollutantKmolPerHr;

                vm.StoichiometricReagentDemandKmolPerHr =
                    r.Reagent.StoichiometricReagentDemandKmolPerHr;

                vm.ReagentSuppliedKmolPerHr =
                    r.Reagent.ReagentSuppliedKmolPerHr;

                vm.ExcessReagentFactor =
                    r.Reagent.ExcessReagentFactor;

                vm.ReagentUtilizationFraction =
                    r.Reagent.ReagentUtilizationFraction;
            }

            if (r?.MaterialBalance != null)
            {
                vm.ClosureErrorFraction =
                    r.MaterialBalance.ClosureErrorFraction;

                vm.IsBalanced =
                    r.MaterialBalance.IsBalanced;

                vm.ClosureStatement =
                    r.MaterialBalance.ClosureStatement;
            }

            if (r?.Validity != null)
            {
                vm.CriticalErrorCount =
                    r.Validity.CriticalErrorCount;

                vm.WarningCount =
                    r.Validity.WarningCount;

                vm.CriticalErrors =
                    r.Validity.CriticalErrors;

                vm.Warnings =
                    r.Validity.Warnings;

                vm.HiddenAssumptions =
                    r.Validity.HiddenAssumptions;
            }

            return vm;
        }
    }
}