namespace WetScrubber.Business.Plate
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Result contract for an impingement-plate scrubber calculation.
    /// Values use SI units unless specified otherwise.
    /// </summary>
    public sealed class ImpingementPlateResult
    {
        public bool IsFeasible { get; init; }

        public bool IsPreview { get; init; } = true;

        public int NumberOfStages { get; init; }

        public double ColumnDiameterM { get; init; }

        public double ActiveAreaM2 { get; init; }

        public double GasVolumetricFlowM3PerS { get; init; }

        public double LiquidVolumetricFlowM3PerS { get; init; }

        public double SuperficialGasVelocityMPerS { get; init; }

        public double FloodingVelocityMPerS { get; init; }

        public double PercentFlood { get; init; }

        public double PressureDropPa { get; init; }

        public double RemovalEfficiencyPercent { get; init; }

        public double? CutDiameterMicron { get; init; }

        public IReadOnlyList<string> Warnings { get; init; }
            = Array.Empty<string>();

        public IReadOnlyList<string> CalculationNotes { get; init; }
            = Array.Empty<string>();

        public bool HasWarnings =>
            Warnings != null && Warnings.Count > 0;

        /// <summary>
        /// Creates a result and calculates column area, superficial gas
        /// velocity, and percent flooding from the supplied inputs.
        /// </summary>
        public static ImpingementPlateResult Create(
            int numberOfStages,
            double columnDiameterM,
            double gasVolumetricFlowM3PerS,
            double liquidVolumetricFlowM3PerS,
            double floodingVelocityMPerS,
            double pressureDropPa,
            double removalEfficiencyPercent,
            double? cutDiameterMicron = null,
            bool isPreview = true,
            IEnumerable<string>? warnings = null,
            IEnumerable<string>? calculationNotes = null)
        {
            RequireFinitePositive(
                columnDiameterM,
                nameof(columnDiameterM));

            RequireFiniteNonNegative(
                gasVolumetricFlowM3PerS,
                nameof(gasVolumetricFlowM3PerS));

            RequireFiniteNonNegative(
                liquidVolumetricFlowM3PerS,
                nameof(liquidVolumetricFlowM3PerS));

            RequireFinitePositive(
                floodingVelocityMPerS,
                nameof(floodingVelocityMPerS));

            RequireFiniteNonNegative(
                pressureDropPa,
                nameof(pressureDropPa));

            if (numberOfStages < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numberOfStages),
                    "At least one plate stage is required.");
            }

            if (!IsFinite(removalEfficiencyPercent) ||
                removalEfficiencyPercent < 0.0 ||
                removalEfficiencyPercent > 100.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(removalEfficiencyPercent),
                    "Removal efficiency must be between 0 and 100 percent.");
            }

            if (cutDiameterMicron.HasValue &&
                (!IsFinite(cutDiameterMicron.Value) ||
                 cutDiameterMicron.Value <= 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cutDiameterMicron),
                    "Cut diameter must be finite and greater than zero.");
            }

            double activeAreaM2 =
                Math.PI * columnDiameterM * columnDiameterM / 4.0;

            if (!IsFinite(activeAreaM2) || activeAreaM2 <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columnDiameterM),
                    "Column area is outside the supported numeric range.");
            }

            double superficialGasVelocityMPerS =
                gasVolumetricFlowM3PerS / activeAreaM2;

            double percentFlood =
                100.0 * superficialGasVelocityMPerS
                / floodingVelocityMPerS;

            var resultWarnings = warnings?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList()
                ?? new List<string>();

            var notes = calculationNotes?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList()
                ?? new List<string>();

            notes.Add(
                "Active area is calculated from the circular column diameter.");

            notes.Add(
                "Superficial gas velocity equals gas flow divided by column cross-sectional area.");

            notes.Add(
                "Percent flooding equals superficial gas velocity divided by flooding velocity, multiplied by 100.");

            if (isPreview)
            {
                notes.Add(
                    "This result is a preview and requires validation against benchmark data.");
            }

            if (percentFlood > 70.0)
            {
                resultWarnings.Add(
                    "Flooding exceeds the recommended 70% screening limit.");
            }

            if (percentFlood > 100.0)
            {
                resultWarnings.Add(
                    "Calculated gas velocity exceeds the supplied flooding velocity.");
            }

            if (pressureDropPa > 3000.0)
            {
                resultWarnings.Add(
                    "Pressure drop exceeds the provisional 3000 Pa screening limit.");
            }

            if (gasVolumetricFlowM3PerS == 0.0)
            {
                resultWarnings.Add(
                    "Gas flow is zero; hydraulic performance may not represent an operating condition.");
            }

            return new ImpingementPlateResult
            {
                IsFeasible = percentFlood <= 100.0,

                IsPreview = isPreview,

                NumberOfStages = numberOfStages,

                ColumnDiameterM = columnDiameterM,

                ActiveAreaM2 = activeAreaM2,

                GasVolumetricFlowM3PerS =
                    gasVolumetricFlowM3PerS,

                LiquidVolumetricFlowM3PerS =
                    liquidVolumetricFlowM3PerS,

                SuperficialGasVelocityMPerS =
                    superficialGasVelocityMPerS,

                FloodingVelocityMPerS =
                    floodingVelocityMPerS,

                PercentFlood = percentFlood,

                PressureDropPa = pressureDropPa,

                RemovalEfficiencyPercent =
                    removalEfficiencyPercent,

                CutDiameterMicron = cutDiameterMicron,

                Warnings = resultWarnings.AsReadOnly(),

                CalculationNotes = notes.AsReadOnly()
            };
        }

        private static void RequireFinitePositive(
            double value,
            string parameterName)
        {
            if (!IsFinite(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Value must be finite and greater than zero.");
            }
        }

        private static void RequireFiniteNonNegative(
            double value,
            string parameterName)
        {
            if (!IsFinite(value) || value < 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Value must be finite and non-negative.");
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value)
                && !double.IsInfinity(value);
        }
    }
}