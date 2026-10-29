namespace WetScrubber.Business.Plate
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Preliminary impingement-plate scrubber hydraulic model.
    /// Uses SI units. Correlations are screening estimates and require
    /// validation against authoritative design data before production use.
    /// </summary>
    [Obsolete("Superseded by ImpingementPlateHydraulics.Evaluate; not used by the calculation engine.")]
    public sealed class ImpingementPlateModel
    {
        private const double RecommendedFloodPercent = ImpingementPlateHydraulics.RecommendedFloodPercent;
        private const double ProvisionalPressureDropLimitPa = ImpingementPlateHydraulics.DefaultPressureDropCeilingPa;
        private const double DefaultDischargeCoefficient = ImpingementPlateHydraulics.DefaultOrificeCoefficient;
        private const double DefaultPlateHoleAreaFraction = ImpingementPlateHydraulics.DefaultOpenAreaFraction;
        private const double DefaultLiquidDensityKgPerM3 = 1000.0;
        private const double DefaultGasDensityKgPerM3 = 1.2;
        private const double DefaultGravityMPerS2 = 9.80665;

        /// <summary>
        /// Calculates preliminary column hydraulics and returns a result DTO.
        /// Flow rates are in m³/s, diameter in m, and densities in kg/m³.
        /// </summary>
        public ImpingementPlateResult Calculate(
            int numberOfStages,
            double columnDiameterM,
            double gasFlowM3PerS,
            double liquidFlowM3PerS,
            double gasDensityKgPerM3 = DefaultGasDensityKgPerM3,
            double liquidDensityKgPerM3 = DefaultLiquidDensityKgPerM3,
            double? removalEfficiencyPercent = null,
            double? cutDiameterMicron = null,
            double plateHoleAreaFraction = DefaultPlateHoleAreaFraction,
            double dischargeCoefficient = DefaultDischargeCoefficient,
            bool isPreview = true)
        {
            ValidatePositive(numberOfStages, nameof(numberOfStages));
            ValidatePositive(columnDiameterM, nameof(columnDiameterM));
            ValidateNonNegative(gasFlowM3PerS, nameof(gasFlowM3PerS));
            ValidateNonNegative(liquidFlowM3PerS, nameof(liquidFlowM3PerS));
            ValidatePositive(gasDensityKgPerM3, nameof(gasDensityKgPerM3));
            ValidatePositive(liquidDensityKgPerM3, nameof(liquidDensityKgPerM3));
            ValidateFraction(plateHoleAreaFraction, nameof(plateHoleAreaFraction));
            ValidatePositive(dischargeCoefficient, nameof(dischargeCoefficient));

            if (plateHoleAreaFraction <= 0.0 || plateHoleAreaFraction >= 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(plateHoleAreaFraction),
                    "Hole area fraction must be greater than zero and less than one.");
            }

            if (dischargeCoefficient > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dischargeCoefficient),
                    "Discharge coefficient must not exceed one.");
            }

            double efficiency = removalEfficiencyPercent ?? 0.0;

            if (!IsFinite(efficiency) || efficiency < 0.0 || efficiency > 100.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(removalEfficiencyPercent),
                    "Removal efficiency must be between 0 and 100 percent.");
            }

            if (cutDiameterMicron.HasValue &&
                (!IsFinite(cutDiameterMicron.Value) || cutDiameterMicron.Value <= 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cutDiameterMicron),
                    "Cut diameter must be finite and greater than zero.");
            }

            double columnAreaM2 = Math.PI * columnDiameterM * columnDiameterM / 4.0;
            double holeAreaM2 = columnAreaM2 * plateHoleAreaFraction;

            double superficialGasVelocityMPerS =
                gasFlowM3PerS / columnAreaM2;

            double holeGasVelocityMPerS =
                gasFlowM3PerS / holeAreaM2;

            // Idealized orifice estimate; excludes tray-specific dry/wet
            // pressure-drop corrections and liquid-head effects.
            double dryPlatePressureDropPa =
                0.5 * gasDensityKgPerM3
                * Math.Pow(holeGasVelocityMPerS / dischargeCoefficient, 2.0);

            // Screening estimate only: actual liquid head and plate geometry
            // should be modeled using validated tray correlations.
            double liquidHeadPressureDropPa =
                liquidDensityKgPerM3 * DefaultGravityMPerS2
                * (liquidFlowM3PerS / Math.Max(columnAreaM2, 1e-12))
                * 0.01;

            double pressureDropPerStagePa =
                dryPlatePressureDropPa + liquidHeadPressureDropPa;

            double totalPressureDropPa =
                pressureDropPerStagePa * numberOfStages;

            // Provisional flooding estimate based on an assumed maximum
            // superficial velocity. This is not a substitute for a validated
            // capacity-factor / tray-spacing correlation.
            double floodingVelocityMPerS =
                CalculateProvisionalFloodingVelocity(
                    gasDensityKgPerM3,
                    liquidDensityKgPerM3);

            double percentFlood = floodingVelocityMPerS > 0.0
                ? 100.0 * superficialGasVelocityMPerS / floodingVelocityMPerS
                : double.PositiveInfinity;

            var warnings = new List<string>();

            if (percentFlood > RecommendedFloodPercent)
            {
                warnings.Add(
                    $"Flooding is {percentFlood:F1}%, above the recommended {RecommendedFloodPercent:F0}% screening limit.");
            }

            if (totalPressureDropPa > ProvisionalPressureDropLimitPa)
            {
                warnings.Add(
                    $"Estimated pressure drop is {totalPressureDropPa:F0} Pa, above the provisional {ProvisionalPressureDropLimitPa:F0} Pa screening limit.");
            }

            if (gasFlowM3PerS == 0.0)
            {
                warnings.Add("Gas flow is zero; hydraulic results may not represent operating conditions.");
            }

            if (liquidFlowM3PerS == 0.0)
            {
                warnings.Add("Liquid flow is zero; wet-plate operation and collection performance are not established.");
            }

            var notes = new List<string>
            {
                "Pressure drop uses an idealized orifice estimate plus a provisional liquid-head allowance per stage.",
                "Flooding velocity is a provisional density-based screening estimate, not a validated impingement-plate capacity correlation.",
                "Removal efficiency is passed through from the caller; it is not predicted by this hydraulic model."
            };

            return new ImpingementPlateResult
            {
                IsFeasible = percentFlood <= 100.0,
                IsPreview = isPreview,
                NumberOfStages = numberOfStages,
                ColumnDiameterM = columnDiameterM,
                ActiveAreaM2 = columnAreaM2,
                GasVolumetricFlowM3PerS = gasFlowM3PerS,
                LiquidVolumetricFlowM3PerS = liquidFlowM3PerS,
                SuperficialGasVelocityMPerS = superficialGasVelocityMPerS,
                FloodingVelocityMPerS = floodingVelocityMPerS,
                PercentFlood = percentFlood,
                PressureDropPa = totalPressureDropPa,
                RemovalEfficiencyPercent = efficiency,
                CutDiameterMicron = cutDiameterMicron,
                Warnings = warnings.AsReadOnly(),
                CalculationNotes = notes.AsReadOnly()
            };
        }

        private static double CalculateProvisionalFloodingVelocity(
            double gasDensityKgPerM3,
            double liquidDensityKgPerM3)
        {
            // Simplified Souders–Brown-style screening estimate.
            // The capacity constant is provisional and must be calibrated.
            const double provisionalCapacityFactorMPerS = ImpingementPlateHydraulics.DefaultFloodCapacityFactorMs;

            double densityTerm =
                (liquidDensityKgPerM3 - gasDensityKgPerM3)
                / gasDensityKgPerM3;

            if (densityTerm <= 0.0)
            {
                throw new ArgumentException(
                    "Liquid density must exceed gas density.");
            }

            return provisionalCapacityFactorMPerS * Math.Sqrt(densityTerm);
        }

        private static void ValidatePositive(int value, string parameterName)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Value must be greater than zero.");
            }
        }

        private static void ValidatePositive(double value, string parameterName)
        {
            if (!IsFinite(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Value must be finite and greater than zero.");
            }
        }

        private static void ValidateNonNegative(double value, string parameterName)
        {
            if (!IsFinite(value) || value < 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Value must be finite and non-negative.");
            }
        }

        private static void ValidateFraction(double value, string parameterName)
        {
            if (!IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Value must be finite.");
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}