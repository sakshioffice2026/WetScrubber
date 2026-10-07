using System.Text.Json;
using EngineeringAI.Core.Agent;
using EngineeringAI.Core.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WetScrubber.Database;
using WetScrubber.Database.Enums;
using WetScrubber.Plugins;

namespace WetScrubber.Controllers
{
    public class AgentController : Controller
    {
        private readonly AgentOrchestrator<WetScrubberDraftState> _agent;
        private readonly DesignFlowStore<WetScrubberDraftState> _store;
        private readonly ScrubberDesignPlugin _design;
        private readonly ScrubberOptimizerPlugin _optimizer;
        private readonly ScrubberDatabasePlugin _lookups;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<AgentController> _logger;

        private static readonly System.Text.RegularExpressions.Regex OptimizeIntent = new(
            @"\boptimi[sz]e\b|\boptimi[sz]ation\b|\bbest design\b|\blowest power\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex CompareIntent = new(
            @"\bcompare\b|\bbefore\s*(?:&|and)\s*after\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OptimizationSnapshot> LastOptimization = new();

        private static readonly System.Text.RegularExpressions.Regex SavedDesignId = new(
            @"\boptimi[sz]e\s+(?:the\s+|saved\s+)*(?:design\s*)?(?:id\s*)?#?\s*(\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

        public AgentController(
            AgentOrchestrator<WetScrubberDraftState> agent,
            DesignFlowStore<WetScrubberDraftState> store,
            ScrubberDesignPlugin design,
            ScrubberOptimizerPlugin optimizer,
            ScrubberDatabasePlugin lookups,
            ApplicationDbContext db,
            ILogger<AgentController> logger)
        {
            _agent = agent;
            _store = store;
            _design = design;
            _optimizer = optimizer;
            _lookups = lookups;
            _db = db;
            _logger = logger;
        }

        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");

        private string GetSessionKey(int userId)
        {
            HttpContext.Session.SetString("AgentSessionInit", "1");
            return $"u{userId}-{HttpContext.Session.Id}";
        }

        [HttpGet]
        public async Task<IActionResult> Index(int projectId)
        {
            var userId = GetUserId();
            if (userId == null)
            {
                return RedirectToAction("Login", "Account");
            }

            var project = await _db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.ProjectId == projectId &&
                         p.CreatedByUserId == userId.Value);

            if (project == null)
            {
                TempData["Error"] = "Project not found.";
                return RedirectToAction("Index", "Project");
            }

            ViewBag.ProjectId = project.ProjectId;
            ViewBag.ProjectNumber = project.ProjectNumber;
            ViewBag.ProjectName = project.ProjectName;

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Chat([FromBody] AgentChatRequest request)
        {
            var userId = GetUserId();
            if (userId == null)
            {
                return Unauthorized();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { error = "Message is required." });
            }

            var sessionKey = GetSessionKey(userId.Value);

            if (CompareIntent.IsMatch(request.Message) &&
                !OptimizeIntent.IsMatch(request.Message))
            {
                return CompareInternal(sessionKey);
            }

            if (OptimizeIntent.IsMatch(request.Message))
            {
                var idMatch = SavedDesignId.Match(request.Message);

                if (idMatch.Success &&
                    int.TryParse(idMatch.Groups[1].Value, out var savedId))
                {
                    var loaded = await LoadSavedDesignIntoDraftAsync(
                        userId.Value,
                        savedId,
                        sessionKey,
                        HttpContext.RequestAborted);

                    if (!loaded)
                    {
                        return Ok(new
                        {
                            message = $"Design {savedId} was not found in your projects.",
                            designComplete = false,
                            missingFields = Array.Empty<string>(),
                            calculation = (JsonElement?)null,
                            checks = (JsonElement?)null,
                            draft = _store.GetOrCreate(sessionKey),
                            optimization = (object?)null
                        });
                    }
                }

                return await OptimizeInternalAsync(sessionKey);
            }

            _store.Update(
                sessionKey,
                s => ScrubberDesignPlugin.ApplyRuleBased(s, request.Message));

            try
            {
                var reply = await _agent.HandleAsync(
                    sessionKey,
                    request.Message,
                    HttpContext.RequestAborted);

                _store.TryGet(sessionKey, out var draft);

                return Ok(new
                {
                    message = reply.Message,
                    designComplete = reply.DesignComplete,
                    missingFields = reply.MissingFields,
                    calculation = ParseJson(reply.CalculationJson),
                    checks = ParseJson(reply.ChecksJson),
                    draft,
                    optimization = GetOptimizationForSession(sessionKey)
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError(ex, "AI model file is missing.");

                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new
                    {
                        error = "The AI model is not available. Contact the administrator."
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Agent chat failed for session {SessionKey}",
                    sessionKey);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new
                    {
                        error = "The assistant could not process this request."
                    });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Optimize()
        {
            var userId = GetUserId();
            if (userId == null)
            {
                return Unauthorized();
            }

            return await OptimizeInternalAsync(GetSessionKey(userId.Value));
        }

        private IActionResult CompareInternal(string sessionKey)
        {
            if (!LastOptimization.TryGetValue(sessionKey, out var snap) ||
                snap.Baseline is null)
            {
                return Ok(new
                {
                    message = "There is no optimization to compare yet. Run \"optimize design <id>\" or press Optimize first.",
                    designComplete = false,
                    missingFields = Array.Empty<string>(),
                    calculation = (JsonElement?)null,
                    checks = (JsonElement?)null,
                    draft = _store.GetOrCreate(sessionKey),
                    optimization = (object?)null
                });
            }

            var b = snap.Baseline;
            var a = snap.Best;

            static string Row(
                string label,
                string before,
                string after) =>
                $"{label}: {before}  →  {after}";

            var lines = new List<string>
            {
                "Before vs after optimization:",
                Row("Packing", b.PackingCode, a.PackingCode),
                Row(
                    "L/G (L/m³)",
                    b.LiquidToGasRatio.ToString("0.##"),
                    a.LiquidToGasRatio.ToString("0.##")),
                Row(
                    "Total power (kW)",
                    b.TotalPowerKW.ToString("0.##"),
                    a.TotalPowerKW.ToString("0.##")),
                Row(
                    "Tower diameter (m)",
                    b.TowerDiameterM.ToString("0.##"),
                    a.TowerDiameterM.ToString("0.##")),
                Row(
                    "Tower height (m)",
                    b.TowerHeightM.ToString("0.##"),
                    a.TowerHeightM.ToString("0.##")),
                Row(
                    "Liquid loading (m³/m²·h)",
                    b.LiquidLoadingM3M2Hr.ToString("0.#"),
                    a.LiquidLoadingM3M2Hr.ToString("0.#")),
                Row(
                    "Pressure drop (Pa)",
                    b.PressureDropPa.ToString("0.##"),
                    a.PressureDropPa.ToString("0.##")),
                Row(
                    "Flooding (%)",
                    b.PercentFlood.ToString("0.#"),
                    a.PercentFlood.ToString("0.#")),
                Row(
                    "Removal (%)",
                    b.RemovalPct.ToString("0.##"),
                    a.RemovalPct.ToString("0.##")),
                Row(
                    "Failed / warning checks",
                    $"{b.Fails} / {b.Warns}",
                    $"{a.Fails} / {a.Warns}")
            };

            double? powerChangePct = null;

            if (b.TotalPowerKW > 0)
            {
                var saving =
                    (b.TotalPowerKW - a.TotalPowerKW) /
                    b.TotalPowerKW *
                    100.0;

                powerChangePct = -saving;

                lines.Add(
                    $"Power change: {(saving >= 0 ? "-" : "+")}{Math.Abs(saving):0.#}%");
            }

            return Ok(new
            {
                message = string.Join("\n", lines),
                designComplete = true,
                missingFields = Array.Empty<string>(),
                calculation = ParseJson(a.CalculationJson),
                checks = ParseJson(a.ChecksJson),
                draft = _store.GetOrCreate(sessionKey),
                optimization = BuildOptimizationPayload(
                    snap,
                    powerChangePct)
            });
        }

        private async Task<bool> LoadSavedDesignIntoDraftAsync(
            int userId,
            int designId,
            string sessionKey,
            CancellationToken ct)
        {
            var design = await _db.ScrubberDesigns
                .AsNoTracking()
                .Include(d => d.Project)
                .Include(d => d.GasStream!)
                    .ThenInclude(g => g.Pollutants)
                .Include(d => d.LiquidSpec)
                .FirstOrDefaultAsync(
                    d => d.DesignId == designId &&
                         d.Project.CreatedByUserId == userId,
                    ct);

            if (design?.GasStream == null)
            {
                return false;
            }

            var gas = design.GasStream;
            var pollutantRow = gas.Pollutants.FirstOrDefault();
            var spec = design.LiquidSpec;

            string? pollutantName = null;

            if (pollutantRow != null)
            {
                pollutantName = await _db.Pollutants
                    .AsNoTracking()
                    .Where(p => p.Id == pollutantRow.PollutantType)
                    .Select(p => p.Code)
                    .FirstOrDefaultAsync(ct);
            }

            string? liquidName = null;

            if (spec != null)
            {
                liquidName = await _db.ScrubbingLiquids
                    .AsNoTracking()
                    .Where(l => l.Id == spec.LiquidType)
                    .Select(l => l.Code)
                    .FirstOrDefaultAsync(ct);
            }

            _store.Remove(sessionKey);
            LastOptimization.TryRemove(sessionKey, out _);

            _store.Update(sessionKey, s =>
            {
                s.NormalFlowRate =
                    gas.NormalFlowRate > 0
                        ? gas.NormalFlowRate
                        : null;

                s.ActualFlowRate =
                    gas.NormalFlowRate > 0
                        ? null
                        : gas.ActualFlowRate > 0
                            ? gas.ActualFlowRate
                            : null;

                s.InletTemperature = gas.InletTemperature;
                s.InletPressure = gas.InletPressure;
                s.MoistureContent = gas.MoistureContent;

                if (pollutantRow != null)
                {
                    s.PollutantName = pollutantName;
                    s.InletConcentration = pollutantRow.InletConcentration;
                    s.TargetRemovalEfficiency =
                        pollutantRow.TargetRemovalEfficiency;
                }

                if (spec != null)
                {
                    if (!string.IsNullOrWhiteSpace(liquidName))
                    {
                        s.LiquidName = liquidName;
                    }

                    s.LiquidConcentration = spec.Concentration;
                    s.LiquidPH = spec.pH;
                    s.LiquidTemperature = spec.Temperature;
                    s.LiquidToGasRatio = spec.LiquidToGasRatio;
                    s.LiquidToGasRatioUserSet = true;
                }

                if (!string.IsNullOrWhiteSpace(design.PackingCode))
                {
                    s.PackingCode = design.PackingCode!;
                }

                s.ShellMaterial = design.ShellMaterial;
                s.InternalMaterial = design.InternalMaterial;
            });

            return true;
        }

        private async Task<IActionResult> OptimizeInternalAsync(
            string sessionKey)
        {
            try
            {
                var draft = _store.GetOrCreate(sessionKey);
                var missing = draft.GetMissingMandatoryFields();

                if (missing.Count > 0)
                {
                    return Ok(new
                    {
                        message =
                            $"Optimization needs a complete design first. Please provide: {string.Join(", ", missing)}.",
                        designComplete = false,
                        missingFields = missing,
                        calculation = (JsonElement?)null,
                        checks = (JsonElement?)null,
                        draft,
                        optimization = (object?)null
                    });
                }

                var outcome = await _optimizer.OptimizeAsync(
                    draft,
                    HttpContext.RequestAborted);

                if (outcome.Best is null)
                {
                    return Ok(new
                    {
                        message =
                            "The optimizer could not find a valid design for these inputs.",
                        designComplete = false,
                        missingFields = Array.Empty<string>(),
                        calculation = (JsonElement?)null,
                        checks = (JsonElement?)null,
                        draft,
                        optimization = (object?)null
                    });
                }

                var best = outcome.Best;
                var baseline = outcome.Baseline;

                var snapshot = new OptimizationSnapshot(
                    baseline,
                    best,
                    outcome.Evaluated);

                LastOptimization[sessionKey] = snapshot;

                _store.Update(sessionKey, s =>
                {
                    s.PackingCode = best.PackingCode;
                    s.LiquidToGasRatio = best.LiquidToGasRatio;
                    s.LiquidToGasRatioUserSet = true;
                });

                string message;

                if (outcome.BaselineIsBest)
                {
                    message =
                        $"The current design is already the best of {outcome.Evaluated} combinations checked " +
                        $"(packing {best.PackingCode}, L/G {best.LiquidToGasRatio:0.##} L/m³, " +
                        $"total power {best.TotalPowerKW:0.##} kW).";
                }
                else
                {
                    message =
                        $"Optimized design applied from {outcome.Evaluated} combinations: " +
                        $"packing {best.PackingCode}, L/G {best.LiquidToGasRatio:0.##} L/m³. " +
                        $"Total power {best.TotalPowerKW:0.##} kW" +
                        (baseline is not null
                            ? $" (was {baseline.TotalPowerKW:0.##} kW)"
                            : string.Empty) +
                        $", tower {best.TowerDiameterM:0.##} m dia x " +
                        $"{best.TowerHeightM:0.##} m high, " +
                        $"flooding {best.PercentFlood:0.#}%, " +
                        $"removal {best.RemovalPct:0.##}%.";
                }

                if (!outcome.BestPassesAllChecks)
                {
                    message +=
                        $" No combination passed every check; the best has " +
                        $"{best.Fails} failed and {best.Warns} warning check(s).";
                }

                return Ok(new
                {
                    message,
                    designComplete = true,
                    missingFields = Array.Empty<string>(),
                    calculation = ParseJson(best.CalculationJson),
                    checks = ParseJson(best.ChecksJson),
                    draft = _store.GetOrCreate(sessionKey),
                    optimization = BuildOptimizationPayload(
                        snapshot,
                        CalculatePowerChangePct(baseline, best))
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Optimization failed for session {SessionKey}",
                    sessionKey);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new
                    {
                        error = "The optimization could not be completed."
                    });
            }
        }

        private static object? GetOptimizationForSession(
            string sessionKey)
        {
            if (!LastOptimization.TryGetValue(
                    sessionKey,
                    out var snapshot))
            {
                return null;
            }

            return BuildOptimizationPayload(
                snapshot,
                CalculatePowerChangePct(
                    snapshot.Baseline,
                    snapshot.Best));
        }

        private static object BuildOptimizationPayload(
            OptimizationSnapshot snapshot,
            double? powerChangePct)
        {
            return new
            {
                evaluated = snapshot.Evaluated,
                alreadyOptimal = snapshot.BaselineIsBest,
                passesAllChecks = snapshot.BestPassesAllChecks,
                powerChangePct,
                before = snapshot.Baseline is null
                    ? null
                    : BuildCandidatePayload(snapshot.Baseline),
                after = BuildCandidatePayload(snapshot.Best)
            };
        }

        private static object BuildCandidatePayload(
            OptimizationCandidate candidate)
        {
            return new
            {
                packing = candidate.PackingCode,
                packingCode = candidate.PackingCode,

                liquidToGasRatio = candidate.LiquidToGasRatio,
                actualLGRatio = candidate.LiquidToGasRatio,

                totalPowerKW = candidate.TotalPowerKW,

                towerDiameterM = candidate.TowerDiameterM,
                towerHeightM = candidate.TowerHeightM,

                liquidLoadingM3M2Hr = candidate.LiquidLoadingM3M2Hr,
                pressureDropPa = candidate.PressureDropPa,
                percentFlood = candidate.PercentFlood,
                removalPct = candidate.RemovalPct,

                fails = candidate.Fails,
                warns = candidate.Warns,

                calculation = ParseJson(candidate.CalculationJson),
                checks = ParseJson(candidate.ChecksJson)
            };
        }

        private static double? CalculatePowerChangePct(
            OptimizationCandidate? baseline,
            OptimizationCandidate best)
        {
            if (baseline is null || baseline.TotalPowerKW <= 0)
            {
                return null;
            }

            return
                (best.TotalPowerKW - baseline.TotalPowerKW) /
                baseline.TotalPowerKW *
                100.0;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            var userId = GetUserId();
            if (userId == null)
            {
                return Unauthorized();
            }

            var sessionKey = GetSessionKey(userId.Value);

            _store.Remove(sessionKey);
            LastOptimization.TryRemove(sessionKey, out _);

            return Ok(new { reset = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(
            [FromBody] AgentSaveRequest request)
        {
            var userId = GetUserId();

            if (userId == null)
            {
                return Unauthorized();
            }

            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest(new { error = "Project is required." });
            }

            var project = await _db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.ProjectId == request.ProjectId &&
                         p.CreatedByUserId == userId.Value);

            if (project == null)
            {
                return NotFound(new { error = "Project not found." });
            }

            var sessionKey = GetSessionKey(userId.Value);
            var draft = _store.GetOrCreate(sessionKey);

            if (draft.GetMissingMandatoryFields().Count > 0)
            {
                return BadRequest(new
                {
                    error = "The design is incomplete."
                });
            }

            var ct = HttpContext.RequestAborted;

            var computation = await _design.ComputeAsync(draft, ct);

            using var calc = JsonDocument.Parse(
                computation.CalculationJson);

            if (calc.RootElement.TryGetProperty(
                    "error",
                    out var calcError))
            {
                return BadRequest(new
                {
                    error = calcError.GetString()
                });
            }

            var normalFlow =
                calc.RootElement
                    .GetProperty("derivedNormalFlowNm3Hr")
                    .GetDouble();

            var actualFlow =
                calc.RootElement
                    .GetProperty("derivedActualFlowM3Hr")
                    .GetDouble();

            var packingCode =
                calc.RootElement
                    .GetProperty("packingUsed")
                    .GetString() ??
                "PallRing50";

            var pollutant =
                await _lookups.FindPollutantAsync(
                    draft.PollutantName,
                    ct);

            var liquid =
                await _lookups.FindLiquidAsync(
                    draft.LiquidName,
                    ct);

            if (pollutant == null || liquid == null)
            {
                return BadRequest(new
                {
                    error =
                        "Pollutant or scrubbing liquid was not found in the catalog."
                });
            }

            var designName =
                string.IsNullOrWhiteSpace(request.DesignName)
                    ? $"AI Design {DateTime.Now:yyyy-MM-dd HH:mm}"
                    : request.DesignName.Trim();

            if (designName.Length > 200)
            {
                designName = designName[..200];
            }

            var inlet = draft.InletConcentration!.Value;

            await using var tx =
                await _db.Database.BeginTransactionAsync(ct);

            try
            {
                var design = new ScrubberDesign
                {
                    ProjectId = project.ProjectId,
                    DesignName = designName,
                    ScrubberType = ScrubberType.PackedTower,
                    ShellMaterial = draft.ShellMaterial,
                    InternalMaterial = draft.InternalMaterial,
                    PackingCode = packingCode,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _db.ScrubberDesigns.Add(design);
                await _db.SaveChangesAsync(ct);

                var gas = new GasStream
                {
                    DesignId = design.DesignId,
                    NormalFlowRate = normalFlow,
                    ActualFlowRate = actualFlow,
                    InletTemperature = draft.InletTemperature!.Value,
                    InletPressure = draft.InletPressure,
                    MoistureContent = draft.MoistureContent
                };

                _db.GasStreams.Add(gas);
                await _db.SaveChangesAsync(ct);

                _db.PollutantStreams.Add(new PollutantStream
                {
                    GasStreamId = gas.GasStreamId,
                    PollutantType = pollutant.Id,
                    InletConcentration = inlet,
                    TargetOutletConcentration =
                        inlet *
                        (1.0 -
                         draft.TargetRemovalEfficiency / 100.0),
                    TargetRemovalEfficiency =
                        draft.TargetRemovalEfficiency,
                    MolecularWeight =
                        pollutant.DefaultMolecularWeight,
                    HenrysLawConstant =
                        pollutant.DefaultHenrysLawConstant
                });

                _db.ScrubbingLiquidSpecs.Add(
                    new ScrubbingLiquidSpec
                    {
                        DesignId = design.DesignId,
                        LiquidType = liquid.Id,
                        Concentration = draft.LiquidConcentration,
                        pH = draft.LiquidPH,
                        Temperature = draft.LiquidTemperature,
                        Density = liquid.DefaultDensity,
                        LiquidToGasRatio =
                            draft.LiquidToGasRatio
                    });

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _store.Remove(sessionKey);
                LastOptimization.TryRemove(sessionKey, out _);

                _logger.LogInformation(
                    "AI design '{Name}' saved for ProjectId {Id}.",
                    design.DesignName,
                    design.ProjectId);

                return Ok(new
                {
                    designId = design.DesignId,
                    redirectUrl =
                        Url.Action(
                            "DesignDetail",
                            "Scrubber",
                            new
                            {
                                id = design.DesignId
                            })
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(
                    CancellationToken.None);

                _logger.LogError(
                    ex,
                    "Saving AI design failed for ProjectId {Id}.",
                    project.ProjectId);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new
                    {
                        error = "The design could not be saved."
                    });
            }
        }

        private static JsonElement? ParseJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.Clone();
        }
    }

    public sealed record OptimizationSnapshot(
        OptimizationCandidate? Baseline,
        OptimizationCandidate Best,
        int Evaluated)
    {
        public bool BaselineIsBest =>
            Baseline is not null &&
            string.Equals(
                Baseline.PackingCode,
                Best.PackingCode,
                StringComparison.OrdinalIgnoreCase) &&
            Baseline.LiquidToGasRatio ==
            Best.LiquidToGasRatio &&
            Baseline.TotalPowerKW ==
            Best.TotalPowerKW;

        public bool BestPassesAllChecks =>
            Best.Fails == 0 &&
            Best.Warns == 0;
    }

    public class AgentChatRequest
    {
        public string Message { get; set; } = string.Empty;
    }

    public class AgentSaveRequest
    {
        public int ProjectId { get; set; }

        public string? DesignName { get; set; }
    }
}