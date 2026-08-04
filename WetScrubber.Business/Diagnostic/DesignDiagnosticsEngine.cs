using System.Collections.Generic;

namespace WetScrubber.Business.Diagnostics
{
    /// <summary>
    /// Deterministic "symptom → diagnosis → recommendation" rule table for
    /// wet scrubber designs.
    ///
    /// This is the engineering equivalent of a doctor's reference
    /// guidelines: it does not invent anything per-design. It evaluates the
    /// numbers the calculation engine already produced against a fixed set
    /// of thresholds and returns whichever findings apply.
    ///
    /// IMPORTANT
    /// =========
    /// The thresholds below are a starting draft based on common scrubber
    /// design practice (see e.g. Perry's Chemical Engineers' Handbook,
    /// packed-tower and venturi design chapters). They should be reviewed
    /// and, where necessary, corrected by an engineer with domain
    /// expertise before this is relied on for real designs — the same way
    /// a hospital validates its clinical reference ranges rather than
    /// trusting a first draft blindly.
    ///
    /// No AI is involved anywhere in this class.
    /// </summary>
    public sealed class DesignDiagnosticsEngine : IDesignDiagnosticsEngine
    {
        // ── Thresholds (review with a scrubber design SME) ───────────
        private const double LowAbsorptionFactorThreshold = 1.2;

        // "little margin" = actual L/G within this multiple of the minimum
        private const double TightLGMarginMultiple = 1.15;

        // Typical upper bound for total pressure drop, by scrubber type (Pa).
        private const double PackedTowerPressureDropCeilingPa = 3000.0;
        private const double VenturiPressureDropCeilingPa = 8000.0;
        private const double SprayTowerPressureDropCeilingPa = 1500.0;

        // Fallback target when a design has no pollutant record with an
        // explicit TargetRemovalEfficiency (matches PollutantStream's own
        // default of 95%). Used only as a floor for the check — never
        // silently skip evaluating removal efficiency just because a
        // target wasn't set.
        private const double DefaultRemovalEfficiencyTargetPercent = 95.0;

        public IReadOnlyList<DesignFinding> Evaluate(DesignMetrics metrics)
        {
            var findings = new List<DesignFinding>();

            EvaluateStaleDiagnosticsData(metrics, findings);
            EvaluatePackingHeightPlausibility(metrics, findings);
            EvaluateAbsorptionFactor(metrics, findings);
            EvaluateLGMargin(metrics, findings);
            EvaluatePressureDrop(metrics, findings);
            EvaluateRemovalEfficiency(metrics, findings);

            return findings;
        }

        // Packed-tower designs always produce a nonzero AbsorptionFactor and
        // MinLGRatio when RunCalculation has actually run. If both are still
        // zero, this row predates the columns being persisted (or the design
        // was never recalculated after this feature shipped) — flag it
        // rather than silently reporting "no findings", which would read as
        // a clean bill of health instead of missing data.
        private static void EvaluateStaleDiagnosticsData(DesignMetrics m, List<DesignFinding> findings)
        {
            if (m.ScrubberType != "Packed Tower") return;

            if (m.AbsorptionFactor <= 0 && m.MinLGRatio <= 0)
            {
                findings.Add(new DesignFinding
                {
                    Code = "DIAGNOSTICS_DATA_STALE",
                    Severity = FindingSeverity.Info,
                    Symptom = "Absorption factor and minimum L/G ratio are both zero.",
                    Diagnosis = "This design has not been recalculated since diagnostic tracking was added, so the checks below could not run.",
                    Recommendation = "Re-run the calculation for this design, then regenerate the report."
                });
            }
        }

        // No real packed tower is built anywhere near this tall. If the
        // engine outputs one anyway, it almost always means the L/G ratio
        // is sitting right at (or past) the theoretical minimum — the
        // "pinch point" where infinite packing height is required for the
        // requested removal. This check is deliberately independent of
        // the AbsorptionFactor/L-G-margin rules above: if those rules
        // have a unit mismatch and miss the pinch condition, this one
        // still catches the physically-impossible result it produces.
        private const double MaxPlausiblePackingHeightM = 30.0;

        private static void EvaluatePackingHeightPlausibility(DesignMetrics m, List<DesignFinding> findings)
        {
            if (m.PackingHeight <= MaxPlausiblePackingHeightM) return;

            findings.Add(new DesignFinding
            {
                Code = "PACKING_HEIGHT_UNREALISTIC",
                Severity = FindingSeverity.Critical,
                Symptom = $"Calculated packing height is {m.PackingHeight:F1} m.",
                Diagnosis = "This exceeds any physically buildable packed tower and indicates the design is operating at or beyond the minimum L/G ratio (the absorption pinch point), where packing height requirements approach infinity.",
                Recommendation = "Increase the liquid-to-gas ratio well above the calculated minimum, or relax the target outlet concentration, then recalculate."
            });
        }

        private static void EvaluateAbsorptionFactor(DesignMetrics m, List<DesignFinding> findings)
        {
            if (m.AbsorptionFactor <= 0) return; // not applicable (e.g. venturi/spray tower)

            if (m.AbsorptionFactor < LowAbsorptionFactorThreshold)
            {
                findings.Add(new DesignFinding
                {
                    Code = "ABSORPTION_FACTOR_LOW",
                    Severity = FindingSeverity.Warning,
                    Symptom = $"Absorption factor is {m.AbsorptionFactor:F2}.",
                    Diagnosis = "Liquid flow is insufficient for reliable absorption.",
                    Recommendation = "Increase the L/G ratio or switch to a more reactive scrubbing liquid."
                });
            }
        }

        private static void EvaluateLGMargin(DesignMetrics m, List<DesignFinding> findings)
        {
            if (m.MinLGRatio <= 0 || m.ActualLGRatio <= 0) return; // not applicable

            if (m.ActualLGRatio < m.MinLGRatio * TightLGMarginMultiple)
            {
                findings.Add(new DesignFinding
                {
                    Code = "LG_MARGIN_TIGHT",
                    Severity = FindingSeverity.Warning,
                    Symptom = $"Actual L/G ratio ({m.ActualLGRatio:F2}) is close to the minimum required ({m.MinLGRatio:F2}).",
                    Diagnosis = "The design has little margin — flooding risk under upset conditions.",
                    Recommendation = "Increase the liquid flow rate or reduce the gas velocity."
                });
            }
        }

        private static void EvaluatePressureDrop(DesignMetrics m, List<DesignFinding> findings)
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
                    Symptom = $"Total pressure drop is {m.PressureDrop:F0} Pa, above the typical range for a {m.ScrubberType.ToLowerInvariant()} ({ceiling.Value:F0} Pa).",
                    Diagnosis = "Excess fan energy cost, with possible flooding risk.",
                    Recommendation = "Consider a larger tower diameter or lower-pressure-drop packing."
                });
            }
        }

        private static void EvaluateRemovalEfficiency(DesignMetrics m, List<DesignFinding> findings)
        {
            // Falls back to the conventional 95% removal target when no
            // pollutant record is available, rather than silently skipping
            // this check. A missing target must never be read as "this
            // design is fine" — that's the one failure mode this whole
            // engine exists to avoid.
            double target = (m.TargetRemovalEfficiency is double t && t > 0)
                ? t
                : DefaultRemovalEfficiencyTargetPercent;

            if (m.RemovalEfficiency < target)
            {
                findings.Add(new DesignFinding
                {
                    Code = "REMOVAL_EFFICIENCY_BELOW_TARGET",
                    Severity = FindingSeverity.Critical,
                    Symptom = $"Removal efficiency is {m.RemovalEfficiency:F2}%, below the target of {target:F2}%.",
                    Diagnosis = "The design will not meet its stated removal goal.",
                    Recommendation = "Increase NTU (taller packing) or increase the L/G ratio."
                });
            }
        }
    }
}