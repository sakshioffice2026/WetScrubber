using System;
using System.Collections.Generic;
using System.Text;

namespace WetScrubber.Business.Diagnostic
{
    /// <summary>
    /// Comprehensive chemistry report output matching Section 18 of the checklist.
    /// This is what gets displayed to the engineer to sign off on the design.
    /// </summary>
    public sealed class EnhancedChemistryReport
    {
        /// <summary>Operating conditions summary</summary>
        public sealed class OperatingConditions
        {
            public string Pollutant { get; set; }
            public string PollutantCAS { get; set; }
            public double InletConcentrationValue { get; set; }
            public string InletConcentrationUnits { get; set; }  // ppmv, mg/m3, etc.
            public double OutletConcentrationValue { get; set; }
            public string OutletConcentrationUnits { get; set; }
            public double RemovalEfficiencyPercent { get; set; }
            public double GasFlowKmolPerHr { get; set; }
            public double LiquidFlowKmolPerHr { get; set; }
            public double LiquidToGasRatio { get; set; }
            public string SolventName { get; set; }
            public string ReagentName { get; set; }
            public double ReagentConcentrationMolPerL { get; set; }
            public double TemperatureC { get; set; }
            public double PressureKPa { get; set; }
        }

        /// <summary>Model selections and assumptions</summary>
        public sealed class Models
        {
            public string HenryLawModel { get; set; }
            public string HenryConvention { get; set; }  // "Liquid Referenced (y=H*x)" etc.
            public string ActivityModel { get; set; }
            public string ReactionModel { get; set; }
            public string DiffusivityModel { get; set; }
            public string MassTransferModel { get; set; }
            public bool SaltingOutConsidered { get; set; }
            public bool TemperatureFeedbackIncluded { get; set; }
            public bool ReactiveAbsorptionModeled { get; set; }
        }

        /// <summary>Equilibrium results at inlet and outlet</summary>
        public sealed class EquilibriumSummary
        {
            public double EquilibriumConcentrationYstarInlet { get; set; }
            public double EquilibriumConcentrationYstarOutlet { get; set; }
            public double LiquidPhaseEquilibriumXstorInlet { get; set; }
            public double LiquidPhaseEquilibriumXstarOutlet { get; set; }
            public double DrivingForceInletMolFraction { get; set; }
            public double DrivingForceOutletMolFraction { get; set; }
            public bool PinchPointDetected { get; set; }
            public string PinchWarning { get; set; }  // populated if detected
        }

        /// <summary>Mass transfer coefficient breakdown</summary>
        public sealed class MassTransferBreakdown
        {
            public double GasFilmCoefficientKgMS { get; set; }
            public double GasSideKgaKmolM3HrKPa { get; set; }
            public double LiquidFilmCoefficientKlMS { get; set; }
            public double LiquidSideKlaKmolM3HrMolL { get; set; }
            public double OverallKGaKmolM3HrKPa { get; set; }
            public double GasSideResistanceFraction { get; set; }  // % of total
            public double LiquidSideResistanceFraction { get; set; }
            public string ControllingResistance { get; set; }  // "Gas-side" or "Liquid-side"
            public double EnhancementFactorFromReaction { get; set; }
        }

        /// <summary>Reagent consumption accounting</summary>
        public sealed class ReagentConsumption
        {
            public double AbsorbedPollutantKmolPerHr { get; set; }
            public double StoichiometricReagentDemandKmolPerHr { get; set; }
            public double ReagentSuppliedKmolPerHr { get; set; }
            public double ExcessReagentFactor { get; set; }  // supplied / stoich
            public double ReagentUtilizationFraction { get; set; }  // actual / supplied
            public double ReactionProductFormationKmolPerHr { get; set; }
            public string ReactionProductSpecies { get; set; }
        }

        /// <summary>Material balance verification</summary>
        public sealed class MaterialBalanceVerification
        {
            public double InletPollutantKmolPerHr { get; set; }
            public double OutletGasPollutantKmolPerHr { get; set; }
            public double AbsorbedIntoLiquidKmolPerHr { get; set; }
            public double ChemicallyReactedKmolPerHr { get; set; }
            public double OtherDisposalKmolPerHr { get; set; }
            public double ClosureErrorKmolPerHr { get; set; }
            public double ClosureErrorFraction { get; set; }
            public bool IsBalanced { get; set; }
            public string ClosureStatement { get; set; }
        }

        /// <summary>Validity assessment</summary>
        public sealed class ValidityAssessment
        {
            public bool AllChecksPass { get; set; }
            public int CriticalErrorCount { get; set; }
            public int WarningCount { get; set; }
            public List<string> CriticalErrors { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
            public List<string> HiddenAssumptions { get; set; } = new();
        }

        /// <summary>All report sections</summary>
        public OperatingConditions Conditions { get; set; }
        public Models ModelSelections { get; set; }
        public EquilibriumSummary Equilibrium { get; set; }
        public MassTransferBreakdown MassTransfer { get; set; }
        public ReagentConsumption Reagent { get; set; }
        public MaterialBalanceVerification MaterialBalance { get; set; }
        public ValidityAssessment Validity { get; set; }
        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>
        /// Generate a human-readable text report.
        /// </summary>
        public string GenerateTextReport()
        {
            var sb = new StringBuilder();

            sb.AppendLine("════════════════════════════════════════════════════════════════");
            sb.AppendLine("         WETSCRUBBER CHEMISTRY CALCULATION REPORT");
            sb.AppendLine("════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            // ── Operating Conditions ──
            sb.AppendLine("1. OPERATING CONDITIONS");
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            if (Conditions != null)
            {
                sb.AppendLine($"  Pollutant:              {Conditions.Pollutant}");
                sb.AppendLine($"  Inlet Conc:             {Conditions.InletConcentrationValue:F6} {Conditions.InletConcentrationUnits}");
                sb.AppendLine($"  Outlet Conc:            {Conditions.OutletConcentrationValue:F6} {Conditions.OutletConcentrationUnits}");
                sb.AppendLine($"  Removal Efficiency:     {Conditions.RemovalEfficiencyPercent:F2}%");
                sb.AppendLine($"  Gas Flow:               {Conditions.GasFlowKmolPerHr:F2} kmol/hr");
                sb.AppendLine($"  Liquid Flow:            {Conditions.LiquidFlowKmolPerHr:F2} kmol/hr");
                sb.AppendLine($"  L/G Ratio:              {Conditions.LiquidToGasRatio:F3}");
                sb.AppendLine($"  Solvent:                {Conditions.SolventName}");
                sb.AppendLine($"  Reagent:                {Conditions.ReagentName} ({Conditions.ReagentConcentrationMolPerL:F3} mol/L)");
                sb.AppendLine($"  Temperature:            {Conditions.TemperatureC:F1}°C");
                sb.AppendLine($"  Pressure:               {Conditions.PressureKPa:F1} kPa");
            }
            sb.AppendLine();

            // ── Models ──
            sb.AppendLine("2. MODELS & THERMODYNAMIC FRAMEWORK");
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            if (ModelSelections != null)
            {
                sb.AppendLine($"  Henry's Law:            {ModelSelections.HenryLawModel}");
                sb.AppendLine($"  Convention:             {ModelSelections.HenryConvention}");
                sb.AppendLine($"  Activity Model:         {ModelSelections.ActivityModel}");
                sb.AppendLine($"  Reaction Model:         {ModelSelections.ReactionModel}");
                sb.AppendLine($"  Diffusivity Model:      {ModelSelections.DiffusivityModel}");
                sb.AppendLine($"  Mass Transfer Model:    {ModelSelections.MassTransferModel}");
                sb.AppendLine($"  Salting-Out Effects:    {(ModelSelections.SaltingOutConsidered ? "Yes" : "No")}");
                sb.AppendLine($"  Temperature Feedback:   {(ModelSelections.TemperatureFeedbackIncluded ? "Yes" : "No")}");
                sb.AppendLine($"  Reactive Absorption:    {(ModelSelections.ReactiveAbsorptionModeled ? "Modeled" : "Physical Absorption Only")}");
            }
            sb.AppendLine();

            // ── Equilibrium ──
            sb.AppendLine("3. EQUILIBRIUM CALCULATIONS");
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            if (Equilibrium != null)
            {
                sb.AppendLine($"  y* (inlet):             {Equilibrium.EquilibriumConcentrationYstarInlet:E6}");
                sb.AppendLine($"  y* (outlet):            {Equilibrium.EquilibriumConcentrationYstarOutlet:E6}");
                sb.AppendLine($"  x* (inlet):             {Equilibrium.LiquidPhaseEquilibriumXstorInlet:E6}");
                sb.AppendLine($"  x* (outlet):            {Equilibrium.LiquidPhaseEquilibriumXstarOutlet:E6}");
                sb.AppendLine($"  Driving Force (inlet):  {Equilibrium.DrivingForceInletMolFraction:E6}");
                sb.AppendLine($"  Driving Force (outlet): {Equilibrium.DrivingForceOutletMolFraction:E6}");
                if (Equilibrium.PinchPointDetected)
                {
                    sb.AppendLine($"  ⚠ PINCH CONDITION:      {Equilibrium.PinchWarning}");
                }
            }
            sb.AppendLine();

            // ── Mass Transfer ──
            sb.AppendLine("4. MASS TRANSFER COEFFICIENTS & RESISTANCE");
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            if (MassTransfer != null)
            {
                sb.AppendLine($"  Gas-side kG:            {MassTransfer.GasFilmCoefficientKgMS:F6} m/s");
                sb.AppendLine($"  Gas-side KGa:           {MassTransfer.GasSideKgaKmolM3HrKPa:E6} kmol/(m³·hr·kPa)");
                sb.AppendLine($"  Liquid-side kL:         {MassTransfer.LiquidFilmCoefficientKlMS:E6} m/s");
                sb.AppendLine($"  Liquid-side KLa:        {MassTransfer.LiquidSideKlaKmolM3HrMolL:E6} kmol/(m³·hr·mol/L)");
                sb.AppendLine($"  Overall KGa:            {MassTransfer.OverallKGaKmolM3HrKPa:E6} kmol/(m³·hr·kPa)");
                sb.AppendLine($"  Gas-side Resistance:    {MassTransfer.GasSideResistanceFraction:F1}%");
                sb.AppendLine($"  Liquid-side Resistance: {MassTransfer.LiquidSideResistanceFraction:F1}%");
                sb.AppendLine($"  Controlling Resistance: {MassTransfer.ControllingResistance}");
                sb.AppendLine($"  Enhancement Factor:     {MassTransfer.EnhancementFactorFromReaction:F2}");
            }
            sb.AppendLine();

            // ── Reagent ──
            sb.AppendLine("5. REAGENT CONSUMPTION");
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            if (Reagent != null)
            {
                sb.AppendLine($"  Absorbed Pollutant:     {Reagent.AbsorbedPollutantKmolPerHr:F4} kmol/hr");
                sb.AppendLine($"  Stoich Demand:          {Reagent.StoichiometricReagentDemandKmolPerHr:F4} kmol/hr");
                sb.AppendLine($"  Reagent Supplied:       {Reagent.ReagentSuppliedKmolPerHr:F4} kmol/hr");
                sb.AppendLine($"  Excess Factor:          {Reagent.ExcessReagentFactor:F2}× stoichiometric");
                sb.AppendLine($"  Utilization:            {Reagent.ReagentUtilizationFraction * 100:F1}%");
                if (Reagent.ReactionProductFormationKmolPerHr > 0)
                {
                    sb.AppendLine($"  Reaction Product:       {Reagent.ReactionProductSpecies}");
                    sb.AppendLine($"  Product Formation:      {Reagent.ReactionProductFormationKmolPerHr:F4} kmol/hr");
                }
            }
            sb.AppendLine();

            // ── Material Balance ──
            sb.AppendLine("6. MATERIAL BALANCE VERIFICATION");
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            if (MaterialBalance != null)
            {
                sb.AppendLine($"  IN:  Inlet Pollutant       {MaterialBalance.InletPollutantKmolPerHr:F6} kmol/hr");
                sb.AppendLine($"  OUT: Gas (unabsorbed)      {MaterialBalance.OutletGasPollutantKmolPerHr:F6} kmol/hr");
                sb.AppendLine($"       + Absorbed (liquid)    {MaterialBalance.AbsorbedIntoLiquidKmolPerHr:F6} kmol/hr");
                sb.AppendLine($"       + Chemically reacted   {MaterialBalance.ChemicallyReactedKmolPerHr:F6} kmol/hr");
                if (MaterialBalance.OtherDisposalKmolPerHr > 0)
                {
                    sb.AppendLine($"       + Other               {MaterialBalance.OtherDisposalKmolPerHr:F6} kmol/hr");
                }
                sb.AppendLine($"  ───────────────────────────────────────────");
                sb.AppendLine($"  Total Out:                 {MaterialBalance.InletPollutantKmolPerHr - MaterialBalance.ClosureErrorKmolPerHr:F6} kmol/hr");
                sb.AppendLine($"  Closure Error:             {MaterialBalance.ClosureErrorKmolPerHr:E9} kmol/hr ({MaterialBalance.ClosureErrorFraction * 100:F3}%)");
                sb.AppendLine();
                sb.AppendLine($"  Status: {MaterialBalance.ClosureStatement}");
            }
            sb.AppendLine();

            // ── Validity ──
            sb.AppendLine("7. CHEMISTRY VALIDITY ASSESSMENT");
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            if (Validity != null)
            {
                if (Validity.AllChecksPass)
                {
                    sb.AppendLine("  ✓ ALL CHECKS PASSED");
                }
                else
                {
                    sb.AppendLine($"  ✗ {Validity.CriticalErrorCount} CRITICAL ERROR(S):");
                    foreach (var error in Validity.CriticalErrors)
                    {
                        sb.AppendLine($"    • {error}");
                    }
                }

                if (Validity.WarningCount > 0)
                {
                    sb.AppendLine($"  ⚠ {Validity.WarningCount} Warning(s):");
                    foreach (var warning in Validity.Warnings)
                    {
                        sb.AppendLine($"    • {warning}");
                    }
                }

                if (Validity.HiddenAssumptions.Count > 0)
                {
                    sb.AppendLine($"  Hidden Assumptions (flagged):");
                    foreach (var assumption in Validity.HiddenAssumptions)
                    {
                        sb.AppendLine($"    • {assumption}");
                    }
                }
            }
            sb.AppendLine();

            // ── Sign-off ──
            sb.AppendLine("════════════════════════════════════════════════════════════════");
            if (Validity?.AllChecksPass == true && MaterialBalance?.IsBalanced == true)
            {
                sb.AppendLine("  ✓✓✓ READY FOR ENGINEERING SIGN-OFF ✓✓✓");
            }
            else
            {
                sb.AppendLine("  ✗✗✗ NOT READY — REVIEW ERRORS ABOVE ✗✗✗");
            }
            sb.AppendLine($"  Report Generated: {GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("════════════════════════════════════════════════════════════════");

            return sb.ToString();
        }
    }
}