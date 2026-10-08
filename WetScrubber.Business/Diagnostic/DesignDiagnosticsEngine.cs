using System.Collections.Generic;

namespace WetScrubber.Business.Diagnostics
{
    /// <summary>
    /// Deterministic "symptom → diagnosis → recommendation" rule table for
    /// wet scrubber designs.
    ///
    /// The calculation engine remains the source of record. This class only
    /// evaluates calculated values against deterministic engineering checks.
    ///
    /// No AI is involved anywhere in this class.
    /// </summary>
    public sealed class DesignDiagnosticsEngine : IDesignDiagnosticsEngine
    {
        private const double LowAbsorptionFactorThreshold = 1.2;

        // Actual L/G must be at least this multiple of the calculated minimum
        // before the design is considered to have the requested operating
        // margin.
        private const double TightLGMarginMultiple = 1.15;

        private const double PackedTowerPressureDropCeilingPa = 3000.0;
        private const double VenturiPressureDropCeilingPa = 8000.0;
        private const double SprayTowerPressureDropCeilingPa = 1500.0;

        private const double PackingHeightSuggestionMultiple = 1.5;
        private const double AbsorptionFactorSuggestionMargin = 1.05;
        private const double RemovalEfficiencySuggestionMargin = 1.05;

        private const string LiquidToGasRatioField = "LiquidToGasRatio";

        private const double MaxPlausiblePackingHeightM = 30.0;

        public IReadOnlyList<DesignFinding> Evaluate(DesignMetrics metrics)
        {
            var findings = new List<DesignFinding>();

            EvaluateStaleDiagnosticsData(metrics, findings);
            EvaluatePackingHeightPlausibility(metrics, findings);
            EvaluateAbsorptionFactor(metrics, findings);
            EvaluateLGMargin(metrics, findings);
            EvaluatePressureDrop(metrics, findings);
            EvaluateRemovalEfficiency(metrics, findings);
            EvaluateMaterialTemperature(metrics, findings);

            EvaluatePackingAndSlurryProvenance(metrics, findings);

            return findings;
        }

        private static void EvaluateStaleDiagnosticsData(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            if (m.ScrubberType != "Packed Tower")
                return;

            if (m.AbsorptionFactor <= 0 && m.MinLGRatio <= 0)
            {
                findings.Add(new DesignFinding
                {
                    Code = "DIAGNOSTICS_DATA_STALE",
                    Severity = FindingSeverity.Info,
                    Symptom = "Absorption factor and minimum L/G ratio are both zero.",
                    Diagnosis =
                        "This design has not been recalculated since diagnostic tracking was added, so the checks below could not run.",
                    Recommendation =
                        "Re-run the calculation for this design, then regenerate the report."
                });
            }
        }

        private static void EvaluatePackingHeightPlausibility(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            if (m.PackingHeight <= MaxPlausiblePackingHeightM)
                return;

            double? suggested = m.MinLGRatio > 0
                ? m.MinLGRatio * PackingHeightSuggestionMultiple
                : null;

            findings.Add(new DesignFinding
            {
                Code = "PACKING_HEIGHT_UNREALISTIC",
                Severity = FindingSeverity.Critical,
                Symptom = $"Calculated packing height is {m.PackingHeight:F1} m.",
                Diagnosis =
                    "This exceeds any physically buildable packed tower and indicates the design is operating at or beyond the minimum L/G ratio (the absorption pinch point), where packing height requirements approach infinity.",
                Recommendation =
                    "Increase the liquid-to-gas ratio well above the calculated minimum, or relax the target outlet concentration, then recalculate.",
                AffectedFields = new[] { LiquidToGasRatioField },
                SuggestedValue = suggested,
                SuggestedValueLabel = suggested is double s
                    ? $">= {s:F2} L/m³ gas (currently {m.ActualLGRatio:F2}; min. viable is {m.MinLGRatio:F2})"
                    : null
            });
        }

        private static void EvaluateAbsorptionFactor(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            if (m.AbsorptionFactor <= 0)
                return;

            if (m.AbsorptionFactor < LowAbsorptionFactorThreshold)
            {
                double? suggested = m.ActualLGRatio > 0
                    ? m.ActualLGRatio
                        * (LowAbsorptionFactorThreshold / m.AbsorptionFactor)
                        * AbsorptionFactorSuggestionMargin
                    : null;

                findings.Add(new DesignFinding
                {
                    Code = "ABSORPTION_FACTOR_LOW",
                    Severity = FindingSeverity.Warning,
                    Symptom = $"Absorption factor is {m.AbsorptionFactor:F2}.",
                    Diagnosis =
                        "The liquid-to-gas ratio is too low to provide a reliable absorption driving force at the calculated conditions.",
                    Recommendation =
                        "Increase the L/G ratio or switch to a more reactive scrubbing liquid.",
                    AffectedFields = new[] { LiquidToGasRatioField },
                    SuggestedValue = suggested,
                    SuggestedValueLabel = suggested is double s
                        ? $">= {s:F2} L/m³ gas (currently {m.ActualLGRatio:F2}) — approximate, re-run the calculation to confirm"
                        : null
                });
            }
        }

        private static void EvaluateLGMargin(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            if (m.MinLGRatio <= 0 || m.ActualLGRatio <= 0)
                return;

            double requiredMargin = m.MinLGRatio * TightLGMarginMultiple;

            if (m.ActualLGRatio < requiredMargin)
            {
                bool materiallyBelowMinimum =
                    m.ActualLGRatio < m.MinLGRatio;

                string symptom;
                string diagnosis;
                string recommendation;

                if (materiallyBelowMinimum)
                {
                    symptom =
                        $"Actual L/G ratio ({m.ActualLGRatio:F2}) is below the minimum required ({m.MinLGRatio:F2}).";

                    diagnosis =
                        "The liquid flow is substantially below the calculated minimum required for the stated absorption duty. The design does not have sufficient liquid-to-gas capacity to support the requested performance.";

                    recommendation =
                        "Increase the liquid flow rate to above the calculated minimum and re-run the calculation. Do not treat this condition as a flooding diagnosis.";
                }
                else
                {
                    symptom =
                        $"Actual L/G ratio ({m.ActualLGRatio:F2}) is close to the minimum required ({m.MinLGRatio:F2}).";

                    diagnosis =
                        "The design has limited operating margin above the calculated minimum liquid-to-gas ratio.";

                    recommendation =
                        "Increase the liquid flow rate or otherwise provide additional operating margin, then re-run the calculation.";
                }

                findings.Add(new DesignFinding
                {
                    Code = "LG_MARGIN_TIGHT",
                    Severity = FindingSeverity.Warning,
                    Symptom = symptom,
                    Diagnosis = diagnosis,
                    Recommendation = recommendation,
                    AffectedFields = new[] { LiquidToGasRatioField },
                    SuggestedValue = requiredMargin,
                    SuggestedValueLabel =
                        $">= {requiredMargin:F2} L/m³ gas (currently {m.ActualLGRatio:F2}; min. viable is {m.MinLGRatio:F2})"
                });
            }
        }

        private static void EvaluatePressureDrop(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            double? ceiling = m.ScrubberType switch
            {
                "Packed Tower" => PackedTowerPressureDropCeilingPa,
                "Venturi Scrubber" => VenturiPressureDropCeilingPa,
                "Spray Tower" => SprayTowerPressureDropCeilingPa,
                _ => null
            };

            if (ceiling.HasValue && m.PressureDrop > ceiling.Value)
            {
                findings.Add(new DesignFinding
                {
                    Code = "PRESSURE_DROP_HIGH",
                    Severity = FindingSeverity.Warning,
                    Symptom =
                        $"Total pressure drop is {m.PressureDrop:F0} Pa, above the typical range for a {m.ScrubberType.ToLowerInvariant()} ({ceiling.Value:F0} Pa).",
                    Diagnosis =
                        "Excess fan energy cost, with possible hydraulic operating concerns.",
                    Recommendation =
                        "Consider a larger tower diameter or lower-pressure-drop packing."
                });
            }
        }

        private static void EvaluateRemovalEfficiency(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            if (!(m.TargetRemovalEfficiency is double t) || t <= 0)
                return;

            double target = t;

            if (m.RemovalEfficiency < target)
            {
                double? suggested =
                    (m.ActualLGRatio > 0 && m.RemovalEfficiency > 0)
                        ? m.ActualLGRatio
                            * (target / m.RemovalEfficiency)
                            * RemovalEfficiencySuggestionMargin
                        : null;

                findings.Add(new DesignFinding
                {
                    Code = "REMOVAL_EFFICIENCY_BELOW_TARGET",
                    Severity = FindingSeverity.Critical,
                    Symptom =
                        $"Removal efficiency is {m.RemovalEfficiency:F2}%, below the target of {target:F2}%.",
                    Diagnosis =
                        "The calculated mass-transfer performance is insufficient to meet the stated pollutant removal target.",
                    Recommendation =
                        "Increase NTU through additional effective packing height and/or increase the L/G ratio, then re-run the calculation.",
                    AffectedFields = new[] { LiquidToGasRatioField },
                    SuggestedValue = suggested,
                    SuggestedValueLabel = suggested is double s
                        ? $">= {s:F2} L/m³ gas (currently {m.ActualLGRatio:F2}) — approximate, re-run the calculation to confirm"
                        : null
                });
            }
        }

        // Maximum continuous service temperature (°C) per construction material.
        // Same limits as ScrubberPhysicalChecker.
        private static readonly System.Collections.Generic.Dictionary<string, double> MaxServiceTempC =
            new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["PP"] = 80,
                ["HDPE"] = 60,
                ["PVC"] = 60,
                ["FRP"] = 90,
                ["SS316"] = 400,
                ["HastelloyC"] = 600,
                ["CarbonSteel"] = 400
            };

        private static void EvaluateMaterialTemperature(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            CheckMaterial("Shell material", "ShellMaterial", m.ShellMaterial, m.InletTemperatureC, findings);
            CheckMaterial("Internal material", "InternalMaterial", m.InternalMaterial, m.InletTemperatureC, findings);
        }

        private static void CheckMaterial(
            string label,
            string field,
            string? material,
            double inletTempC,
            List<DesignFinding> findings)
        {
            if (string.IsNullOrWhiteSpace(material) || !MaxServiceTempC.TryGetValue(material, out double limit))
                return;

            if (inletTempC <= limit)
                return;

            findings.Add(new DesignFinding
            {
                Code = "MATERIAL_TEMPERATURE_EXCEEDED",
                Severity = FindingSeverity.Critical,
                Symptom = $"{label} {material} is rated to {limit:F0} °C but the inlet gas is {inletTempC:F0} °C.",
                Diagnosis =
                    "The inlet gas temperature exceeds the maximum continuous service temperature of this material, so it can soften, creep or fail.",
                Recommendation =
                    "Select a higher-rated material (e.g. SS316 or Hastelloy C) or add a quench or cooling stage upstream, then recalculate.",
                AffectedFields = new[] { field }
            });
        }

        private static void EvaluatePackingAndSlurryProvenance(
            DesignMetrics m,
            List<DesignFinding> findings)
        {
            if (m.ScrubberType == "Packed Tower" &&
                string.IsNullOrWhiteSpace(m.PackingCode))
            {
                findings.Add(new DesignFinding
                {
                    Code = "PACKING_SELECTION_MISSING",
                    Severity = FindingSeverity.Warning,
                    Symptom = "No packing selection is stored with this design.",
                    Diagnosis =
                        "The calculation used the legacy default packing, so the result cannot be traced to a selected catalog record.",
                    Recommendation =
                        "Select a packing material and re-run the calculation before engineering review."
                });
            }

            if (m.PackingSizingMethod?.Contains("nominal HETP") == true)
            {
                findings.Add(new DesignFinding
                {
                    Code = "STRUCTURED_PACKING_HETP_ESTIMATE",
                    Severity = FindingSeverity.Info,
                    Symptom =
                        "Structured-packing height was estimated from nominal HETP.",
                    Diagnosis =
                        "Nominal HETP is a vendor/application-specific performance value, not a universal correlation.",
                    Recommendation =
                        "Confirm HETP against the selected vendor's hydraulic and mass-transfer data at the design loads."
                });
            }

            if (m.IsLimestoneSlurry && m.SolidsLoadingWtPercent >= 25.0)
            {
                findings.Add(new DesignFinding
                {
                    Code = "LIMESTONE_SLURRY_HIGH_SOLIDS",
                    Severity = FindingSeverity.Warning,
                    Symptom =
                        $"Limestone solids loading is {m.SolidsLoadingWtPercent:F1} wt%.",
                    Diagnosis =
                        "High solids loading raises apparent viscosity and increases plugging, settling, and slurry-distribution risk.",
                    Recommendation =
                        "Verify recirculation velocity, agitator duty, nozzle passage size, and vendor fouling limits."
                });
            }
        }
    }
}