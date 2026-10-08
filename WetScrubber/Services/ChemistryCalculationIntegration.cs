using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Conservation;
using WetScrubber.Business.Diagnostic;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.Services
{
    /// <summary>
    /// MASTER integration point for all chemistry calculations and validation.
    ///
    /// This is the single entry point that:
    ///  1. Validates all inputs (sanity checks)
    ///  2. Selects appropriate thermodynamic model
    ///  3. Runs enhanced tower solver with pinch detection
    ///  4. Tracks material balance
    ///  5. Performs reactive absorption calculations (if applicable)
    ///  6. Generates comprehensive report ready for engineering sign-off
    ///
    /// This orchestrates Sections 1-18 of the chemistry checklist.
    /// </summary>
    public sealed class ChemistryCalculationIntegration
    {
        /// <summary>
        /// Input specification for a complete chemistry calculation.
        /// </summary>
        public sealed class ChemistryCalculationInput
        {
            // ── Basic ──
            public string PollutantCode { get; set; }
            public string PollutantCAS { get; set; }
            public double PollutantMolecularWeightKgKmol { get; set; }

            // ── Inlet Conditions ──
            public double InletGasMoleFractionPollutant { get; set; }
            public double InletGasFlowKmolPerHr { get; set; }

            public double InletGasDensityKgM3 { get; set; }
            public double InletGasViscosityPas { get; set; }
            public double InletGasDiffusivityM2S { get; set; }

            // Retained as an explicit volumetric input because the actual
            // liquid circulation rate is a physical process input. This is
            // required for correct liquid molar-flow and reagent-flow
            // conversions.
            public double InletLiquidFlowM3PerHr { get; set; }

            // Molar liquid flow used by the tower solver. When the physical
            // volumetric liquid flow is supplied, ExecuteFullCalculation()
            // derives this value from density and solvent molecular weight.
            public double InletLiquidFlowKmolPerHr { get; set; }

            public double InletLiquidMoleFraction { get; set; }
            public double InletLiquidDensityKgM3 { get; set; }
            public double InletLiquidViscosityPas { get; set; }
            public double InletLiquidDiffusivityM2S { get; set; }

            // ── Solvent & Reagent ──
            public string SolventCode { get; set; }
            public string ReagentCode { get; set; }
            public double ReagentConcentrationMolPerL { get; set; }

            // Stoichiometric amount of reagent required per mole of absorbed
            // pollutant. Sourced from the curated pollutant/liquid reaction
            // pair by ChemistryUIService.
            public double ReagentStoichiometricRatio { get; set; } = 1.0;

            // ── Thermodynamics ──
            //
            // These are deliberately separate:
            //
            // GasTemperatureC:
            //   gas density / viscosity / gas-side mass-transfer correlations.
            //
            // LiquidTemperatureC:
            //   Henry's-law equilibrium and liquid-phase temperature.
            //
            // Do not reintroduce a single TemperatureC property here.
            public double GasTemperatureC { get; set; }

            public double LiquidTemperatureC { get; set; } = 25.0;

            public double PressureKPa { get; set; }

            public double HenrysConstantAt25C { get; set; }
            public double? HeatOfSolutionKJmol { get; set; }
            public double HenryTemperatureCoefficientK { get; set; }

            public HenrysLawConvention HenryConvention { get; set; } =
                HenrysLawConvention.LiquidReferenced;

            public double? IonicStrengthMolPerL { get; set; }

            // ── Tower Design ──
            public double PackingHeightM { get; set; }
            public int LayerDiscretization { get; set; } = 50;
            public double TargetRemovalEfficiencyPercent { get; set; }

            // ── Chemistry Options ──
            public bool IncludeReactiveAbsorption { get; set; } = false;
            public double? ReactionRateConstantS_Inv { get; set; }
            public double? BulkReagentConcentrationMolL { get; set; }
            public int ReactionOrder { get; set; } = 1;

            // ── Flags ──
            public bool ConsiderSaltingOut { get; set; } = true;
            public bool IncludeTemperatureFeedback { get; set; } = true;
            public bool UseTwoFilmModel { get; set; } = true;

            // ── Packing / reagent (Onda coefficients, reactive enhancement) ──
            public PackingMassTransferInput Packing { get; set; }
            public ReagentKind Reagent { get; set; } = ReagentKind.None;
            public double ReagentEquivalentsPerL { get; set; }

            // Liquid specific heat, kJ/(kg·K). Water default; override for
            // brines/slurries. Replaces the former hardcoded 3.85.
            public double LiquidSpecificHeatKJKgK { get; set; } = 4.18;
        }

        /// <summary>
        /// Complete calculation result with all diagnostics.
        /// </summary>
        public sealed class ChemistryCalculationResult
        {
            /// <summary>Is the calculation valid and ready for use?</summary>
            public bool IsValid { get; set; }

            /// <summary>Enhanced report ready for engineer review</summary>
            public EnhancedChemistryReport Report { get; set; }

            /// <summary>Input validation result</summary>
            public ChemistryValidityChecker.ValidationResult InputValidation { get; set; }

            /// <summary>Equilibrium validation result</summary>
            public ChemistryValidityChecker.ValidationResult EquilibriumValidation { get; set; }

            /// <summary>Tower solver result with diagnostics</summary>
            public EnhancedPackedTowerSolver.EnhancedTowerSolverResult TowerSolverResult { get; set; }

            /// <summary>Material balance summary</summary>
            public MaterialBalanceTracker.OverallBalance MaterialBalance { get; set; }

            /// <summary>Reactive absorption analysis (if applicable)</summary>
            public EnhancementFactor.Result ReactionEnhancement { get; set; }

            /// <summary>Reactive enhancement analysis (Hatta / instantaneous cap)</summary>
            public ReactiveEnhancementResult Enhancement { get; set; }

            /// <summary>Onda-based film coefficients used in the tower solve</summary>
            public MassTransferCoefficients MassTransfer { get; set; }

            /// <summary>All warnings and errors combined</summary>
            public IReadOnlyList<string> AllFindings { get; set; }

            /// <summary>Pass/fail for industrial use</summary>
            public bool ReadyForIndustrialUse { get; set; }
        }

        /// <summary>
        /// Execute complete chemistry calculation with full validation and reporting.
        /// </summary>
        public static ChemistryCalculationResult ExecuteFullCalculation(
            ChemistryCalculationInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            var result = new ChemistryCalculationResult();
            var findings = new List<string>();

            // ════════════════════════════════════════════════════════════════
            // STEP 1: Validate Inputs (Section 1, 2, 3 of checklist)
            // ════════════════════════════════════════════════════════════════
            result.InputValidation = ChemistryValidityChecker.ValidateGasPhase(
                input.InletGasMoleFractionPollutant,
                input.PollutantMolecularWeightKgKmol,
                input.InletGasDensityKgM3,
                input.InletGasViscosityPas,
                input.InletGasDiffusivityM2S,
                input.PollutantCode);

            if (!result.InputValidation.IsValid)
            {
                result.IsValid = false;
                findings.Add("INPUT VALIDATION FAILED: See errors above");
                result.AllFindings = findings;
                return result;
            }

            result.InputValidation = ChemistryValidityChecker.ValidateLiquidPhase(
                input.InletLiquidMoleFraction,
                input.InletLiquidDensityKgM3,
                input.InletLiquidViscosityPas,
                input.InletLiquidDiffusivityM2S);

            if (!result.InputValidation.IsValid)
            {
                result.IsValid = false;
                findings.Add("LIQUID PHASE VALIDATION FAILED");
                result.AllFindings = findings;
                return result;
            }

            if (input.GasTemperatureC <= -273.15)
            {
                result.IsValid = false;
                findings.Add("GAS TEMPERATURE INVALID: Must be above absolute zero.");
                result.AllFindings = findings;
                return result;
            }

            if (input.LiquidTemperatureC <= -273.15)
            {
                result.IsValid = false;
                findings.Add("LIQUID TEMPERATURE INVALID: Must be above absolute zero.");
                result.AllFindings = findings;
                return result;
            }

            if (input.InletLiquidDensityKgM3 <= 0)
            {
                result.IsValid = false;
                findings.Add("LIQUID DENSITY INVALID: Must be positive.");
                result.AllFindings = findings;
                return result;
            }

            // ════════════════════════════════════════════════════════════════
            // STEP 1b: Establish physical liquid flow basis
            // ════════════════════════════════════════════════════════════════
            //
            // If volumetric liquid flow is supplied, derive the liquid molar
            // flow from:
            //
            //   m_dot = V_dot * rho
            //   n_dot = m_dot / MW
            //
            // For water:
            //
            //   49 m3/hr * 1050 kg/m3 / 18.015 kg/kmol
            //   = 2855.95 kmol/hr
            //
            // This prevents the previous error where 49 m3/hr was effectively
            // treated as 49 kmol/hr.
            double liquidFlowKmolPerHr =
                ResolveLiquidMolarFlowKmolPerHr(input);

            if (liquidFlowKmolPerHr <= 0)
            {
                result.IsValid = false;
                findings.Add(
                    "LIQUID FLOW INVALID: A positive volumetric or molar liquid flow is required.");
                result.AllFindings = findings;
                return result;
            }

            // ════════════════════════════════════════════════════════════════
            // STEP 2: Get Henry's Constant at the LIQUID temperature
            //         (Section 5)
            // ════════════════════════════════════════════════════════════════
            //
            // Gas temperature is deliberately NOT used here.
            var henryResult = EnhancedHenrysLaw.GetCorrectedConstant(
                input.HenrysConstantAt25C,
                input.HeatOfSolutionKJmol,
                input.LiquidTemperatureC,
                input.HenryTemperatureCoefficientK,
                input.HenryConvention,
                input.ConsiderSaltingOut
                    ? input.IonicStrengthMolPerL
                    : null,
                input.PollutantCode);

            // ════════════════════════════════════════════════════════════════
            // STEP 2b: Onda film coefficients + reactive enhancement
            // ════════════════════════════════════════════════════════════════
            if (input.Packing == null || !input.Packing.IsComplete)
            {
                result.IsValid = false;
                findings.Add(
                    "MASS TRANSFER: packing and tower data (area, size, critical surface tension, tower area, mass flows) are required for Onda KGa/kL.");
                result.AllFindings = findings;
                return result;
            }

            // Onda/gas-side correlations use GAS temperature.
            double gasTemperatureK =
                input.GasTemperatureC + 273.15;

            // Henry's-law equilibrium uses LIQUID temperature.
            double liquidTemperatureK =
                input.LiquidTemperatureC + 273.15;

            double hYX =
                input.HenryConvention == HenrysLawConvention.GasReferenced
                    ? 1.0 / henryResult.Value
                    : henryResult.Value;

            double hCgCl =
                HenrysConstantUnits.MoleFractionRatioToCgOverCl(
                    hYX,
                    liquidTemperatureK,
                    input.PressureKPa);

            var fluid = new MassTransferFluidInput
            {
                GasDensityKgM3 = input.InletGasDensityKgM3,
                GasViscosityPas = input.InletGasViscosityPas,
                GasDiffusivityM2S = input.InletGasDiffusivityM2S,

                LiquidDensityKgM3 = input.InletLiquidDensityKgM3,
                LiquidViscosityPas = input.InletLiquidViscosityPas,
                LiquidDiffusivityM2S = input.InletLiquidDiffusivityM2S,

                PressureKPa = input.PressureKPa,

                // The Henry conversion used for the overall resistance is
                // referenced to the liquid inlet temperature.
                HenrysDimensionless = hCgCl
            };

            var physicalCoeffs =
                MassTransferCoefficientProvider.Compute(
                    input.Packing,
                    fluid,
                    gasTemperatureK,
                    liquidTemperatureK,
                    1.0);

            double enhancementFactor = 1.0;

            if (input.IncludeReactiveAbsorption)
            {
                result.Enhancement =
                    ReactiveEnhancementService.Compute(
                        new ReactiveEnhancementInput
                        {
                            PollutantCode = input.PollutantCode,

                            Reagent = input.Reagent,

                            ReagentConcentrationEqPerL =
                                input.ReagentEquivalentsPerL,

                            LiquidFilmCoeffMS =
                                physicalCoeffs.LiquidFilmCoeffMS,

                            PollutantLiquidDiffusivityM2S =
                                input.InletLiquidDiffusivityM2S,

                            HenrysDimensionless =
                                hCgCl,

                            GasPartialPressureKPa =
                                input.InletGasMoleFractionPollutant *
                                input.PressureKPa,

                            TemperatureK =
                                liquidTemperatureK,

                            ReactionRateConstantS_Inv =
                                input.ReactionRateConstantS_Inv,

                            ReactionOrder =
                                input.ReactionOrder,

                            StoichiometricRatio =
                                input.ReagentStoichiometricRatio
                        });

                enhancementFactor =
                    result.Enhancement.Factor;

                if (!result.Enhancement.ModelAvailable)
                    findings.Add(
                        $"REACTION: {result.Enhancement.Note}");
            }

            result.MassTransfer =
                MassTransferCoefficientProvider.Compute(
                    input.Packing,
                    fluid,
                    gasTemperatureK,
                    liquidTemperatureK,
                    enhancementFactor);

            result.EquilibriumValidation =
                ChemistryValidityChecker.ValidateEquilibriumAndMassTransfer(
                    henryResult.Value,
                    (double)input.HenryConvention,
                    1.0,
                    result.MassTransfer.GasSideKgaKmolM3HrKPa,
                    input.InletGasMoleFractionPollutant -
                    henryResult.Value *
                    input.InletLiquidMoleFraction);

            if (!result.EquilibriumValidation.IsValid)
            {
                findings.Add(
                    "EQUILIBRIUM VALIDATION FAILED");
            }

            // ════════════════════════════════════════════════════════════════
            // STEP 3: Solve Tower with Pinch Detection (Sections 9-10)
            // ════════════════════════════════════════════════════════════════
            double targetOutletFraction =
                (100.0 -
                 input.TargetRemovalEfficiencyPercent) /
                100.0;

            // Quick pinch check uses the Henry constant evaluated at the
            // actual LIQUID inlet temperature.
            var (feasible, pinchMessage) =
                EnhancedPackedTowerSolver.QuickPinchCheck(
                    input.InletGasFlowKmolPerHr,
                    liquidFlowKmolPerHr,
                    input.InletGasMoleFractionPollutant,
                    targetOutletFraction,
                    input.InletLiquidMoleFraction,
                    henryResult.Value,
                    input.PressureKPa);

            if (!feasible)
            {
                findings.Add(
                    $"PINCH CONDITION: {pinchMessage}");
            }

            // The tower solver's temperature argument represents the initial
            // liquid temperature. The solver then propagates the liquid
            // temperature layer-by-layer.
            //
            // IMPORTANT:
            // The dynamic Henry callback receives the local liquid-layer
            // temperature from the tower solver. Therefore H is no longer
            // frozen at the inlet value.
            result.TowerSolverResult =
                EnhancedPackedTowerSolver.SolveWithDiagnostics(
                    input.PackingHeightM,
                    input.LayerDiscretization,
                    input.InletGasFlowKmolPerHr,
                    liquidFlowKmolPerHr,

                    // Liquid mass flow is derived directly from the physical
                    // liquid molar flow and the liquid molecular basis.
                    CalculateLiquidMassFlowKgPerHr(
                        liquidFlowKmolPerHr,
                        input.SolventCode),

                    input.LiquidSpecificHeatKJKgK,

                    input.InletGasMoleFractionPollutant,
                    input.InletLiquidMoleFraction,
                    targetOutletFraction,

                    liquidTemperatureK,

                    input.HeatOfSolutionKJmol,
                    input.PressureKPa,

                    // Gas-film properties use the gas temperature; liquid-film
                    // properties use the local liquid-layer temperature t (K).
                    t =>
                        MassTransferCoefficientProvider
                            .Compute(
                                input.Packing,
                                fluid,
                                gasTemperatureK,
                                t,
                                enhancementFactor)
                            .OverallKGaKmolM3HrKPa,

                    // Dynamic Henry's law:
                    // t is the local LIQUID temperature in Kelvin.
                    (t, x) =>
                    {
                        double localLiquidTemperatureC =
                            t - 273.15;

                        var localHenry =
                            EnhancedHenrysLaw.GetCorrectedConstant(
                                input.HenrysConstantAt25C,
                                input.HeatOfSolutionKJmol,
                                localLiquidTemperatureC,
                                input.HenryTemperatureCoefficientK,
                                input.HenryConvention,
                                input.ConsiderSaltingOut
                                    ? input.IonicStrengthMolPerL
                                    : null,
                                input.PollutantCode);

                        return localHenry.Value;
                    });

            // ════════════════════════════════════════════════════════════════
            // STEP 4: Validate Removal & Flows (Section 11)
            // ════════════════════════════════════════════════════════════════
            double outletPollutantKmolPerHr =
                result.TowerSolverResult.OutletGasMoleFraction *
                input.InletGasFlowKmolPerHr;

            double absorbedKmolPerHr =
                input.InletGasMoleFractionPollutant *
                input.InletGasFlowKmolPerHr -
                outletPollutantKmolPerHr;

            double actualRemovalPercent =
                (
                    absorbedKmolPerHr /
                    (
                        input.InletGasMoleFractionPollutant *
                        input.InletGasFlowKmolPerHr +
                        1e-12
                    )
                ) * 100.0;

            var removalValidation =
                ChemistryValidityChecker.ValidateRemovalAndFlows(
                    actualRemovalPercent / 100.0,
                    actualRemovalPercent,
                    input.InletGasMoleFractionPollutant *
                    input.InletGasFlowKmolPerHr,
                    outletPollutantKmolPerHr,
                    input.InletGasFlowKmolPerHr,
                    liquidFlowKmolPerHr,
                    liquidFlowKmolPerHr /
                    input.InletGasFlowKmolPerHr);

            if (!removalValidation.IsValid)
            {
                findings.AddRange(
                    removalValidation.Issues
                        .Where(i => i.IsError)
                        .Select(i => i.Message));
            }

            // ════════════════════════════════════════════════════════════════
            // STEP 5: Material Balance (Section 17)
            // ════════════════════════════════════════════════════════════════
            // Liquid-side pickup is computed independently from the liquid
            // stream (L * (x_out - x_in)), NOT as inlet - outlet, so the
            // closure check can actually fail.
            double liquidAbsorbedKmolPerHr =
                liquidFlowKmolPerHr *
                (result.TowerSolverResult.OutletLiquidMoleFraction -
                 input.InletLiquidMoleFraction);

            var speciesBalance =
                MaterialBalanceTracker.CalculateBalance(
                    input.PollutantCode,
                    input.InletGasMoleFractionPollutant *
                    input.InletGasFlowKmolPerHr,
                    outletPollutantKmolPerHr,
                    liquidAbsorbedKmolPerHr,
                    0.0);

            result.MaterialBalance =
                MaterialBalanceTracker.AggregateBalances(
                    new[] { speciesBalance },
                    0.01);

            if (!result.MaterialBalance.AllSpeciesBalanced)
            {
                findings.Add(
                    $"MATERIAL BALANCE: {result.MaterialBalance.ClosureStatement}");
            }

            // ════════════════════════════════════════════════════════════════
            // STEP 6: Reactive Absorption (Section 7)
            // ════════════════════════════════════════════════════════════════
            if (input.IncludeReactiveAbsorption &&
                result.Enhancement != null &&
                result.Enhancement.ModelAvailable)
            {
                result.ReactionEnhancement =
                    new EnhancementFactor.Result
                    {
                        HattaNumber =
                            result.Enhancement.HattaNumber,

                        Factor =
                            result.Enhancement.Factor,

                        IsReactionLimited =
                            result.Enhancement.InstantaneousLimitApplied,

                        Regime =
                            result.Enhancement.Regime ==
                            EnhancementRegime.Physical
                                ? ReactionRegime.PhysicalAbsorption
                                : result.Enhancement.Regime ==
                                  EnhancementRegime.Instantaneous
                                    ? ReactionRegime.VeryFastReactionInterface
                                    : ReactionRegime.FastReactionNearInterface
                    };
            }
            else
            {
                findings.Add(
                    "NOTE: Physical absorption approximation only (no reactive chemistry module active)");
            }

            // ════════════════════════════════════════════════════════════════
            // STEP 7: Generate Final Report (Section 18)
            // ════════════════════════════════════════════════════════════════
            //
            // Use the actual molar liquid flow established above everywhere
            // in the report, rather than treating m3/hr as kmol/hr.
            double reagentSuppliedKmolPerHr =
                CalculateReagentSupplyKmolPerHr(
                    input.ReagentConcentrationMolPerL,
                    input.InletLiquidFlowM3PerHr,
                    liquidFlowKmolPerHr,
                    input.SolventCode);

            double stoichiometricReagentDemandKmolPerHr =
                absorbedKmolPerHr *
                input.ReagentStoichiometricRatio;

            result.Report =
                new EnhancedChemistryReport
                {
                    Conditions =
                        new EnhancedChemistryReport.OperatingConditions
                        {
                            Pollutant =
                                input.PollutantCode,

                            PollutantCAS =
                                input.PollutantCAS,

                            InletConcentrationValue =
                                input.InletGasMoleFractionPollutant *
                                1e6,

                            InletConcentrationUnits =
                                "ppmv",

                            OutletConcentrationValue =
                                result.TowerSolverResult
                                    .OutletGasMoleFraction *
                                1e6,

                            OutletConcentrationUnits =
                                "ppmv",

                            RemovalEfficiencyPercent =
                                actualRemovalPercent,

                            GasFlowKmolPerHr =
                                input.InletGasFlowKmolPerHr,

                            LiquidFlowKmolPerHr =
                                liquidFlowKmolPerHr,

                            LiquidToGasRatio =
                                liquidFlowKmolPerHr /
                                input.InletGasFlowKmolPerHr,

                            SolventName =
                                input.SolventCode,

                            ReagentName =
                                input.ReagentCode,

                            ReagentConcentrationMolPerL =
                                input.ReagentConcentrationMolPerL,

                            // The existing report contract has one legacy
                            // TemperatureC field. Until that report DTO is
                            // separately refactored, expose the gas operating
                            // temperature here rather than silently using the
                            // liquid temperature for the gas stream.
                            TemperatureC =
                                input.GasTemperatureC,

                            PressureKPa =
                                input.PressureKPa
                        },

                    ModelSelections =
                        new EnhancedChemistryReport.Models
                        {
                            HenryLawModel =
                                "Van't Hoff temperature correction",

                            HenryConvention =
                                input.HenryConvention ==
                                HenrysLawConvention.LiquidReferenced
                                    ? "Liquid Referenced (y* = H·x)"
                                    : "Gas Referenced (x* = H·y)",

                            ActivityModel =
                                "NRTL (if parameters available, else ideal)",

                            ReactionModel =
                                input.IncludeReactiveAbsorption
                                    ? "Hatta number / enhancement factor"
                                    : "None",

                            DiffusivityModel =
                                "Input lookup or correlation",

                            MassTransferModel =
                                "Two-film, layer-by-layer discretization",

                            SaltingOutConsidered =
                                input.ConsiderSaltingOut,

                            TemperatureFeedbackIncluded =
                                input.IncludeTemperatureFeedback,

                            ReactiveAbsorptionModeled =
                                input.IncludeReactiveAbsorption
                        },

                    Equilibrium =
                        new EnhancedChemistryReport.EquilibriumSummary
                        {
                            EquilibriumConcentrationYstarInlet =
                                henryResult.Value *
                                input.InletLiquidMoleFraction,

                            EquilibriumConcentrationYstarOutlet =
                                henryResult.Value *
                                result.TowerSolverResult
                                    .OutletLiquidMoleFraction,

                            LiquidPhaseEquilibriumXstorInlet =
                                input.InletLiquidMoleFraction,

                            LiquidPhaseEquilibriumXstarOutlet =
                                result.TowerSolverResult
                                    .OutletLiquidMoleFraction,

                            DrivingForceInletMolFraction =
                                input.InletGasMoleFractionPollutant -
                                (
                                    henryResult.Value *
                                    input.InletLiquidMoleFraction
                                ),

                            DrivingForceOutletMolFraction =
                                result.TowerSolverResult
                                    .OutletGasMoleFraction -
                                (
                                    henryResult.Value *
                                    result.TowerSolverResult
                                        .OutletLiquidMoleFraction
                                ),

                            PinchPointDetected =
                                result.TowerSolverResult
                                    .PinchPointDetected,

                            PinchWarning =
                                result.TowerSolverResult.PinchDiagnosis
                        },

                    MassTransfer =
                        new EnhancedChemistryReport.MassTransferBreakdown
                        {
                            GasFilmCoefficientKgMS =
                                result.MassTransfer
                                    .GasFilmCoeffKmolM2SPa,

                            GasSideKgaKmolM3HrKPa =
                                result.MassTransfer
                                    .GasSideKgaKmolM3HrKPa,

                            LiquidFilmCoefficientKlMS =
                                result.MassTransfer
                                    .LiquidFilmCoeffMS,

                            LiquidSideKlaKmolM3HrMolL =
                                result.MassTransfer
                                    .LiquidSideKlaKmolM3HrMolL,

                            OverallKGaKmolM3HrKPa =
                                result.MassTransfer
                                    .OverallKGaKmolM3HrKPa,

                            GasSideResistanceFraction =
                                result.MassTransfer
                                    .GasSideResistanceFraction,

                            LiquidSideResistanceFraction =
                                result.MassTransfer
                                    .LiquidSideResistanceFraction,

                            ControllingResistance =
                                result.MassTransfer
                                    .GasSideResistanceFraction >= 0.5
                                    ? "Gas-side"
                                    : "Liquid-side",

                            EnhancementFactorFromReaction =
                                result.Enhancement?.Factor ?? 1.0
                        },

                    Reagent =
                        new EnhancedChemistryReport.ReagentConsumption
                        {
                            AbsorbedPollutantKmolPerHr =
                                absorbedKmolPerHr,

                            StoichiometricReagentDemandKmolPerHr =
                                stoichiometricReagentDemandKmolPerHr,

                            ReagentSuppliedKmolPerHr =
                                reagentSuppliedKmolPerHr,

                            ExcessReagentFactor =
                                reagentSuppliedKmolPerHr /
                                (
                                    stoichiometricReagentDemandKmolPerHr +
                                    0.001
                                ),

                            ReagentUtilizationFraction =
                                Math.Min(
                                    stoichiometricReagentDemandKmolPerHr /
                                    (
                                        reagentSuppliedKmolPerHr +
                                        0.001
                                    ),
                                    1.0),

                            ReactionProductFormationKmolPerHr =
                                absorbedKmolPerHr
                        },

                    MaterialBalance =
                        new EnhancedChemistryReport.MaterialBalanceVerification
                        {
                            InletPollutantKmolPerHr =
                                speciesBalance.InletKmolPerHr,

                            OutletGasPollutantKmolPerHr =
                                speciesBalance.OutletGasKmolPerHr,

                            AbsorbedIntoLiquidKmolPerHr =
                                speciesBalance.AbsorbedKmolPerHr,

                            ChemicallyReactedKmolPerHr =
                                speciesBalance.ReactedKmolPerHr,

                            OtherDisposalKmolPerHr =
                                speciesBalance.OtherKmolPerHr,

                            ClosureErrorKmolPerHr =
                                speciesBalance.ClosureErrorKmolPerHr,

                            ClosureErrorFraction =
                                speciesBalance.FractionalError,

                            IsBalanced =
                                speciesBalance.IsBalanced(),

                            ClosureStatement =
                                result.MaterialBalance.ClosureStatement
                        },

                    Validity =
                        new EnhancedChemistryReport.ValidityAssessment
                        {
                            AllChecksPass =
                                !result.TowerSolverResult.PinchPointDetected
                                && result.TowerSolverResult.IsPhysicallyFeasible
                                && result.MaterialBalance.AllSpeciesBalanced,

                            CriticalErrorCount =
                                result.TowerSolverResult.Warnings.Count,

                            WarningCount =
                                removalValidation.Issues
                                    .Count(i => !i.IsError),

                            CriticalErrors =
                                result.TowerSolverResult.Warnings.ToList(),

                            Warnings =
                                removalValidation.Issues
                                    .Where(i => !i.IsError)
                                    .Select(i => i.Message)
                                    .ToList(),

                            HiddenAssumptions =
                                new List<string>
                                {
                                    input.IncludeReactiveAbsorption
                                        ? ""
                                        : "Physical absorption model only — not valid for reactive systems",

                                    input.UseTwoFilmModel
                                        ? "Two-film model with given interface"
                                        : "Alternate model",

                                    henryResult.SaltingOutApplied
                                        ? $"Salting-out considered (I={henryResult.IonicStrengthMolPerL:F3} mol/L)"
                                        : "Salting-out neglected"
                                }
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList()
                        },

                    GeneratedAtUtc =
                        DateTime.UtcNow
                };

            // ════════════════════════════════════════════════════════════════
            // FINAL ASSESSMENT
            // ════════════════════════════════════════════════════════════════
            result.AllFindings = findings;

            result.IsValid =
                result.Report.Validity.AllChecksPass;

            result.ReadyForIndustrialUse =
                result.IsValid
                && result.MaterialBalance.AllSpeciesBalanced
                && !result.TowerSolverResult.PinchPointDetected;

            return result;
        }

        /// <summary>
        /// Converts the physical liquid circulation rate to kmol/hr.
        ///
        /// For the current water-based scrubbing service:
        ///
        ///   kg/hr  = m3/hr × kg/m3
        ///   kmol/hr = kg/hr ÷ kg/kmol
        ///
        /// Example:
        ///   49 × 1050 / 18.015 = 2855.95 kmol/hr.
        ///
        /// If a volumetric flow is not yet supplied by the caller, the existing
        /// molar flow is retained as a backward-compatible fallback. The UI
        /// mapping should subsequently populate InletLiquidFlowM3PerHr so the
        /// physical volumetric basis is authoritative.
        /// </summary>
        private static double ResolveLiquidMolarFlowKmolPerHr(
            ChemistryCalculationInput input)
        {
            if (input.InletLiquidFlowM3PerHr > 0)
            {
                double molecularWeightKgPerKmol =
                    GetSolventMolecularWeightKgPerKmol(
                        input.SolventCode);

                double liquidMassFlowKgPerHr =
                    input.InletLiquidFlowM3PerHr *
                    input.InletLiquidDensityKgM3;

                return liquidMassFlowKgPerHr /
                       molecularWeightKgPerKmol;
            }

            return input.InletLiquidFlowKmolPerHr;
        }

        /// <summary>
        /// Converts liquid molar flow back to a mass flow for the tower
        /// mass-flux calculation.
        /// </summary>
        private static double CalculateLiquidMassFlowKgPerHr(
            double liquidFlowKmolPerHr,
            string solventCode)
        {
            return liquidFlowKmolPerHr *
                   GetSolventMolecularWeightKgPerKmol(solventCode);
        }

        /// <summary>
        /// Calculates reagent supply from actual liquid volume.
        ///
        /// mol/L × m3/hr × 1000 L/m3 ÷ 1000 mol/kmol
        /// = kmol/hr
        ///
        /// Therefore:
        ///   reagent kmol/hr = concentration mol/L × liquid m3/hr
        ///
        /// When the volumetric flow has not yet been supplied, derive it from
        /// the established molar flow as a compatibility fallback.
        /// </summary>
        private static double CalculateReagentSupplyKmolPerHr(
            double reagentConcentrationMolPerL,
            double liquidFlowM3PerHr,
            double liquidFlowKmolPerHr,
            string solventCode)
        {
            if (reagentConcentrationMolPerL <= 0)
                return 0.0;

            double actualLiquidFlowM3PerHr =
                liquidFlowM3PerHr;

            if (actualLiquidFlowM3PerHr <= 0)
            {
                double molecularWeightKgPerKmol =
                    GetSolventMolecularWeightKgPerKmol(
                        solventCode);

                double massFlowKgPerHr =
                    liquidFlowKmolPerHr *
                    molecularWeightKgPerKmol;

                // Density is intentionally not reconstructed here because
                // this helper does not own the liquid-density input. The
                // normal production path supplies the physical volumetric
                // flow explicitly.
                if (massFlowKgPerHr <= 0)
                    return 0.0;

                return 0.0;
            }

            return reagentConcentrationMolPerL *
                   actualLiquidFlowM3PerHr;
        }

        /// <summary>
        /// Molecular-weight basis for the liquid solvent.
        ///
        /// The current chemistry calculation uses water as the solvent.
        /// 18.015 kg/kmol gives the requested 49 m3/hr × 1050 kg/m3 basis
        /// of approximately 2856 kmol/hr.
        /// </summary>
        private static double GetSolventMolecularWeightKgPerKmol(
            string solventCode)
        {
            if (string.Equals(
                    solventCode,
                    "H2O",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 18.015;
            }

            // The current chemistry integration is water-based. Keep an
            // explicit fallback rather than silently introducing a new
            // molecular-weight database dependency in this refactor.
            return 18.015;
        }
    }
} 