using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using WetScrubber.Database;

namespace WetScrubber.Models
{
    // Input form: engineer picks pollutant + liquid, then enters the stream
    // conditions the calculation engine needs. Mirrors ReactionFormViewModel's
    // shape (dropdown sources populated by the controller/service).
    public class ChemistryCalculationFormViewModel
    {
        [Required(ErrorMessage = "Choose a pollutant")]
        public int PollutantId { get; set; }

        [Required(ErrorMessage = "Choose a scrubbing liquid")]
        public int ScrubbingLiquidId { get; set; }

        // ── Gas stream ──
        [Range(0, 1000000, ErrorMessage = "0–1,000,000 ppmv")]
        public double InletConcentrationPpmv { get; set; } = 1000;

        [Range(0.0001, 1000000, ErrorMessage = "Must be > 0")]
        public double InletGasFlowKmolPerHr { get; set; } = 100;

        public double InletGasDensityKgM3 { get; set; } = 1.2;
        public double InletGasViscosityPas { get; set; } = 0.000018;
        public double InletGasDiffusivityM2S { get; set; } = 0.0000001;

        // ── Liquid stream ──
        [Range(0.0001, 1000000, ErrorMessage = "Must be > 0")]
        public double InletLiquidFlowKmolPerHr { get; set; } = 500;

        public double InletLiquidMoleFraction { get; set; } = 0;
        public double InletLiquidDensityKgM3 { get; set; } = 1000;
        public double InletLiquidViscosityPas { get; set; } = 0.001;
        public double InletLiquidDiffusivityM2S { get; set; } = 0.0000000018;

        [Range(0, 100, ErrorMessage = "0–100 mol/L")]
        public double ReagentConcentrationMolPerL { get; set; } = 0.5;

        // ── Operating conditions ──
        [Range(-50, 200, ErrorMessage = "-50–200 °C")]
        public double TemperatureC { get; set; } = 25;

        [Range(1, 1000, ErrorMessage = "1–1000 kPa")]
        public double PressureKPa { get; set; } = 101.325;

        // ── Tower design ──
        [Range(0.1, 100, ErrorMessage = "0.1–100 m")]
        public double PackingHeightM { get; set; } = 5;

        [Range(0, 100, ErrorMessage = "0–100%")]
        public double TargetRemovalEfficiencyPercent { get; set; } = 95;

        // ── Reactive absorption (optional) ──
        public bool IncludeReactiveAbsorption { get; set; } = false;
        public double? ReactionRateConstantS_Inv { get; set; }
        public double? BulkReagentConcentrationMolL { get; set; }

        // Dropdown sources (populated by ChemistryUIService).
        public List<Pollutant> Pollutants { get; set; } = new();
        public List<ScrubbingLiquid> Liquids { get; set; } = new();
    }

    // Flattened, view-friendly projection of EnhancedChemistryReport, plus
    // the pollutant/liquid context so the report page doesn't need to
    // re-query anything.
    public class ChemistryReportViewModel
    {
        public string PollutantName { get; set; } = "";
        public string PollutantFormula { get; set; } = "";
        public string LiquidName { get; set; } = "";
        public string LiquidFormula { get; set; } = "";

        public bool IsValid { get; set; }
        public bool ReadyForIndustrialUse { get; set; }
        public List<string> AllFindings { get; set; } = new();
        public DateTime GeneratedAtUtc { get; set; }

        // ── Operating conditions ──
        public double InletConcentrationValue { get; set; }
        public string InletConcentrationUnits { get; set; } = "ppmv";
        public double OutletConcentrationValue { get; set; }
        public string OutletConcentrationUnits { get; set; } = "ppmv";
        public double RemovalEfficiencyPercent { get; set; }
        public double GasFlowKmolPerHr { get; set; }
        public double LiquidFlowKmolPerHr { get; set; }
        public double LiquidToGasRatio { get; set; }
        public double ReagentConcentrationMolPerL { get; set; }
        public double TemperatureC { get; set; }
        public double PressureKPa { get; set; }

        // ── Models ──
        public string HenryLawModel { get; set; } = "";
        public string HenryConvention { get; set; } = "";
        public string ActivityModel { get; set; } = "";
        public string ReactionModel { get; set; } = "";
        public string MassTransferModel { get; set; } = "";
        public bool SaltingOutConsidered { get; set; }
        public bool ReactiveAbsorptionModeled { get; set; }

        // ── Equilibrium ──
        public double DrivingForceInletMolFraction { get; set; }
        public double DrivingForceOutletMolFraction { get; set; }
        public bool PinchPointDetected { get; set; }
        public string? PinchWarning { get; set; }

        // ── Mass transfer ──
        public double GasSideResistanceFraction { get; set; }
        public double LiquidSideResistanceFraction { get; set; }
        public string ControllingResistance { get; set; } = "";
        public double EnhancementFactorFromReaction { get; set; }

        // ── Reagent ──
        public double AbsorbedPollutantKmolPerHr { get; set; }
        public double StoichiometricReagentDemandKmolPerHr { get; set; }
        public double ReagentSuppliedKmolPerHr { get; set; }
        public double ExcessReagentFactor { get; set; }
        public double ReagentUtilizationFraction { get; set; }

        // ── Material balance ──
        public double ClosureErrorFraction { get; set; }
        public bool IsBalanced { get; set; }
        public string ClosureStatement { get; set; } = "";

        // ── Validity ──
        public int CriticalErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<string> CriticalErrors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> HiddenAssumptions { get; set; } = new();
    }
}