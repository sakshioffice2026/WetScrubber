using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WetScrubber.Database;
using WetScrubber.Database.Enums;

namespace WetScrubber.Plugins
{
    public sealed class OptimizationCandidate
    {
        public string PackingCode { get; init; } = string.Empty;
        public double LiquidToGasRatio { get; init; }
        public double TotalPowerKW { get; init; }
        public double TowerDiameterM { get; init; }
        public double TowerHeightM { get; init; }
        public double PressureDropPa { get; init; }
        public double PercentFlood { get; init; }
        public double RemovalPct { get; init; }
        public double LiquidLoadingM3M2Hr { get; init; }
        public double RequiredLoadingM3M2Hr { get; init; }
        public int Fails { get; init; }
        public int Warns { get; init; }
        public string CalculationJson { get; init; } = "{}";
        public string ChecksJson { get; init; } = "[]";

        public double TowerVolumeM3 =>
            Math.PI * TowerDiameterM * TowerDiameterM / 4.0 * TowerHeightM;
    }

    public sealed class OptimizationOutcome
    {
        public OptimizationCandidate? Baseline { get; init; }
        public OptimizationCandidate? Best { get; init; }
        public int Evaluated { get; init; }
        public bool BestPassesAllChecks { get; init; }
        public bool BaselineIsBest { get; init; }
    }

    public class ScrubberOptimizerPlugin
    {
        private const int MaxPackings = 12;
        private const int MaxEvaluations = 160;
        private const double MinWettingRatePerArea = 0.08; // m3/m.h per m2/m3 of packing area

        private static readonly double[] LiquidToGasGrid =
            { 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };

        private static readonly HashSet<ConstructionMaterial> PlasticMaterials = new()
        {
            ConstructionMaterial.PP,
            ConstructionMaterial.HDPE,
            ConstructionMaterial.PVC,
            ConstructionMaterial.FRP
        };

        private readonly ApplicationDbContext _db;
        private readonly ScrubberDesignPlugin _design;

        public ScrubberOptimizerPlugin(ApplicationDbContext db, ScrubberDesignPlugin design)
        {
            _db = db;
            _design = design;
        }

        public async Task<OptimizationOutcome> OptimizeAsync(
            WetScrubberDraftState draft,
            CancellationToken ct = default)
        {
            var areas = await _db.Packings
                .AsNoTracking()
                .Where(p => p.IsActive)
                .ToDictionaryAsync(p => p.Code, p => p.SpecificAreaM2M3, StringComparer.OrdinalIgnoreCase, ct);

            var baseline = await EvaluateAsync(Clone(draft, draft.PackingCode, draft.LiquidToGasRatio), areas, ct);

            var packingCodes = await GetPackingCodesAsync(draft, ct);
            var lgValues = LiquidToGasGrid
                .Append(draft.LiquidToGasRatio)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var candidates = new List<OptimizationCandidate>();
            var evaluated = 0;

            if (baseline is not null)
            {
                candidates.Add(baseline);
                evaluated++;
            }

            foreach (var code in packingCodes)
            {
                foreach (var lg in lgValues)
                {
                    ct.ThrowIfCancellationRequested();

                    if (evaluated >= MaxEvaluations)
                    {
                        break;
                    }

                    if (baseline is not null &&
                        string.Equals(code, baseline.PackingCode, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(lg - baseline.LiquidToGasRatio) < 1e-9)
                    {
                        continue;
                    }

                    var candidate = await EvaluateAsync(Clone(draft, code, lg), areas, ct);
                    evaluated++;

                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            var best = candidates
                .OrderBy(c => c.Fails)
                .ThenBy(c => c.Warns)
                .ThenBy(c => c.TotalPowerKW)
                .ThenBy(c => c.TowerVolumeM3)
                .FirstOrDefault();

            return new OptimizationOutcome
            {
                Baseline = baseline,
                Best = best,
                Evaluated = evaluated,
                BestPassesAllChecks = best is not null && best.Fails == 0 && best.Warns == 0,
                BaselineIsBest = best is not null && baseline is not null &&
                                 ReferenceEquals(best, baseline)
            };
        }

        private async Task<List<string>> GetPackingCodesAsync(WetScrubberDraftState draft, CancellationToken ct)
        {
            var rows = await _db.Packings
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new { p.Code, p.Material })
                .ToListAsync(ct);

            var wantPlastic = PlasticMaterials.Contains(draft.InternalMaterial);
            var wanted = wantPlastic ? "plastic" : "metal";

            var matching = rows
                .Where(r => string.Equals(r.Material?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Code)
                .ToList();

            var codes = matching.Count > 0 ? matching : rows.Select(r => r.Code).ToList();

            return codes.Take(MaxPackings).ToList();
        }

        private async Task<OptimizationCandidate?> EvaluateAsync(
            WetScrubberDraftState trial,
            IReadOnlyDictionary<string, double> packingAreas,
            CancellationToken ct)
        {
            var computation = await _design.ComputeAsync(trial, ct);

            try
            {
                using var calc = JsonDocument.Parse(computation.CalculationJson);
                var root = calc.RootElement;

                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                {
                    return null;
                }

                var fails = 0;
                var warns = 0;

                using var checks = JsonDocument.Parse(computation.ChecksJson);
                if (checks.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var check in checks.RootElement.EnumerateArray())
                    {
                        var status = check.TryGetProperty("status", out var st) ? st.GetString() : null;

                        if (string.Equals(status, "FAIL", StringComparison.OrdinalIgnoreCase)) fails++;
                        else if (string.Equals(status, "WARN", StringComparison.OrdinalIgnoreCase)) warns++;
                    }
                }

                var packingUsed = root.TryGetProperty("packingUsed", out var pu) ? pu.GetString() ?? trial.PackingCode : trial.PackingCode;
                var diameter = Num(root, "towerDiameterM");
                var liquidFlow = Num(root, "liquidFlowRateM3Hr");
                var area = diameter > 0 ? Math.PI * diameter * diameter / 4.0 : 0.0;
                var loading = area > 0 ? liquidFlow / area : 0.0;
                var required = packingAreas.TryGetValue(packingUsed, out var aT) ? MinWettingRatePerArea * aT : 0.0;

                if (required > 0 && loading < required)
                {
                    fails++;
                }

                return new OptimizationCandidate
                {
                    LiquidLoadingM3M2Hr = loading,
                    RequiredLoadingM3M2Hr = required,
                    PackingCode = root.TryGetProperty("packingUsed", out var pk) ? pk.GetString() ?? trial.PackingCode : trial.PackingCode,
                    LiquidToGasRatio = trial.LiquidToGasRatio,
                    TotalPowerKW = Num(root, "totalPowerKW"),
                    TowerDiameterM = Num(root, "towerDiameterM"),
                    TowerHeightM = Num(root, "towerHeightM"),
                    PressureDropPa = Num(root, "pressureDropPa"),
                    PercentFlood = Num(root, "percentFlood"),
                    RemovalPct = Num(root, "removalEfficiencyPct"),
                    Fails = fails,
                    Warns = warns,
                    CalculationJson = computation.CalculationJson,
                    ChecksJson = computation.ChecksJson
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static double Num(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
                ? d
                : 0.0;
        }

        private static WetScrubberDraftState Clone(WetScrubberDraftState s, string packingCode, double lg)
        {
            return new WetScrubberDraftState
            {
                SessionId = s.SessionId,
                LastUpdatedUtc = s.LastUpdatedUtc,
                NormalFlowRate = s.NormalFlowRate,
                ActualFlowRate = s.ActualFlowRate,
                InletTemperature = s.InletTemperature,
                InletPressure = s.InletPressure,
                MoistureContent = s.MoistureContent,
                PollutantName = s.PollutantName,
                InletConcentration = s.InletConcentration,
                TargetRemovalEfficiency = s.TargetRemovalEfficiency,
                LiquidName = s.LiquidName,
                LiquidConcentration = s.LiquidConcentration,
                LiquidPH = s.LiquidPH,
                LiquidTemperature = s.LiquidTemperature,
                LiquidToGasRatio = lg,
                LiquidToGasRatioUserSet = true,
                PackingCode = packingCode,
                ShellMaterial = s.ShellMaterial,
                InternalMaterial = s.InternalMaterial
            };
        }
    }
}