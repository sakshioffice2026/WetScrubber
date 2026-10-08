
﻿using System;
using System.Linq;
using WetScrubber.Business.Services;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Models;
using WetScrubber.Repositories.Contracts;
using WetScrubber.Repositories.Repositories;

namespace WetScrubber.Services
{
    // UI orchestration layer for the Chemistry Calculation page.
    //
    // Responsibilities:
    //   - Read master data through IUnitOfWork.
    //   - Read authoritative Henry's-law data.
    //   - Build ChemistryCalculationIntegration input.
    //   - Supply a complete PackingMassTransferInput.
    //   - Execute the chemistry calculation.
    //   - Map the result into ChemistryReportViewModel.
    //
    // This class does not modify the repository or database.
    public class ChemistryUIService
    {
        private const double WaterMolecularWeightKgPerKmol = 18.015;

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

        // ─────────────────────────────────────────────────────────────
        // GET: Build calculation form
        // ─────────────────────────────────────────────────────────────

        public ChemistryCalculationFormViewModel BuildForm()
        {
            var vm = new ChemistryCalculationFormViewModel();

            PopulateDropdowns(vm);

            return vm;
        }

        public void PopulateDropdowns(
            ChemistryCalculationFormViewModel vm)
        {
            if (vm == null)
                throw new ArgumentNullException(nameof(vm));

            vm.Pollutants =
                _uow.pollutantRepository.GetAll(activeOnly: true);

            vm.Liquids =
                _uow.scrubbingLiquidRepository.GetAll(activeOnly: true);
        }

        // ─────────────────────────────────────────────────────────────
        // POST: Execute calculation
        // ─────────────────────────────────────────────────────────────

        public ChemistryReportViewModel RunCalculation(
            ChemistryCalculationFormViewModel form)
        {
            if (form == null)
                throw new ArgumentNullException(nameof(form));

            var pollutant =
                _uow.pollutantRepository.GetById(form.PollutantId);

            var liquid =
                _uow.scrubbingLiquidRepository.GetById(
                    form.ScrubbingLiquidId);

            if (pollutant == null || liquid == null)
            {
                throw new InvalidOperationException(
                    "Pollutant or scrubbing liquid not found.");
            }

            // ─────────────────────────────────────────────────────────
            // Henry's law
            // ─────────────────────────────────────────────────────────

            var henryData =
                _henrysLawLookup.GetByPollutantCode(pollutant.Code);

            if (henryData == null)
            {
                throw new InvalidOperationException(
                    $"No active authoritative Henry's Law record exists " +
                    $"for pollutant '{pollutant.Code}'. " +
                    "Add the pollutant to HenrysLawData before running " +
                    "a chemistry calculation.");
            }

            if (henryData.H_ReferenceAt25C <= 0)
            {
                throw new InvalidOperationException(
                    $"The authoritative Henry's Law constant for pollutant " +
                    $"'{pollutant.Code}' must be positive; received " +
                    $"{henryData.H_ReferenceAt25C}.");
            }

            // ─────────────────────────────────────────────────────────
            // Reaction stoichiometry
            // ─────────────────────────────────────────────────────────

            var reaction =
                _uow.chemicalReactionRepository
                    .GetPrimaryForPair(
                        form.PollutantId,
                        form.ScrubbingLiquidId);

            double reagentStoichiometricRatio =
                ResolveReactionStoichiometricRatio(reaction);

            // ─────────────────────────────────────────────────────────
            // Liquid flow
            //
            // The current form exposes liquid flow in kmol/hr.
            //
            // ChemistryCalculationIntegration also supports volumetric
            // liquid flow. Derive the equivalent physical volumetric flow
            // from the current molar flow and liquid density.
            // ─────────────────────────────────────────────────────────

            double inletLiquidFlowM3PerHr =
                ResolveLiquidVolumetricFlowM3PerHr(form);

            // ─────────────────────────────────────────────────────────
            // Complete packing input
            // ─────────────────────────────────────────────────────────

            var packing =
                BuildStandardPackingInput(
                    form,
                    inletLiquidFlowM3PerHr);

            // ─────────────────────────────────────────────────────────
            // Integration input
            // ─────────────────────────────────────────────────────────

            var input =
                new ChemistryCalculationIntegration.ChemistryCalculationInput
                {
                    // Pollutant
                    PollutantCode =
                        pollutant.Code,

                    PollutantCAS =
                        pollutant.Code,

                    PollutantMolecularWeightKgKmol =
                        pollutant.DefaultMolecularWeight,

                    // Gas stream
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

                    // Liquid stream
                    InletLiquidFlowM3PerHr =
                        inletLiquidFlowM3PerHr,

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

                    // Solvent / reagent
                    SolventCode =
                        "H2O",

                    ReagentCode =
                        liquid.Code,

                    ReagentConcentrationMolPerL =
                        form.ReagentConcentrationMolPerL,

                    ReagentStoichiometricRatio =
                        reagentStoichiometricRatio,

                    // Temperatures
                    //
                    // Gas temperature is used by gas-side calculations.
                    // Liquid temperature is used by Henry's law and
                    // liquid-side calculations.
                    GasTemperatureC =
                        form.GasTemperatureC,

                    LiquidTemperatureC =
                        form.LiquidTemperatureC,

                    PressureKPa =
                        form.PressureKPa,

                    // Henry's law
                    HenrysConstantAt25C =
                        henryData.H_ReferenceAt25C,

                    HeatOfSolutionKJmol =
                        henryData.HeatOfSolutionKJmol,

                    HenryConvention =
                        HenrysLawConvention.LiquidReferenced,

                    // Tower
                    PackingHeightM =
                        form.PackingHeightM,

                    TargetRemovalEfficiencyPercent =
                        form.TargetRemovalEfficiencyPercent,

                    // Reactive absorption
                    IncludeReactiveAbsorption =
                        form.IncludeReactiveAbsorption,

                    ReactionRateConstantS_Inv =
                        form.ReactionRateConstantS_Inv,

                    BulkReagentConcentrationMolL =
                        form.BulkReagentConcentrationMolL,

                    // Packing / mass transfer
                    Packing =
                        packing
                };

            // ─────────────────────────────────────────────────────────
            // Execute calculation
            // ─────────────────────────────────────────────────────────

            var result =
                ChemistryCalculationIntegration
                    .ExecuteFullCalculation(input);

            // IMPORTANT:
            // Pass the actual double variable here.
            //
            // Do NOT pass:
            //     ResolveReactionStoichiometricRatio
            //
            // because that would be a method group rather than a double.
            return MapToReportViewModel(
                result,
                pollutant,
                liquid,
                input.GasTemperatureC,
                input.LiquidTemperatureC,
                reagentStoichiometricRatio);
        }

        // ─────────────────────────────────────────────────────────────
        // Liquid flow helper
        // ─────────────────────────────────────────────────────────────

        private static double ResolveLiquidVolumetricFlowM3PerHr(
            ChemistryCalculationFormViewModel form)
        {
            if (form.InletLiquidFlowKmolPerHr <= 0)
            {
                throw new InvalidOperationException(
                    "Inlet liquid flow must be greater than zero.");
            }

            if (form.InletLiquidDensityKgM3 <= 0)
            {
                throw new InvalidOperationException(
                    "Inlet liquid density must be greater than zero.");
            }

            /*
             * For the current H2O solvent basis:
             *
             *     mass flow kg/hr
             *       = kmol/hr × kg/kmol
             *
             *     volumetric flow m3/hr
             *       = mass flow / density
             *
             * Therefore:
             *
             *     Vdot = n_dot × MW / rho
             */

            return
                form.InletLiquidFlowKmolPerHr *
                WaterMolecularWeightKgPerKmol /
                form.InletLiquidDensityKgM3;
        }

        // ─────────────────────────────────────────────────────────────
        // Packing input
        // ─────────────────────────────────────────────────────────────

        private static PackingMassTransferInput BuildStandardPackingInput(
            ChemistryCalculationFormViewModel form,
            double inletLiquidFlowM3PerHr)
        {
            if (form == null)
                throw new ArgumentNullException(nameof(form));

            if (inletLiquidFlowM3PerHr <= 0)
            {
                throw new InvalidOperationException(
                    "Liquid volumetric flow must be greater than zero.");
            }

            /*
             * Standard fallback packing values.
             *
             * These are deliberately isolated here because the current
             * ChemistryCalculationFormViewModel does not expose packing
             * selection or tower diameter.
             */

            const double specificAreaM2M3 = 110.0;
            const double nominalSizeM = 0.050;
            const double criticalSurfaceTensionNM = 0.033;
            const double liquidSurfaceTensionNM = 0.072;

            // Temporary standard tower cross-sectional area.
            const double towerAreaM2 = 1.0;

            // Liquid mass flow:
            //
            //     kg/hr = m3/hr × kg/m3
            //     kg/s  = kg/hr / 3600
            double liquidMassFlowKgS =
                inletLiquidFlowM3PerHr *
                form.InletLiquidDensityKgM3 /
                3600.0;

            /*
             * The current form does not expose total gas mass flow.
             * The integration only requires a positive value for the
             * packing mass-transfer input, so preserve the existing gas
             * flow basis here.
             *
             * This should be replaced by an authoritative total-gas
             * mass-flow field when that field is added to the form model.
             */
            double gasMassFlowKgS =
                form.InletGasFlowKmolPerHr / 3600.0;

            var packing =
                new PackingMassTransferInput
                {
                    SpecificAreaM2M3 =
                        specificAreaM2M3,

                    NominalSizeM =
                        nominalSizeM,

                    CriticalSurfaceTensionNM =
                        criticalSurfaceTensionNM,

                    LiquidSurfaceTensionNM =
                        liquidSurfaceTensionNM,

                    TowerAreaM2 =
                        towerAreaM2,

                    GasMassFlowKgS =
                        gasMassFlowKgS,

                    LiquidMassFlowKgS =
                        liquidMassFlowKgS
                };

            // Defensive contract check.
            if (!packing.IsComplete)
            {
                throw new InvalidOperationException(
                    "The packing mass-transfer input is incomplete.");
            }

            return packing;
        }

        // ─────────────────────────────────────────────────────────────
        // Reaction stoichiometry helper
        // ─────────────────────────────────────────────────────────────

        private static double ResolveReactionStoichiometricRatio(
            Database.ChemicalReaction? reaction)
        {
            return reaction?.StoichiometricRatio > 0
                ? reaction.StoichiometricRatio
                : 1.0;
        }

        // ─────────────────────────────────────────────────────────────
        // Result mapping
        // ─────────────────────────────────────────────────────────────

        private static ChemistryReportViewModel MapToReportViewModel(
            ChemistryCalculationIntegration.ChemistryCalculationResult result,
            Database.Pollutant pollutant,
            Database.ScrubbingLiquid liquid,
            double gasTemperatureC,
            double liquidTemperatureC,
            double reagentStoichiometricRatio)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            if (pollutant == null)
                throw new ArgumentNullException(nameof(pollutant));

            if (liquid == null)
                throw new ArgumentNullException(nameof(liquid));

            var r = result.Report;

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
                        result.AllFindings?.ToList()
                        ?? new(),

                    GeneratedAtUtc =
                        r?.GeneratedAtUtc
                        ?? DateTime.UtcNow,

                    ReagentStoichiometricRatio =
                        reagentStoichiometricRatio,

                    // ChemistryReportViewModel has separate temperature
                    // properties. The current EnhancedChemistryReport
                    // Conditions DTO still has only the legacy
                    // TemperatureC property, so use the authoritative
                    // calculation input values here.
                    GasTemperatureC =
                        gasTemperatureC,

                    LiquidTemperatureC =
                        liquidTemperatureC
                };

            // ─────────────────────────────────────────────────────────
            // Operating conditions
            // ─────────────────────────────────────────────────────────

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

                // DO NOT use:
                //
                // vm.TemperatureC = r.Conditions.TemperatureC;
                //
                // ChemistryReportViewModel uses:
                //     GasTemperatureC
                //     LiquidTemperatureC
                //
                // and the legacy report DTO does not expose separate
                // temperature properties.

                vm.PressureKPa =
                    r.Conditions.PressureKPa;
            }

            // ─────────────────────────────────────────────────────────
            // Model selections
            // ─────────────────────────────────────────────────────────

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

            // ─────────────────────────────────────────────────────────
            // Equilibrium
            // ─────────────────────────────────────────────────────────

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

            // ─────────────────────────────────────────────────────────
            // Mass transfer
            // ─────────────────────────────────────────────────────────

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

            // ─────────────────────────────────────────────────────────
            // Reagent
            // ─────────────────────────────────────────────────────────

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

            // ─────────────────────────────────────────────────────────
            // Material balance
            // ─────────────────────────────────────────────────────────

            if (r?.MaterialBalance != null)
            {
                vm.ClosureErrorFraction =
                    r.MaterialBalance.ClosureErrorFraction;

                vm.IsBalanced =
                    r.MaterialBalance.IsBalanced;

                vm.ClosureStatement =
                    r.MaterialBalance.ClosureStatement;
            }

            // ─────────────────────────────────────────────────────────
            // Validity / diagnostics
            // ─────────────────────────────────────────────────────────

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

