using WetScrubber.Database.Enums;
using WetScrubber.Plugins;

namespace WetScrubber.Services
{
    public enum CheckStatus
    {
        Pass,
        Warn,
        Fail
    }

    public sealed record PhysicalCheck(
        string Name,
        CheckStatus Status,
        string Parameter,
        string Detail);

    public class ScrubberPhysicalChecker
    {
        // Maximum continuous service temperature (°C) per construction material.
        private static readonly Dictionary<ConstructionMaterial, double> MaxServiceTempC = new()
        {
            [ConstructionMaterial.PP] = 80,
            [ConstructionMaterial.HDPE] = 60,
            [ConstructionMaterial.PVC] = 60,
            [ConstructionMaterial.FRP] = 90,
            [ConstructionMaterial.SS316] = 400,
            [ConstructionMaterial.HastelloyC] = 600,
            [ConstructionMaterial.CarbonSteel] = 400
        };

        public List<PhysicalCheck> Evaluate(WetScrubberDraftState draft, CalculationResult result)
        {
            var checks = new List<PhysicalCheck>();

            CheckFlooding(result, checks);
            CheckGasVelocity(result, checks);
            CheckPressureDropPerMeter(result, checks);
            CheckAbsorptionFactor(result, checks);
            CheckLiquidGasRatio(result, checks);
            CheckRemovalTarget(draft, result, checks);
            CheckMaterialTemperature("Shell material", draft.ShellMaterial, draft.InletTemperature, "ShellMaterial", checks);
            CheckMaterialTemperature("Internal material", draft.InternalMaterial, draft.InletTemperature, "InternalMaterial", checks);

            return checks;
        }

        private static void CheckFlooding(CalculationResult r, List<PhysicalCheck> checks)
        {
            if (r.PercentFlood <= 0)
            {
                return;
            }

            if (r.PercentFlood > 80)
            {
                checks.Add(new PhysicalCheck("Flooding", CheckStatus.Fail, "LiquidToGasRatio / TowerDiameter",
                    $"Operating at {r.PercentFlood:F1}% of flooding velocity (limit 80%)."));
            }
            else if (r.PercentFlood > 70 || r.ExceedsRecommendedFlood)
            {
                checks.Add(new PhysicalCheck("Flooding", CheckStatus.Warn, "TowerDiameter",
                    $"Operating at {r.PercentFlood:F1}% of flooding velocity (recommended maximum 70%)."));
            }
            else
            {
                checks.Add(new PhysicalCheck("Flooding", CheckStatus.Pass, "TowerDiameter",
                    $"Operating at {r.PercentFlood:F1}% of flooding velocity."));
            }
        }

        private static void CheckGasVelocity(CalculationResult r, List<PhysicalCheck> checks)
        {
            if (r.ScrubberType != "Packed Tower" || r.GasVelocity <= 0)
            {
                return;
            }

            if (r.GasVelocity < 0.5 || r.GasVelocity > 3.5)
            {
                checks.Add(new PhysicalCheck("Gas velocity", CheckStatus.Warn, "TowerDiameter",
                    $"Superficial gas velocity {r.GasVelocity:F2} m/s is outside the typical 0.5-3.5 m/s range."));
            }
            else
            {
                checks.Add(new PhysicalCheck("Gas velocity", CheckStatus.Pass, "TowerDiameter",
                    $"Superficial gas velocity {r.GasVelocity:F2} m/s is within the typical range."));
            }
        }

        private static void CheckPressureDropPerMeter(CalculationResult r, List<PhysicalCheck> checks)
        {
            if (r.PackingHeight <= 0 || r.PressureDrop <= 0)
            {
                return;
            }

            var perMeter = r.PressureDrop / r.PackingHeight;

            if (perMeter > 1000)
            {
                checks.Add(new PhysicalCheck("Pressure drop per metre", CheckStatus.Fail, "PackingCode / TowerDiameter",
                    $"{perMeter:F0} Pa/m exceeds 1000 Pa/m."));
            }
            else if (perMeter > 500)
            {
                checks.Add(new PhysicalCheck("Pressure drop per metre", CheckStatus.Warn, "PackingCode / TowerDiameter",
                    $"{perMeter:F0} Pa/m is above the typical 500 Pa/m."));
            }
            else
            {
                checks.Add(new PhysicalCheck("Pressure drop per metre", CheckStatus.Pass, "PackingCode",
                    $"{perMeter:F0} Pa/m is within the typical range."));
            }
        }

        private static void CheckAbsorptionFactor(CalculationResult r, List<PhysicalCheck> checks)
        {
            if (r.AbsorptionFactor <= 0)
            {
                return;
            }

            if (r.AbsorptionFactor < 1.0)
            {
                checks.Add(new PhysicalCheck("Absorption factor", CheckStatus.Fail, "LiquidToGasRatio",
                    $"Absorption factor {r.AbsorptionFactor:F2} is below 1.0; high removal is not achievable at this L/G."));
            }
            else if (r.AbsorptionFactor < 1.25)
            {
                checks.Add(new PhysicalCheck("Absorption factor", CheckStatus.Warn, "LiquidToGasRatio",
                    $"Absorption factor {r.AbsorptionFactor:F2} is below the recommended 1.25-2.0 range."));
            }
            else
            {
                checks.Add(new PhysicalCheck("Absorption factor", CheckStatus.Pass, "LiquidToGasRatio",
                    $"Absorption factor {r.AbsorptionFactor:F2} is acceptable."));
            }
        }

        private static void CheckLiquidGasRatio(CalculationResult r, List<PhysicalCheck> checks)
        {
            if (r.MinLGRatio <= 0 || r.ActualLGRatio <= 0)
            {
                return;
            }

            var margin = r.ActualLGRatio / r.MinLGRatio;

            if (margin < 1.0)
            {
                checks.Add(new PhysicalCheck("L/G margin", CheckStatus.Fail, "LiquidToGasRatio",
                    $"Actual L/G {r.ActualLGRatio:F2} is below the minimum {r.MinLGRatio:F2}."));
            }
            else if (margin < 1.2)
            {
                checks.Add(new PhysicalCheck("L/G margin", CheckStatus.Warn, "LiquidToGasRatio",
                    $"Actual L/G is only {margin:F2}x the minimum; 1.2-1.5x is recommended."));
            }
            else
            {
                checks.Add(new PhysicalCheck("L/G margin", CheckStatus.Pass, "LiquidToGasRatio",
                    $"Actual L/G is {margin:F2}x the minimum."));
            }
        }

        private static void CheckRemovalTarget(WetScrubberDraftState d, CalculationResult r, List<PhysicalCheck> checks)
        {
            if (r.RemovalEfficiency <= 0)
            {
                return;
            }

            if (r.RemovalEfficiency + 0.5 < d.TargetRemovalEfficiency)
            {
                checks.Add(new PhysicalCheck("Removal target", CheckStatus.Fail, "LiquidToGasRatio / PackingCode",
                    $"Predicted removal {r.RemovalEfficiency:F2}% is below the target {d.TargetRemovalEfficiency:F2}%."));
            }
            else
            {
                checks.Add(new PhysicalCheck("Removal target", CheckStatus.Pass, "TargetRemovalEfficiency",
                    $"Predicted removal {r.RemovalEfficiency:F2}% meets the target {d.TargetRemovalEfficiency:F2}%."));
            }
        }

        private static void CheckMaterialTemperature(
            string label,
            ConstructionMaterial material,
            double? gasTempC,
            string parameter,
            List<PhysicalCheck> checks)
        {
            if (gasTempC is null || !MaxServiceTempC.TryGetValue(material, out var limit))
            {
                return;
            }

            if (gasTempC.Value > limit)
            {
                checks.Add(new PhysicalCheck($"{label} temperature limit", CheckStatus.Fail, parameter,
                    $"{material} is rated to about {limit:F0} °C but inlet gas is {gasTempC.Value:F0} °C."));
            }
            else if (gasTempC.Value > limit * 0.9)
            {
                checks.Add(new PhysicalCheck($"{label} temperature limit", CheckStatus.Warn, parameter,
                    $"Inlet gas {gasTempC.Value:F0} °C is within 10% of the {material} limit of {limit:F0} °C."));
            }
            else
            {
                checks.Add(new PhysicalCheck($"{label} temperature limit", CheckStatus.Pass, parameter,
                    $"{material} rated to about {limit:F0} °C; inlet gas is {gasTempC.Value:F0} °C."));
            }
        }
    }
}