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
        private readonly ScrubberDatabasePlugin _lookups;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<AgentController> _logger;

        public AgentController(
            AgentOrchestrator<WetScrubberDraftState> agent,
            DesignFlowStore<WetScrubberDraftState> store,
            ScrubberDesignPlugin design,
            ScrubberDatabasePlugin lookups,
            ApplicationDbContext db,
            ILogger<AgentController> logger)
        {
            _agent = agent;
            _store = store;
            _design = design;
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

        // ── GET /Agent/Index?projectId=5 ─────────────────────────
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
                .FirstOrDefaultAsync(p => p.ProjectId == projectId && p.CreatedByUserId == userId.Value);

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

        // ── POST /Agent/Chat ─────────────────────────────────────
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

            try
            {
                var reply = await _agent.HandleAsync(sessionKey, request.Message, HttpContext.RequestAborted);
                _store.TryGet(sessionKey, out var draft);

                return Ok(new
                {
                    message = reply.Message,
                    designComplete = reply.DesignComplete,
                    missingFields = reply.MissingFields,
                    calculation = ParseJson(reply.CalculationJson),
                    checks = ParseJson(reply.ChecksJson),
                    draft
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError(ex, "AI model file is missing.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { error = "The AI model is not available. Contact the administrator." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent chat failed for session {SessionKey}", sessionKey);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = "The assistant could not process this request." });
            }
        }

        // ── POST /Agent/Reset ────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            var userId = GetUserId();
            if (userId == null)
            {
                return Unauthorized();
            }

            _store.Remove(GetSessionKey(userId.Value));
            return Ok(new { reset = true });
        }

        // ── POST /Agent/Save  (explicit human confirmation) ──────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save([FromBody] AgentSaveRequest request)
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
                .FirstOrDefaultAsync(p => p.ProjectId == request.ProjectId && p.CreatedByUserId == userId.Value);

            if (project == null)
            {
                return NotFound(new { error = "Project not found." });
            }

            var sessionKey = GetSessionKey(userId.Value);
            var draft = _store.GetOrCreate(sessionKey);

            if (draft.GetMissingMandatoryFields().Count > 0)
            {
                return BadRequest(new { error = "The design is incomplete." });
            }

            var ct = HttpContext.RequestAborted;

            var computation = await _design.ComputeAsync(draft, ct);
            using var calc = JsonDocument.Parse(computation.CalculationJson);

            if (calc.RootElement.TryGetProperty("error", out var calcError))
            {
                return BadRequest(new { error = calcError.GetString() });
            }

            var normalFlow = calc.RootElement.GetProperty("derivedNormalFlowNm3Hr").GetDouble();
            var actualFlow = calc.RootElement.GetProperty("derivedActualFlowM3Hr").GetDouble();
            var packingCode = calc.RootElement.GetProperty("packingUsed").GetString() ?? "PallRing50";

            var pollutant = await _lookups.FindPollutantAsync(draft.PollutantName, ct);
            var liquid = await _lookups.FindLiquidAsync(draft.LiquidName, ct);

            if (pollutant == null || liquid == null)
            {
                return BadRequest(new { error = "Pollutant or scrubbing liquid was not found in the catalog." });
            }

            var designName = string.IsNullOrWhiteSpace(request.DesignName)
                ? $"AI Design {DateTime.Now:yyyy-MM-dd HH:mm}"
                : request.DesignName.Trim();

            if (designName.Length > 200)
            {
                designName = designName[..200];
            }

            var inlet = draft.InletConcentration!.Value;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
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
                    TargetOutletConcentration = inlet * (1.0 - draft.TargetRemovalEfficiency / 100.0),
                    TargetRemovalEfficiency = draft.TargetRemovalEfficiency,
                    MolecularWeight = pollutant.DefaultMolecularWeight,
                    HenrysLawConstant = pollutant.DefaultHenrysLawConstant
                });

                _db.ScrubbingLiquidSpecs.Add(new ScrubbingLiquidSpec
                {
                    DesignId = design.DesignId,
                    LiquidType = liquid.Id,
                    Concentration = draft.LiquidConcentration,
                    pH = draft.LiquidPH,
                    Temperature = draft.LiquidTemperature,
                    Density = liquid.DefaultDensity,
                    LiquidToGasRatio = draft.LiquidToGasRatio
                });

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _store.Remove(sessionKey);

                _logger.LogInformation("AI design '{Name}' saved for ProjectId {Id}.", design.DesignName, design.ProjectId);

                return Ok(new
                {
                    designId = design.DesignId,
                    redirectUrl = Url.Action("DesignDetail", "Scrubber", new { id = design.DesignId })
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(CancellationToken.None);
                _logger.LogError(ex, "Saving AI design failed for ProjectId {Id}", project.ProjectId);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = "The design could not be saved." });
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