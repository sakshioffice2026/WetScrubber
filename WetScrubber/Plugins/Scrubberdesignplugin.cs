using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using EngineeringAI.Core.Abstractions;
using EngineeringAI.Core.Agent;
using EngineeringAI.Core.State;
using Microsoft.SemanticKernel;
using WetScrubber.Business.Thermodynamics;
using WetScrubber.Database;
using WetScrubber.Database.Enums;
using WetScrubber.Models;
using WetScrubber.Repositories.Repositories;
using WetScrubber.Services;

namespace WetScrubber.Plugins
{
    public class ScrubberDesignPlugin : IEngineeringPlugin
    {
        public const string DomainDescription = "wet scrubber (packed tower) design for gas pollutant removal";

        public const string FieldSchemaJson = """
            {
              "normalFlowRate": "number, gas flow in Nm3/hr",
              "actualFlowRate": "number, gas flow in m3/hr",
              "inletTemperature": "number, inlet gas temperature in degrees Celsius",
              "inletPressure": "number, inlet gas pressure in Pa",
              "moistureContent": "number, gas moisture in % vol",
              "pollutantName": "string, pollutant name or formula, for example SO2",
              "inletConcentration": "number, inlet pollutant concentration in mg/Nm3 (only when the user states mg/Nm3)",
              "inletConcentrationPpm": "number, inlet pollutant concentration in ppm (only when the user states ppm)",
              "targetRemovalEfficiency": "number, required removal in %",
              "liquidName": "string, scrubbing liquid, for example Caustic Soda",
              "liquidConcentration": "number, liquid concentration in % wt",
              "liquidPH": "number, liquid pH",
              "liquidTemperature": "number, liquid temperature in degrees Celsius",
              "liquidToGasRatio": "number, L/G ratio in L per m3 of gas; the user may write it as L/G, LG ratio or liquid to gas ratio, for example L/G 1.5 means 1.5",
              "packingCode": "string, packing code, for example PallRing50",
              "shellMaterial": "string, one of FRP, PP, HDPE, PVC, SS316, HastelloyC, CarbonSteel",
              "internalMaterial": "string, material of packing and internals, one of FRP, PP, HDPE, PVC, SS316, HastelloyC, CarbonSteel"
            }
            """;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly ApplicationDbContext _db;
        private readonly ScrubberDatabasePlugin _lookups;
        private readonly ScrubberPhysicalChecker _checker;
        private readonly DesignFlowStore<WetScrubberDraftState> _store;
        private readonly ILogger<ScrubberDesignPlugin> _logger;
        private readonly ScrubberCalculationEngine _engine;

        public ScrubberDesignPlugin(
            ApplicationDbContext db,
            ScrubberDatabasePlugin lookups,
            ScrubberPhysicalChecker checker,
            DesignFlowStore<WetScrubberDraftState> store,
            ILogger<ScrubberDesignPlugin> logger)
        {
            _db = db;
            _lookups = lookups;
            _checker = checker;
            _store = store;
            _logger = logger;

            _engine = new ScrubberCalculationEngine(
                new PengRobinsonEos(),
                new EfComponentPropertyLookup(db),
                new EfHenrysLawLookup(db),
                new NrtlActivityModel(),
                new EfNrtlBinaryParameterLookup(db),
                new EfDiffusionCoefficientLookup(db),
                new EfPackingLookup(db));
        }

        public string PluginName => "ScrubberDesign";

        // ── Kernel function ─────────────────────────────────────────────────

        [KernelFunction("run_scrubber_sizing")]
        [Description("Runs the deterministic packed tower sizing calculation and physical checks for the current session draft.")]
        public async Task<string> RunScrubberSizingAsync(
            [Description("Chat session id")] string sessionId,
            CancellationToken ct = default)
        {
            var draft = _store.GetOrCreate(sessionId);
            var missing = draft.GetMissingMandatoryFields();

            if (missing.Count > 0)
            {
                return JsonSerializer.Serialize(new { ready = false, missing }, JsonOptions);
            }

            var computation = await ComputeAsync(draft, ct);

            return JsonSerializer.Serialize(new
            {
                ready = true,
                calculation = JsonDocument.Parse(computation.CalculationJson).RootElement,
                checks = JsonDocument.Parse(computation.ChecksJson).RootElement
            }, JsonOptions);
        }

        // ── Deterministic computation ───────────────────────────────────────

        public async Task<AgentComputation> ComputeAsync(WetScrubberDraftState draft, CancellationToken ct = default)
        {
            var pollutant = await _lookups.FindPollutantAsync(draft.PollutantName, ct);
            if (pollutant is null)
            {
                return Failure($"Pollutant '{draft.PollutantName}' was not found in the master catalog.", "PollutantName");
            }

            var liquid = await _lookups.FindLiquidAsync(draft.LiquidName, ct);
            if (liquid is null)
            {
                return Failure($"Scrubbing liquid '{draft.LiquidName}' was not found in the master catalog.", "LiquidName");
            }

            var notes = new List<string>();

            var packing = await _lookups.FindPackingAsync(draft.PackingCode, ct);
            var packingCode = packing?.Code ?? "PallRing50";
            if (packing is null)
            {
                notes.Add($"Packing '{draft.PackingCode}' not found; PallRing50 was used.");
            }

            var tempC = draft.InletTemperature!.Value;
            var pressure = draft.InletPressure;
            var actualToNormal = (tempC + 273.15) / 273.15 * (101325.0 / pressure);

            var normalFlow = draft.NormalFlowRate ?? draft.ActualFlowRate!.Value / actualToNormal;
            var actualFlow = draft.NormalFlowRate.HasValue
                ? normalFlow * actualToNormal
                : draft.ActualFlowRate!.Value;

            var inlet = draft.InletConcentration!.Value;
            var target = draft.TargetRemovalEfficiency;

            var vm = new CreateDesignViewModel
            {
                DesignName = "AI Draft",
                ScrubberType = ScrubberType.PackedTower,
                NormalFlowRate = normalFlow,
                ActualFlowRate = actualFlow,
                InletTemperature = tempC,
                InletPressure = pressure,
                MoistureContent = draft.MoistureContent,
                Pollutants = new List<PollutantInputViewModel>
                {
                    new()
                    {
                        PollutantType = pollutant.Id,
                        InletConcentration = inlet,
                        TargetOutletConcentration = inlet * (1.0 - target / 100.0),
                        TargetRemovalEfficiency = target,
                        MolecularWeight = pollutant.DefaultMolecularWeight,
                        HenrysLawConstant = pollutant.DefaultHenrysLawConstant
                    }
                },
                LiquidType = liquid.Id,
                LiquidConcentration = draft.LiquidConcentration,
                LiquidPH = draft.LiquidPH,
                LiquidTemperature = draft.LiquidTemperature,
                LiquidDensity = liquid.DefaultDensity,
                LiquidToGasRatio = draft.LiquidToGasRatio,
                PackingCode = packingCode,
                ShellMaterial = draft.ShellMaterial,
                InternalMaterial = draft.InternalMaterial
            };

            CalculationResult result;
            try
            {
                result = _engine.RunCalculation(vm);

                if (!draft.LiquidToGasRatioUserSet && !IsHydraulicallyAcceptable(result, target))
                {
                    var original = vm.LiquidToGasRatio;
                    var accepted = false;

                    foreach (var candidate in AutoLiquidToGasRatios.Where(c => c < original))
                    {
                        vm.LiquidToGasRatio = candidate;
                        var trial = _engine.RunCalculation(vm);

                        if (IsHydraulicallyAcceptable(trial, target))
                        {
                            result = trial;
                            accepted = true;
                            notes.Add($"L/G was not specified; {candidate:0.##} L/m3 was selected to keep flooding and gas velocity within limits.");
                            break;
                        }
                    }

                    if (!accepted)
                    {
                        vm.LiquidToGasRatio = original;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scrubber calculation failed for session {SessionId}", draft.SessionId);
                return Failure("The calculation engine could not complete with these inputs.", "Inputs");
            }

            var checks = _checker.Evaluate(draft, result);

            var calculationJson = JsonSerializer.Serialize(new
            {
                scrubberType = result.ScrubberType,
                towerDiameterM = result.TowerDiameter,
                towerHeightM = result.TowerHeight,
                packingHeightM = result.PackingHeight,
                gasVelocityMs = result.GasVelocity,
                pressureDropPa = result.PressureDrop,
                percentFlood = result.PercentFlood,
                floodingGasVelocityMs = result.FloodingGasVelocity,
                removalEfficiencyPct = result.RemovalEfficiency,
                ntu = result.NTU,
                htu = result.HTU,
                absorptionFactor = result.AbsorptionFactor,
                minLGRatio = result.MinLGRatio,
                actualLGRatio = result.ActualLGRatio,
                liquidFlowRateM3Hr = result.LiquidFlowRateM3Hr,
                fanPowerKW = result.FanPowerKW,
                pumpPowerKW = result.PumpPowerKW,
                totalPowerKW = result.TotalPowerKW,
                liquidOutletTemperatureC = result.LiquidOutletTemperature,
                derivedNormalFlowNm3Hr = normalFlow,
                derivedActualFlowM3Hr = actualFlow,
                packingUsed = packingCode,
                notes
            }, JsonOptions);

            var checksJson = JsonSerializer.Serialize(
                checks.Select(c => new
                {
                    name = c.Name,
                    status = c.Status.ToString().ToUpperInvariant(),
                    parameter = c.Parameter,
                    detail = c.Detail
                }),
                JsonOptions);

            return new AgentComputation(calculationJson, checksJson);
        }

        private static readonly double[] AutoLiquidToGasRatios = { 2.5, 2.0, 1.5, 1.0, 0.75, 0.5 };

        private static bool IsHydraulicallyAcceptable(CalculationResult r, double targetRemovalPct)
        {
            if (r.PercentFlood > 70 || r.ExceedsRecommendedFlood)
            {
                return false;
            }

            if (r.GasVelocity < 0.5 || r.GasVelocity > 3.5)
            {
                return false;
            }

            if (r.RemovalEfficiency + 0.5 < targetRemovalPct)
            {
                return false;
            }

            if (r.AbsorptionFactor < 1.25)
            {
                return false;
            }

            return r.MinLGRatio <= 0 || r.ActualLGRatio / r.MinLGRatio >= 1.2;
        }

        private static AgentComputation Failure(string message, string parameter)
        {
            var calculationJson = JsonSerializer.Serialize(new { error = message }, JsonOptions);

            var checksJson = JsonSerializer.Serialize(new[]
            {
                new { name = "Input validation", status = "FAIL", parameter, detail = message }
            }, JsonOptions);

            return new AgentComputation(calculationJson, checksJson);
        }

        // ── ppm to mg/Nm3 conversion ────────────────────────────────────────

        private const double NormalMolarVolume = 22.414; // Nm3/kmol at 0 C, 1 atm

        private static readonly Dictionary<string, double> MolecularWeights =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["SO2"] = 64.066,
                ["SO3"] = 80.063,
                ["HCl"] = 36.461,
                ["HF"] = 20.006,
                ["NH3"] = 17.031,
                ["H2S"] = 34.081,
                ["Cl2"] = 70.906,
                ["NO2"] = 46.006,
                ["NO"] = 30.006,
                ["CO2"] = 44.009,
                ["HBr"] = 80.912
            };

        private static string NormalizePollutantKey(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var key = name.Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

            return key switch
            {
                "sulfurdioxide" or "sulphurdioxide" => "SO2",
                "sulfurtrioxide" or "sulphurtrioxide" => "SO3",
                "hydrogenchloride" or "hydrochloricacid" => "HCl",
                "hydrogenfluoride" or "hydrofluoricacid" => "HF",
                "ammonia" => "NH3",
                "hydrogensulfide" or "hydrogensulphide" => "H2S",
                "chlorine" => "Cl2",
                "nitrogendioxide" => "NO2",
                "nitricoxide" => "NO",
                "carbondioxide" => "CO2",
                "hydrogenbromide" => "HBr",
                _ => name.Trim()
            };
        }

        private static bool TryPpmToMgNm3(string? pollutant, double ppm, out double mgNm3)
        {
            mgNm3 = 0;

            if (!MolecularWeights.TryGetValue(NormalizePollutantKey(pollutant), out var mw))
            {
                return false;
            }

            mgNm3 = ppm * mw / NormalMolarVolume;
            return double.IsFinite(mgNm3);
        }

        // ── Rule-based extraction (deterministic, runs before the LLM) ──────

        private static readonly System.Text.RegularExpressions.RegexOptions Rx =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;

        private const string Num = @"(-?\d[\d,]*(?:\.\d+)?)";

        public static void ApplyRuleBased(WetScrubberDraftState state, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var found = new Dictionary<string, object>();

            // Gas flow with explicit unit
            var unitFlow = System.Text.RegularExpressions.Regex.Match(
                message, Num + @"\s*(n)?\s*(?:m3|m³|m\^3)\s*/?\s*(?:h|hr|hour)\b", Rx);

            if (unitFlow.Success && TryNumber(unitFlow.Groups[1].Value, out var flowWithUnit))
            {
                found[unitFlow.Groups[2].Success ? "normalFlowRate" : "actualFlowRate"] = flowWithUnit;
            }
            else
            {
                // "flow 800", "flowrate: 800", "flow rate of 800"
                var plainFlow = System.Text.RegularExpressions.Regex.Match(
                    message, @"flow\s*-?\s*(?:rate)?\s*(?:of|is|=|:)?\s*" + Num, Rx);

                if (plainFlow.Success && TryNumber(plainFlow.Groups[1].Value, out var plain))
                {
                    found["normalFlowRate"] = plain;
                }
            }

            // Temperature: "temperature 30", "tempreture: 30", "30 °C"
            var temp = System.Text.RegularExpressions.Regex.Match(
                message, @"temp\w*\s*(?:of|is|=|:)?\s*" + Num, Rx);

            if (!temp.Success)
            {
                temp = System.Text.RegularExpressions.Regex.Match(message, Num + @"\s*(?:°|º|deg\w*)\s*c\b", Rx);
            }

            if (temp.Success && TryNumber(temp.Groups[1].Value, out var tempValue))
            {
                found["inletTemperature"] = tempValue;
            }

            // Pollutant
            var pollutant = System.Text.RegularExpressions.Regex.Match(
                message, @"\b(SO2|SO3|HCl|HF|NH3|H2S|Cl2|NO2|HBr|sulphur dioxide|sulfur dioxide|ammonia|hydrogen chloride|hydrogen sulfide|chlorine)\b", Rx);

            if (pollutant.Success)
            {
                found["pollutantName"] = pollutant.Groups[1].Value;
            }

            // Concentration
            var ppm = System.Text.RegularExpressions.Regex.Match(message, Num + @"\s*ppm", Rx);
            var mg = System.Text.RegularExpressions.Regex.Match(message, Num + @"\s*mg\s*/\s*n?m3", Rx);

            if (ppm.Success && TryNumber(ppm.Groups[1].Value, out var ppmValue))
            {
                found["inletConcentrationPpm"] = ppmValue;
            }
            else if (mg.Success && TryNumber(mg.Groups[1].Value, out var mgValue))
            {
                found["inletConcentration"] = mgValue;
            }

            // L/G ratio
            var lg = System.Text.RegularExpressions.Regex.Match(
                message, @"\bl\s*/\s*g(?:\s*ratio)?\s*(?:of|is|=|:)?\s*" + Num, Rx);

            if (lg.Success && TryNumber(lg.Groups[1].Value, out var lgValue))
            {
                found["liquidToGasRatio"] = lgValue;
            }

            // Removal
            var removal = System.Text.RegularExpressions.Regex.Match(
                message, Num + @"\s*%\s*(?:removal|efficiency)", Rx);

            if (!removal.Success)
            {
                removal = System.Text.RegularExpressions.Regex.Match(
                    message, @"(?:removal|efficiency)\s*(?:of|is|=|:)?\s*" + Num, Rx);
            }

            if (removal.Success && TryNumber(removal.Groups[1].Value, out var removalValue))
            {
                found["targetRemovalEfficiency"] = removalValue;
            }

            // Packing material
            var packing = System.Text.RegularExpressions.Regex.Match(
                message, @"\b(PP|HDPE|PVC|FRP|SS316L?|polypropylene)\s+packing\b", Rx);

            if (packing.Success)
            {
                found["packingMaterial"] = packing.Groups[1].Value;
            }

            if (found.Count == 0)
            {
                return;
            }

            if (found.ContainsKey("normalFlowRate")) state.ActualFlowRate = null;
            if (found.ContainsKey("actualFlowRate")) state.NormalFlowRate = null;

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(found));
            ApplyExtracted(state, doc.RootElement);
        }

        private static bool TryNumber(string raw, out double value)
        {
            return double.TryParse(raw.Replace(",", string.Empty), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        // ── Draft update from extracted JSON ────────────────────────────────

        public static void ApplyExtracted(WetScrubberDraftState s, JsonElement root)
        {
            double? ppmValue = null;

            foreach (var prop in root.EnumerateObject())
            {
                var v = prop.Value;

                switch (prop.Name.ToLowerInvariant())
                {
                    case "normalflowrate":
                        if (TryRange(v, 1, 10_000_000, out var nf)) s.NormalFlowRate = nf;
                        break;
                    case "actualflowrate":
                        if (TryRange(v, 1, 10_000_000, out var af)) s.ActualFlowRate = af;
                        break;
                    case "inlettemperature":
                        if (TryRange(v, -50, 1500, out var t)) s.InletTemperature = t;
                        break;
                    case "inletpressure":
                        if (TryRange(v, 50_000, 300_000, out var p)) s.InletPressure = p;
                        break;
                    case "moisturecontent":
                        if (TryRange(v, 0, 100, out var m)) s.MoistureContent = m;
                        break;
                    case "pollutantname":
                        if (TryText(v, out var pn)) s.PollutantName = pn;
                        break;
                    case "inletconcentration":
                        if (TryRange(v, 0.1, 1_000_000, out var ic)) s.InletConcentration = ic;
                        break;
                    case "inletconcentrationppm":
                        if (TryRange(v, 0.01, 1_000_000, out var ppm)) ppmValue = ppm;
                        break;
                    case "targetremovalefficiency":
                        if (TryRange(v, 1, 99.99, out var te)) s.TargetRemovalEfficiency = te;
                        break;
                    case "liquidname":
                        // A construction material (e.g. "PP") is never a scrubbing liquid.
                        if (TryText(v, out var ln) && !TryMaterial(v, out _)) s.LiquidName = ln;
                        break;
                    case "liquidconcentration":
                        if (TryRange(v, 0, 50, out var lc)) s.LiquidConcentration = lc;
                        break;
                    case "liquidph":
                        if (TryRange(v, 0, 14, out var ph)) s.LiquidPH = ph;
                        break;
                    case "liquidtemperature":
                        if (TryRange(v, 0, 100, out var lt)) s.LiquidTemperature = lt;
                        break;
                    case "liquidtogasratio":
                    case "lgratio":
                    case "lg":
                    case "l/g":
                    case "liquidgasratio":
                        if (TryRange(v, 0.1, 50, out var lg))
                        {
                            s.LiquidToGasRatio = lg;
                            s.LiquidToGasRatioUserSet = true;
                        }
                        break;
                    case "packingcode":
                        if (TryMaterial(v, out var packingMaterial))
                        {
                            // "PP packing" names the packing material, not a packing code.
                            s.InternalMaterial = packingMaterial;
                        }
                        else if (TryText(v, out var pc))
                        {
                            s.PackingCode = pc;
                        }
                        break;
                    case "shellmaterial":
                        if (TryMaterial(v, out var sm)) s.ShellMaterial = sm;
                        break;
                    case "internalmaterial":
                    case "packingmaterial":
                        if (TryMaterial(v, out var im)) s.InternalMaterial = im;
                        break;
                }
            }

            // ppm wins over a mg/Nm3 value the model may have copied from the same number.
            if (ppmValue.HasValue && TryPpmToMgNm3(s.PollutantName, ppmValue.Value, out var converted))
            {
                if (converted >= 0.1 && converted <= 1_000_000)
                {
                    s.InletConcentration = Math.Round(converted, 2);
                }
            }
        }

        private static bool TryRange(JsonElement v, double min, double max, out double value)
        {
            value = 0;

            if (v.ValueKind == JsonValueKind.Number)
            {
                if (!v.TryGetDouble(out value))
                {
                    return false;
                }
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                var raw = (v.GetString() ?? string.Empty).Replace(",", string.Empty).Trim();
                var match = System.Text.RegularExpressions.Regex.Match(raw, @"-?\d+(\.\d+)?([eE][+-]?\d+)?");
                if (!match.Success ||
                    !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            return double.IsFinite(value) && value >= min && value <= max;
        }

        private static bool TryText(JsonElement v, out string value)
        {
            value = string.Empty;

            if (v.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var raw = (v.GetString() ?? string.Empty).Trim();

            if (raw.Length == 0 || raw.Length > 100 ||
                raw.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            value = raw;
            return true;
        }

        private static bool TryMaterial(JsonElement v, out ConstructionMaterial material)
        {
            material = default;

            if (!TryText(v, out var raw))
            {
                return false;
            }

            var key = raw.Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

            switch (key)
            {
                case "pp":
                case "polypropylene":
                    material = ConstructionMaterial.PP;
                    return true;
                case "frp":
                case "fiberglass":
                    material = ConstructionMaterial.FRP;
                    return true;
                case "hdpe":
                    material = ConstructionMaterial.HDPE;
                    return true;
                case "pvc":
                    material = ConstructionMaterial.PVC;
                    return true;
                case "ss316":
                case "ss316l":
                case "316l":
                case "316":
                case "stainlesssteel":
                case "stainlesssteel316l":
                    material = ConstructionMaterial.SS316;
                    return true;
                case "hastelloy":
                case "hastelloyc":
                    material = ConstructionMaterial.HastelloyC;
                    return true;
                case "carbonsteel":
                case "cs":
                    material = ConstructionMaterial.CarbonSteel;
                    return true;
                default:
                    return Enum.TryParse(raw, true, out material);
            }
        }
    }
}