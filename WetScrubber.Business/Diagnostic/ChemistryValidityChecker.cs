using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Diagnostic
{
    /// <summary>
    /// Comprehensive sanity check on all chemistry calculations.
    /// Rejects impossible values instead of silently clamping them.
    /// 
    /// Section 16 of the chemistry checklist.
    /// </summary>
    public sealed class ChemistryValidityChecker
    {
        public sealed class ValidationIssue
        {
            public string Code { get; set; }  // e.g., "NEGATIVE_CONCENTRATION"
            public string Message { get; set; }
            public bool IsError { get; set; } // true = block calculation, false = warning
            public double? OffendingValue { get; set; }
            public string Parameter { get; set; }
        }

        public sealed class ValidationResult
        {
            public bool IsValid { get; set; }
            public IReadOnlyList<ValidationIssue> Issues { get; set; }
            public string SummaryStatement { get; set; }
        }

        /// <summary>
        /// Validate all gas-side thermodynamic values.
        /// </summary>
        public static ValidationResult ValidateGasPhase(
            double moleFractionPollutant,
            double gasMolecularWeightKgKmol,
            double gasDensityKgM3,
            double gasViscosityPas,
            double gasDiffusivityM2S,
            string pollutantCode = "")
        {
            var issues = new List<ValidationIssue>();

            // ── Mole fraction checks ──
            if (double.IsNaN(moleFractionPollutant))
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NAN_MOLE_FRACTION",
                    Message = $"Gas mole fraction is NaN",
                    IsError = true,
                    Parameter = "moleFractionPollutant"
                });
            }
            if (moleFractionPollutant < 0.0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_MOLE_FRACTION",
                    Message = $"Gas mole fraction cannot be negative (got {moleFractionPollutant:E6})",
                    IsError = true,
                    OffendingValue = moleFractionPollutant,
                    Parameter = "moleFractionPollutant"
                });
            }
            if (moleFractionPollutant > 1.0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "MOLE_FRACTION_EXCEEDS_UNITY",
                    Message = $"Gas mole fraction cannot exceed 1.0 (got {moleFractionPollutant:F6})",
                    IsError = true,
                    OffendingValue = moleFractionPollutant,
                    Parameter = "moleFractionPollutant"
                });
            }

            // ── Molecular weight ──
            if (gasMolecularWeightKgKmol <= 0.1)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_MOLECULAR_WEIGHT",
                    Message = $"Gas molecular weight implausible (got {gasMolecularWeightKgKmol:F2} kg/kmol)",
                    IsError = true,
                    OffendingValue = gasMolecularWeightKgKmol,
                    Parameter = "gasMolecularWeightKgKmol"
                });
            }

            // ── Density ──
            if (gasDensityKgM3 <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_DENSITY",
                    Message = $"Gas density must be positive (got {gasDensityKgM3:E6} kg/m³)",
                    IsError = true,
                    OffendingValue = gasDensityKgM3,
                    Parameter = "gasDensityKgM3"
                });
            }
            if (gasDensityKgM3 > 100) // unrealistic for typical gas at moderate P/T
            {
                issues.Add(new ValidationIssue
                {
                    Code = "IMPLAUSIBLE_DENSITY",
                    Message = $"Gas density seems very high (got {gasDensityKgM3:F2} kg/m³); check P, T",
                    IsError = false,
                    OffendingValue = gasDensityKgM3,
                    Parameter = "gasDensityKgM3"
                });
            }

            // ── Viscosity ──
            if (gasViscosityPas <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_VISCOSITY",
                    Message = $"Gas viscosity must be positive (got {gasViscosityPas:E6} Pa·s)",
                    IsError = true,
                    OffendingValue = gasViscosityPas,
                    Parameter = "gasViscosityPas"
                });
            }
            if (gasViscosityPas > 0.001)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "IMPLAUSIBLE_VISCOSITY",
                    Message = $"Gas viscosity unusually high (got {gasViscosityPas * 1e6:F1} µPa·s); check T",
                    IsError = false,
                    OffendingValue = gasViscosityPas,
                    Parameter = "gasViscosityPas"
                });
            }

            // ── Diffusivity ──
            if (gasDiffusivityM2S <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_DIFFUSIVITY",
                    Message = $"Gas diffusivity must be positive (got {gasDiffusivityM2S:E9} m²/s)",
                    IsError = true,
                    OffendingValue = gasDiffusivityM2S,
                    Parameter = "gasDiffusivityM2S"
                });
            }
            if (gasDiffusivityM2S > 1e-4)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "IMPLAUSIBLE_DIFFUSIVITY",
                    Message = $"Gas diffusivity seems very high (got {gasDiffusivityM2S:E9} m²/s)",
                    IsError = false,
                    OffendingValue = gasDiffusivityM2S,
                    Parameter = "gasDiffusivityM2S"
                });
            }

            return BuildResult(issues);
        }

        /// <summary>
        /// Validate liquid-side properties.
        /// </summary>
        public static ValidationResult ValidateLiquidPhase(
            double moleFractionSolute,
            double liquidDensityKgM3,
            double liquidViscosityPas,
            double liquidDiffusivityM2S,
            double surfaceTensionNM = -1)
        {
            var issues = new List<ValidationIssue>();

            // ── Mole fraction ──
            if (moleFractionSolute < 0.0 || moleFractionSolute > 1.0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_LIQUID_MOLE_FRACTION",
                    Message = $"Liquid mole fraction out of range (got {moleFractionSolute:F6})",
                    IsError = true,
                    OffendingValue = moleFractionSolute,
                    Parameter = "moleFractionSolute"
                });
            }

            // ── Density ──
            if (liquidDensityKgM3 <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_LIQUID_DENSITY",
                    Message = $"Liquid density must be positive (got {liquidDensityKgM3:E6} kg/m³)",
                    IsError = true,
                    OffendingValue = liquidDensityKgM3,
                    Parameter = "liquidDensityKgM3"
                });
            }
            if (liquidDensityKgM3 < 500 || liquidDensityKgM3 > 2000)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "IMPLAUSIBLE_LIQUID_DENSITY",
                    Message = $"Liquid density out of typical range (got {liquidDensityKgM3:F0} kg/m³)",
                    IsError = false,
                    OffendingValue = liquidDensityKgM3,
                    Parameter = "liquidDensityKgM3"
                });
            }

            // ── Viscosity ──
            if (liquidViscosityPas <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_LIQUID_VISCOSITY",
                    Message = $"Liquid viscosity must be positive (got {liquidViscosityPas:E6} Pa·s)",
                    IsError = true,
                    OffendingValue = liquidViscosityPas,
                    Parameter = "liquidViscosityPas"
                });
            }

            // ── Diffusivity ──
            if (liquidDiffusivityM2S <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_LIQUID_DIFFUSIVITY",
                    Message = $"Liquid diffusivity must be positive (got {liquidDiffusivityM2S:E12} m²/s)",
                    IsError = true,
                    OffendingValue = liquidDiffusivityM2S,
                    Parameter = "liquidDiffusivityM2S"
                });
            }

            // ── Surface tension (optional) ──
            if (surfaceTensionNM > 0 && (surfaceTensionNM < 0.01 || surfaceTensionNM > 1.0))
            {
                issues.Add(new ValidationIssue
                {
                    Code = "IMPLAUSIBLE_SURFACE_TENSION",
                    Message = $"Surface tension out of typical range (got {surfaceTensionNM:F3} N/m)",
                    IsError = false,
                    OffendingValue = surfaceTensionNM,
                    Parameter = "surfaceTensionNM"
                });
            }

            return BuildResult(issues);
        }

        /// <summary>
        /// Validate equilibrium and mass transfer parameters.
        /// </summary>
        public static ValidationResult ValidateEquilibriumAndMassTransfer(
            double henrysConstant,
            double henrysConstantConvention, // 1=liquid, 2=gas referenced
            double activityCoefficientLiquid,
            double kgaCoeffKmolM3HrKPa,
            double drivingForceYminusYstar,
            string modelName = "")
        {
            var issues = new List<ValidationIssue>();

            // ── Henry's constant ──
            if (henrysConstant <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_HENRY_CONSTANT",
                    Message = $"Henry's constant must be positive (got {henrysConstant:E6})",
                    IsError = true,
                    OffendingValue = henrysConstant,
                    Parameter = "henrysConstant"
                });
            }
            if (double.IsNaN(henrysConstant) || double.IsInfinity(henrysConstant))
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_HENRY_CONSTANT",
                    Message = $"Henry's constant is invalid ({henrysConstant})",
                    IsError = true,
                    OffendingValue = henrysConstant,
                    Parameter = "henrysConstant"
                });
            }

            // ── Convention ──
            if (henrysConstantConvention != 1 && henrysConstantConvention != 2)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "UNDEFINED_HENRY_CONVENTION",
                    Message = "Henry's law convention must be declared (1=liquid, 2=gas referenced)",
                    IsError = true,
                    Parameter = "henrysConstantConvention"
                });
            }

            // ── Activity coefficient ──
            if (activityCoefficientLiquid <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_ACTIVITY_COEFFICIENT",
                    Message = $"Activity coefficient must be positive (got {activityCoefficientLiquid:F6})",
                    IsError = true,
                    OffendingValue = activityCoefficientLiquid,
                    Parameter = "activityCoefficientLiquid"
                });
            }

            // ── Mass transfer coefficient ──
            if (kgaCoeffKmolM3HrKPa <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_KGA",
                    Message = $"KGa must be positive (got {kgaCoeffKmolM3HrKPa:E6} kmol/(m³·hr·kPa))",
                    IsError = true,
                    OffendingValue = kgaCoeffKmolM3HrKPa,
                    Parameter = "kgaCoeffKmolM3HrKPa"
                });
            }

            // ── Driving force ──
            if (drivingForceYminusYstar < -0.001)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_DRIVING_FORCE",
                    Message = $"Driving force (y - y*) must be non-negative (got {drivingForceYminusYstar:E6})",
                    IsError = true,
                    OffendingValue = drivingForceYminusYstar,
                    Parameter = "drivingForceYminusYstar"
                });
            }
            if (drivingForceYminusYstar < 1e-6 && drivingForceYminusYstar > -1e-6)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEAR_ZERO_DRIVING_FORCE",
                    Message = $"Driving force is near-zero; may indicate pinch condition (got {drivingForceYminusYstar:E9})",
                    IsError = false,
                    OffendingValue = drivingForceYminusYstar,
                    Parameter = "drivingForceYminusYstar"
                });
            }

            return BuildResult(issues);
        }

        /// <summary>
        /// Validate removal efficiency and material flow.
        /// </summary>
        public static ValidationResult ValidateRemovalAndFlows(
            double removalEfficiencyFraction,
            double removalEfficiencyPercent,
            double inletPollutantKmolPerHr,
            double outletGasPollutantKmolPerHr,
            double gasFlowKmolPerHr,
            double liquidFlowKmolPerHr,
            double lGRatio)
        {
            var issues = new List<ValidationIssue>();

            // ── Removal fraction ──
            if (removalEfficiencyFraction < 0.0 || removalEfficiencyFraction > 1.0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_REMOVAL_FRACTION",
                    Message = $"Removal efficiency must be in [0, 1] (got {removalEfficiencyFraction:F6})",
                    IsError = true,
                    OffendingValue = removalEfficiencyFraction,
                    Parameter = "removalEfficiencyFraction"
                });
            }

            // ── Removal percent ──
            if (removalEfficiencyPercent < 0.0 || removalEfficiencyPercent > 100.0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_REMOVAL_PERCENT",
                    Message = $"Removal efficiency must be in [0, 100]% (got {removalEfficiencyPercent:F2}%)",
                    IsError = true,
                    OffendingValue = removalEfficiencyPercent,
                    Parameter = "removalEfficiencyPercent"
                });
            }

            // ── Flow consistency ──
            if (inletPollutantKmolPerHr < 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_INLET_POLLUTANT",
                    Message = $"Inlet pollutant flow cannot be negative (got {inletPollutantKmolPerHr:E6} kmol/hr)",
                    IsError = true,
                    OffendingValue = inletPollutantKmolPerHr,
                    Parameter = "inletPollutantKmolPerHr"
                });
            }

            if (outletGasPollutantKmolPerHr < 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "NEGATIVE_OUTLET_POLLUTANT",
                    Message = $"Outlet pollutant flow cannot be negative (got {outletGasPollutantKmolPerHr:E6} kmol/hr)",
                    IsError = true,
                    OffendingValue = outletGasPollutantKmolPerHr,
                    Parameter = "outletGasPollutantKmolPerHr"
                });
            }

            if (outletGasPollutantKmolPerHr > inletPollutantKmolPerHr)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "OUTLET_EXCEEDS_INLET",
                    Message = $"Outlet pollutant cannot exceed inlet (inlet: {inletPollutantKmolPerHr:E6}, outlet: {outletGasPollutantKmolPerHr:E6})",
                    IsError = true,
                    Parameter = "outletGasPollutantKmolPerHr vs inletPollutantKmolPerHr"
                });
            }

            // ── Flow rates ──
            if (gasFlowKmolPerHr <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_GAS_FLOW",
                    Message = $"Gas flow must be positive (got {gasFlowKmolPerHr:E6} kmol/hr)",
                    IsError = true,
                    OffendingValue = gasFlowKmolPerHr,
                    Parameter = "gasFlowKmolPerHr"
                });
            }

            if (liquidFlowKmolPerHr <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_LIQUID_FLOW",
                    Message = $"Liquid flow must be positive (got {liquidFlowKmolPerHr:E6} kmol/hr)",
                    IsError = true,
                    OffendingValue = liquidFlowKmolPerHr,
                    Parameter = "liquidFlowKmolPerHr"
                });
            }

            if (lGRatio <= 0)
            {
                issues.Add(new ValidationIssue
                {
                    Code = "INVALID_LG_RATIO",
                    Message = $"L/G ratio must be positive (got {lGRatio:F6})",
                    IsError = true,
                    OffendingValue = lGRatio,
                    Parameter = "lGRatio"
                });
            }

            return BuildResult(issues);
        }

        /// <summary>
        /// Build final validation result.
        /// </summary>
        private static ValidationResult BuildResult(List<ValidationIssue> issues)
        {
            bool hasErrors = false;
            foreach (var issue in issues)
            {
                if (issue.IsError)
                {
                    hasErrors = true;
                    break;
                }
            }

            string summary;
            if (hasErrors)
            {
                int errorCount = 0;
                foreach (var issue in issues)
                    if (issue.IsError) errorCount++;
                summary = $"✗ VALIDATION FAILED: {errorCount} critical error(s)";
            }
            else if (issues.Count > 0)
            {
                int warningCount = 0;
                foreach (var issue in issues)
                    if (!issue.IsError) warningCount++;
                summary = $"⚠ {warningCount} warning(s) — calculation proceeds with caution";
            }
            else
            {
                summary = "✓ All validation checks passed";
            }

            return new ValidationResult
            {
                IsValid = !hasErrors,
                Issues = issues,
                SummaryStatement = summary
            };
        }
    }
}