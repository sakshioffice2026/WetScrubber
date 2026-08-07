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
    // pattern) — reads master data through IUnitOfWork, hands the numbers to
    // ChemistryCalculationIntegration, and flattens the result into the
    // view-friendly ChemistryReportViewModel. No engineering number is
    // computed here; this class only wires inputs/outputs together.
    public class ChemistryUIService
    {
        private readonly UnitOfWorks _uow;

        public ChemistryUIService(IUnitOfWork uow)
        {
            _uow = uow as UnitOfWorks;
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
        public ChemistryReportViewModel RunCalculation(ChemistryCalculationFormViewModel form)
        {
            var pollutant = _uow.pollutantRepository.GetById(form.PollutantId);
            var liquid = _uow.scrubbingLiquidRepository.GetById(form.ScrubbingLiquidId);

            if (pollutant == null || liquid == null)
                throw new InvalidOperationException("Pollutant or scrubbing liquid not found.");

            // The engine hard-rejects a non-positive Henry's constant. Catch it
            // here with a clear message instead of letting ArgumentException
            // bubble up as an unhandled 500 — this happens when the pollutant's
            // master row was never given a DefaultHenrysLawConstant.
            if (pollutant.DefaultHenrysLawConstant <= 0)
                throw new InvalidOperationException(
                    $"'{pollutant.DisplayName}' has no Henry's Law constant set (currently 0). " +
                    "Edit this pollutant on the Pollutants page and set a positive value before running a calculation.");

            // Primary reaction for the pair (if curated) supplies the reagent
            // stoichiometry defaults; falls back to a 1:1 physical estimate.
            var reaction = _uow.chemicalReactionRepository.GetPrimaryForPair(form.PollutantId, form.ScrubbingLiquidId);

            var input = new ChemistryCalculationIntegration.ChemistryCalculationInput
            {
                PollutantCode = pollutant.Code,
                PollutantCAS = pollutant.Code,
                PollutantMolecularWeightKgKmol = pollutant.DefaultMolecularWeight,

                InletGasMoleFractionPollutant = form.InletConcentrationPpmv / 1_000_000.0,
                InletGasFlowKmolPerHr = form.InletGasFlowKmolPerHr,
                InletGasDensityKgM3 = form.InletGasDensityKgM3,
                InletGasViscosityPas = form.InletGasViscosityPas,
                InletGasDiffusivityM2S = form.InletGasDiffusivityM2S,
                InletLiquidFlowKmolPerHr = form.InletLiquidFlowKmolPerHr,
                InletLiquidMoleFraction = form.InletLiquidMoleFraction,
                InletLiquidDensityKgM3 = form.InletLiquidDensityKgM3,
                InletLiquidViscosityPas = form.InletLiquidViscosityPas,
                InletLiquidDiffusivityM2S = form.InletLiquidDiffusivityM2S,

                SolventCode = "H2O",
                ReagentCode = liquid.Code,
                ReagentConcentrationMolPerL = form.ReagentConcentrationMolPerL,

                TemperatureC = form.TemperatureC,
                PressureKPa = form.PressureKPa,
                HenrysConstantAt25C = pollutant.DefaultHenrysLawConstant,
                HenryConvention = HenrysLawConvention.LiquidReferenced,

                PackingHeightM = form.PackingHeightM,
                TargetRemovalEfficiencyPercent = form.TargetRemovalEfficiencyPercent,

                IncludeReactiveAbsorption = form.IncludeReactiveAbsorption,
                ReactionRateConstantS_Inv = form.ReactionRateConstantS_Inv,
                BulkReagentConcentrationMolL = form.BulkReagentConcentrationMolL
            };

            var result = ChemistryCalculationIntegration.ExecuteFullCalculation(input);

            return MapToReportViewModel(result, pollutant, liquid, reaction);
        }

        // ── Helpers ──────────────────────────────────────────────────
        private static ChemistryReportViewModel MapToReportViewModel(
            ChemistryCalculationIntegration.ChemistryCalculationResult result,
            Database.Pollutant pollutant,
            Database.ScrubbingLiquid liquid,
            Database.ChemicalReaction? reaction)
        {
            var r = result.Report;

            var vm = new ChemistryReportViewModel
            {
                PollutantName = pollutant.DisplayName,
                PollutantFormula = pollutant.Formula,
                LiquidName = liquid.DisplayName,
                LiquidFormula = liquid.Formula,

                IsValid = result.IsValid,
                ReadyForIndustrialUse = result.ReadyForIndustrialUse,
                AllFindings = result.AllFindings?.ToList() ?? new(),
                GeneratedAtUtc = r?.GeneratedAtUtc ?? DateTime.UtcNow
            };

            if (r?.Conditions != null)
            {
                vm.InletConcentrationValue = r.Conditions.InletConcentrationValue;
                vm.InletConcentrationUnits = r.Conditions.InletConcentrationUnits;
                vm.OutletConcentrationValue = r.Conditions.OutletConcentrationValue;
                vm.OutletConcentrationUnits = r.Conditions.OutletConcentrationUnits;
                vm.RemovalEfficiencyPercent = r.Conditions.RemovalEfficiencyPercent;
                vm.GasFlowKmolPerHr = r.Conditions.GasFlowKmolPerHr;
                vm.LiquidFlowKmolPerHr = r.Conditions.LiquidFlowKmolPerHr;
                vm.LiquidToGasRatio = r.Conditions.LiquidToGasRatio;
                vm.ReagentConcentrationMolPerL = r.Conditions.ReagentConcentrationMolPerL;
                vm.TemperatureC = r.Conditions.TemperatureC;
                vm.PressureKPa = r.Conditions.PressureKPa;
            }

            if (r?.ModelSelections != null)
            {
                vm.HenryLawModel = r.ModelSelections.HenryLawModel;
                vm.HenryConvention = r.ModelSelections.HenryConvention;
                vm.ActivityModel = r.ModelSelections.ActivityModel;
                vm.ReactionModel = r.ModelSelections.ReactionModel;
                vm.MassTransferModel = r.ModelSelections.MassTransferModel;
                vm.SaltingOutConsidered = r.ModelSelections.SaltingOutConsidered;
                vm.ReactiveAbsorptionModeled = r.ModelSelections.ReactiveAbsorptionModeled;
            }

            if (r?.Equilibrium != null)
            {
                vm.DrivingForceInletMolFraction = r.Equilibrium.DrivingForceInletMolFraction;
                vm.DrivingForceOutletMolFraction = r.Equilibrium.DrivingForceOutletMolFraction;
                vm.PinchPointDetected = r.Equilibrium.PinchPointDetected;
                vm.PinchWarning = r.Equilibrium.PinchWarning;
            }

            if (r?.MassTransfer != null)
            {
                vm.GasSideResistanceFraction = r.MassTransfer.GasSideResistanceFraction;
                vm.LiquidSideResistanceFraction = r.MassTransfer.LiquidSideResistanceFraction;
                vm.ControllingResistance = r.MassTransfer.ControllingResistance;
                vm.EnhancementFactorFromReaction = r.MassTransfer.EnhancementFactorFromReaction;
            }

            if (r?.Reagent != null)
            {
                vm.AbsorbedPollutantKmolPerHr = r.Reagent.AbsorbedPollutantKmolPerHr;
                vm.StoichiometricReagentDemandKmolPerHr = r.Reagent.StoichiometricReagentDemandKmolPerHr;
                vm.ReagentSuppliedKmolPerHr = r.Reagent.ReagentSuppliedKmolPerHr;
                vm.ExcessReagentFactor = r.Reagent.ExcessReagentFactor;
                vm.ReagentUtilizationFraction = r.Reagent.ReagentUtilizationFraction;
            }

            if (r?.MaterialBalance != null)
            {
                vm.ClosureErrorFraction = r.MaterialBalance.ClosureErrorFraction;
                vm.IsBalanced = r.MaterialBalance.IsBalanced;
                vm.ClosureStatement = r.MaterialBalance.ClosureStatement;
            }

            if (r?.Validity != null)
            {
                vm.CriticalErrorCount = r.Validity.CriticalErrorCount;
                vm.WarningCount = r.Validity.WarningCount;
                vm.CriticalErrors = r.Validity.CriticalErrors;
                vm.Warnings = r.Validity.Warnings;
                vm.HiddenAssumptions = r.Validity.HiddenAssumptions;
            }

            return vm;
        }
    }
}