using System;
using WetScrubber.Business.Common;

namespace WetScrubber.Business.Plate
{
    /// <summary>
    /// Screening inputs for gas absorption across a bank of impingement plates.
    /// Concentrations are on a consistent gas-phase basis. KGa is an effective
    /// first-order volumetric transfer coefficient (1/s) and contact time is per plate.
    /// These inputs must be supported by a validated correlation or measured data before
    /// this calculation is used for final design.
    /// </summary>
    public sealed class PlateAbsorptionInput
    {
        public double InletGasConcentration { get; set; }
        public double EquilibriumGasConcentration { get; set; }
        public double OverallVolumetricMassTransferCoefficientPerS { get; set; }
        public double EffectiveContactTimePerPlateS { get; set; }
        public int NumberOfPlates { get; set; } = 1;
        public double? TargetRemovalFraction { get; set; }
    }

    /// <summary>Screening result for gas absorption over a plate bank.</summary>
    public sealed class PlateAbsorptionResult : ModelResultBase
    {
        public PlateAbsorptionResult()
        {
            Scope = ModelScope.GasAbsorptionOnly;
            ValidationStatus = ModelValidationStatus.NotYetValidated;
        }

        public double InletGasConcentration { get; set; }
        public double EquilibriumGasConcentration { get; set; }
        public double OutletGasConcentration { get; set; }
        public double OverallVolumetricMassTransferCoefficientPerS { get; set; }
        public double EffectiveContactTimePerPlateS { get; set; }
        public int NumberOfPlates { get; set; }
        public double TotalEffectiveContactTimeS { get; set; }
        public double NumberOfTransferUnits { get; set; }
        public double RemovalFraction { get; set; }
        public double RemovalPercent => 100.0 * RemovalFraction;
        public double? TargetRemovalFraction { get; set; }
        public bool? TargetMet { get; set; }
    }

    /// <summary>
    /// First-order, equilibrium-limited absorption screen:
    /// C_out = C_eq + (C_in - C_eq) exp(-KGa * t_contact * N_plates).
    /// This is a simplified NTU-style model, not a validated impingement-plate correlation.
    /// </summary>
    public static class ImpingementPlateAbsorption
    {
        public static PlateAbsorptionResult Evaluate(PlateAbsorptionInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            RequireFiniteNonNegative(nameof(input.InletGasConcentration), input.InletGasConcentration);
            RequireFiniteNonNegative(nameof(input.EquilibriumGasConcentration), input.EquilibriumGasConcentration);
            RequireFiniteNonNegative(nameof(input.OverallVolumetricMassTransferCoefficientPerS), input.OverallVolumetricMassTransferCoefficientPerS);
            RequireFiniteNonNegative(nameof(input.EffectiveContactTimePerPlateS), input.EffectiveContactTimePerPlateS);

            if (input.NumberOfPlates < 1)
                throw new ArgumentOutOfRangeException(nameof(input.NumberOfPlates), "Must be at least 1.");

            if (input.TargetRemovalFraction.HasValue &&
                (double.IsNaN(input.TargetRemovalFraction.Value) ||
                 double.IsInfinity(input.TargetRemovalFraction.Value) ||
                 input.TargetRemovalFraction.Value < 0.0 || input.TargetRemovalFraction.Value > 1.0))
            {
                throw new ArgumentOutOfRangeException(nameof(input.TargetRemovalFraction), "Must be between 0 and 1.");
            }

            double totalTime = input.EffectiveContactTimePerPlateS * input.NumberOfPlates;
            double ntu = input.OverallVolumetricMassTransferCoefficientPerS * totalTime;
            double inletExcess = input.InletGasConcentration - input.EquilibriumGasConcentration;
            double outlet = input.EquilibriumGasConcentration + inletExcess * Math.Exp(-ntu);

            // Removal is defined relative to the inlet-to-equilibrium driving range.
            // If there is no driving range, report zero removal rather than divide by zero.
            double removal = inletExcess > 0.0
                ? (input.InletGasConcentration - outlet) / inletExcess
                : 0.0;

            // Numerical protection against tiny floating-point excursions.
            removal = Math.Max(0.0, Math.Min(1.0, removal));

            var result = new PlateAbsorptionResult
            {
                InletGasConcentration = input.InletGasConcentration,
                EquilibriumGasConcentration = input.EquilibriumGasConcentration,
                OutletGasConcentration = outlet,
                OverallVolumetricMassTransferCoefficientPerS = input.OverallVolumetricMassTransferCoefficientPerS,
                EffectiveContactTimePerPlateS = input.EffectiveContactTimePerPlateS,
                NumberOfPlates = input.NumberOfPlates,
                TotalEffectiveContactTimeS = totalTime,
                NumberOfTransferUnits = ntu,
                RemovalFraction = removal,
                TargetRemovalFraction = input.TargetRemovalFraction,
                TargetMet = input.TargetRemovalFraction.HasValue
                    ? removal >= input.TargetRemovalFraction.Value
                    : (bool?)null
            };

            result.AddWarning("ABSORPTION_SCREENING_MODEL",
                "Absorption uses a first-order NTU screening model; KGa, contact time and equilibrium concentration require validated data for the target gas-liquid system.");

            if (input.InletGasConcentration <= input.EquilibriumGasConcentration)
            {
                result.AddWarning("NO_POSITIVE_ABSORPTION_DRIVING_FORCE",
                    "Inlet concentration is not above the specified equilibrium concentration; positive absorption removal is not predicted.");
            }

            if (input.OverallVolumetricMassTransferCoefficientPerS == 0.0 || input.EffectiveContactTimePerPlateS == 0.0)
            {
                result.AddWarning("ZERO_TRANSFER_EXPOSURE",
                    "The mass-transfer coefficient or contact time is zero, so the model predicts no absorption progress.");
            }

            return result;
        }

        private static void RequireFiniteNonNegative(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(name, value, "Must be a finite, non-negative number.");
        }
    }
}
