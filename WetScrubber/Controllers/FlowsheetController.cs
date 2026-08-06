using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WetScrubber.Business.Flowsheet;
using WetScrubber.Database;
using WetScrubber.Models;

namespace WetScrubber.Controllers
{
    // Phase 4c — CRUD for flowsheets (chained unit ops) and running them
    // through FlowsheetTopologicalSolver (Phase 4c/4d: DAG order + tear
    // stream convergence for recycle connections).
    public class FlowsheetController : Controller
    {
        private readonly ApplicationDbContext _dbContext;
        public FlowsheetController(ApplicationDbContext dbContext) => _dbContext = dbContext;

        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");

        private IActionResult? RedirectIfNotLoggedIn()
        {
            if (GetUserId() == null)
                return RedirectToAction("Login", "Account");
            return null;
        }

        // ── GET /Flowsheet ─────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Index(int? projectId)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var query = _dbContext.Flowsheets
             .Include(f => f.Project)
             .Include(f => f.UnitOperations)
             .Include(f => f.StreamConnections)
             .AsQueryable();

            ViewBag.ProjectId = projectId;
            return View(await query.OrderByDescending(f => f.UpdatedAt).ToListAsync());
        }

        // ── GET /Flowsheet/Create ──────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Create(int? projectId)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            ViewBag.Projects = await _dbContext.Projects.ToListAsync();
            return View(new FlowsheetFormViewModel { ProjectId = projectId ?? 0 });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(FlowsheetFormViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            if (!ModelState.IsValid)
            {
                ViewBag.Projects = await _dbContext.Projects.ToListAsync();
                return View(model);
            }

            var entity = new FlowsheetEntity
            {
                Name = model.Name.Trim(),
                Description = model.Description?.Trim() ?? "",
                ProjectId = model.ProjectId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _dbContext.Flowsheets.Add(entity);
            await _dbContext.SaveChangesAsync();

            TempData["Success"] = "Flowsheet created.";
            return RedirectToAction(nameof(Detail), new { id = entity.Id });
        }

        // ── GET /Flowsheet/Detail/5 ─────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Detail(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var flowsheet = await _dbContext.Flowsheets
                .Include(f => f.Project)
                .Include(f => f.UnitOperations)
                .Include(f => f.StreamConnections)
                .FirstOrDefaultAsync(f => f.Id == id);

            if (flowsheet == null)
            {
                TempData["Error"] = "Flowsheet not found.";
                return RedirectToAction(nameof(Index));
            }

            return View(flowsheet);
        }

        // ── POST /Flowsheet/Delete/5 ────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var flowsheet = await _dbContext.Flowsheets
                .Include(f => f.UnitOperations)
                .Include(f => f.StreamConnections)
                .FirstOrDefaultAsync(f => f.Id == id);

            if (flowsheet != null)
            {
                _dbContext.StreamConnections.RemoveRange(flowsheet.StreamConnections);
                _dbContext.UnitOperations.RemoveRange(flowsheet.UnitOperations);
                _dbContext.Flowsheets.Remove(flowsheet);
                await _dbContext.SaveChangesAsync();
                TempData["Success"] = "Flowsheet deleted.";
            }

            return RedirectToAction(nameof(Index));
        }

        // ── POST /Flowsheet/AddUnit ─────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddUnit(UnitOperationFormViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            if (string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.Type))
            {
                TempData["Error"] = "Unit name and type are required.";
                return RedirectToAction(nameof(Detail), new { id = model.FlowsheetId });
            }

            _dbContext.UnitOperations.Add(new UnitOperationEntity
            {
                FlowsheetId = model.FlowsheetId,
                Name = model.Name.Trim(),
                Type = model.Type.Trim().ToLowerInvariant(),
                SequenceOrder = model.SequenceOrder,
                ConfigJson = model.ConfigJson?.Trim() ?? "{}"
            });
            await TouchAndSave(model.FlowsheetId);

            TempData["Success"] = "Unit operation added.";
            return RedirectToAction(nameof(Detail), new { id = model.FlowsheetId });
        }

        // ── POST /Flowsheet/DeleteUnit ───────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUnit(int id, int flowsheetId)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var unit = await _dbContext.UnitOperations.FindAsync(id);
            if (unit != null)
            {
                var relatedConnections = _dbContext.StreamConnections
                    .Where(c => c.SourceUnitId == id || c.SinkUnitId == id);
                _dbContext.StreamConnections.RemoveRange(relatedConnections);
                _dbContext.UnitOperations.Remove(unit);
                await TouchAndSave(flowsheetId);
            }

            return RedirectToAction(nameof(Detail), new { id = flowsheetId });
        }

        // ── POST /Flowsheet/AddConnection ────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddConnection(StreamConnectionFormViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            if (model.SourceUnitId == model.SinkUnitId)
            {
                TempData["Error"] = "Source and sink must be different units.";
                return RedirectToAction(nameof(Detail), new { id = model.FlowsheetId });
            }

            _dbContext.StreamConnections.Add(new StreamConnectionEntity
            {
                FlowsheetId = model.FlowsheetId,
                SourceUnitId = model.SourceUnitId,
                SinkUnitId = model.SinkUnitId,
                StreamType = model.StreamType.Trim().ToLowerInvariant()
            });
            await TouchAndSave(model.FlowsheetId);

            TempData["Success"] = "Connection added.";
            return RedirectToAction(nameof(Detail), new { id = model.FlowsheetId });
        }

        // ── POST /Flowsheet/DeleteConnection ─────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConnection(int id, int flowsheetId)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var conn = await _dbContext.StreamConnections.FindAsync(id);
            if (conn != null)
            {
                _dbContext.StreamConnections.Remove(conn);
                await TouchAndSave(flowsheetId);
            }

            return RedirectToAction(nameof(Detail), new { id = flowsheetId });
        }

        // ── POST /Flowsheet/Run ───────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(FlowsheetRunFormViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var flowsheet = await _dbContext.Flowsheets
                .Include(f => f.UnitOperations)
                .Include(f => f.StreamConnections)
                .FirstOrDefaultAsync(f => f.Id == model.Id);

            if (flowsheet == null)
            {
                TempData["Error"] = "Flowsheet not found.";
                return RedirectToAction(nameof(Index));
            }

            if (flowsheet.UnitOperations.Count == 0)
            {
                TempData["Error"] = "Add at least one unit operation before running.";
                return RedirectToAction(nameof(Detail), new { id = model.Id });
            }

            try
            {
                var feed = new FlowsheetPorts
                {
                    Gas = new ProcessStream
                    {
                        ActualFlowM3Hr = model.ActualFlowM3Hr,
                        TemperatureC = model.TemperatureC,
                        PressurePa = model.PressurePa,
                        PollutantPpmByCode = ParsePollutants(model.Pollutants)
                    },
                    Liquid = new LiquidStream
                    {
                        MassFlowKgS = model.LiquidFlowKgS,
                        TemperatureC = model.LiquidTemperatureC,
                        PollutantLoadingKgKg = new Dictionary<string, double>()
                    }
                };

                var output = SolveFlowsheet(flowsheet, feed, model.LiquidRecycleFraction);

                ViewBag.Flowsheet = flowsheet;
                ViewBag.Feed = feed;
                return View("RunResult", output);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Run failed: {ex.Message}";
                return RedirectToAction(nameof(Detail), new { id = model.Id });
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════
        private async Task TouchAndSave(int flowsheetId)
        {
            var flowsheet = await _dbContext.Flowsheets.FindAsync(flowsheetId);
            if (flowsheet != null) flowsheet.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        private static Dictionary<string, double> ParsePollutants(string raw)
        {
            var result = new Dictionary<string, double>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && double.TryParse(parts[1].Trim(), out var ppm))
                    result[parts[0].Trim()] = ppm;
            }

            return result;
        }

        private static FlowsheetTopologicalSolver.SolveOutput SolveFlowsheet(
            FlowsheetEntity flowsheet, FlowsheetPorts feed, double liquidRecycleFraction)
        {
            var orderedUnits = flowsheet.UnitOperations.OrderBy(u => u.SequenceOrder).ToList();

            var nodes = orderedUnits
                .Select(u => new FlowsheetTopologicalSolver.UnitNode
                {
                    Name = u.Name,
                    Operation = BuildUnit(u)
                })
                .ToList();

            var edgeConnections = flowsheet.StreamConnections
                .Where(c => c.StreamType != "recycle")
                .ToList();
            bool hasRecycle = flowsheet.StreamConnections.Any(c => c.StreamType == "recycle");

            if (edgeConnections.Count > 0)
            {
                var nameById = orderedUnits.ToDictionary(u => u.Id, u => u.Name);
                var nodeByName = nodes.ToDictionary(n => n.Name);

                foreach (var c in edgeConnections)
                {
                    if (!nameById.TryGetValue(c.SourceUnitId, out var srcName)) continue;
                    if (!nameById.TryGetValue(c.SinkUnitId, out var sinkName)) continue;
                    if (!nodeByName.TryGetValue(srcName, out var srcNode)) continue;
                    if (!nodeByName.TryGetValue(sinkName, out var sinkNode)) continue;

                    srcNode.OutletConnections.Add(sinkName);
                    sinkNode.InletConnections.Add(srcName);
                }
            }
            else
            {
                // No explicit connections drawn — fall back to a simple
                // chain in SequenceOrder so a design still runs.
                for (int i = 0; i < nodes.Count - 1; i++)
                {
                    nodes[i].OutletConnections.Add(nodes[i + 1].Name);
                    nodes[i + 1].InletConnections.Add(nodes[i].Name);
                }
            }

            var solveInput = new FlowsheetTopologicalSolver.SolveInput
            {
                Units = nodes,
                FeedPorts = feed,
                TearStreamNames = hasRecycle ? new List<string> { "recycle" } : new List<string>(),
                LiquidRecycleFraction = liquidRecycleFraction
            };

            return FlowsheetTopologicalSolver.Solve(solveInput);
        }

        private static IUnitOperation BuildUnit(UnitOperationEntity entity)
        {
            var cfg = string.IsNullOrWhiteSpace(entity.ConfigJson)
                ? new Dictionary<string, double>()
                : JsonSerializer.Deserialize<Dictionary<string, double>>(entity.ConfigJson) ?? new();

            double Get(string key, double fallback) => cfg.TryGetValue(key, out var v) ? v : fallback;

            return entity.Type switch
            {
                "cooler" => new CoolerUnitOp
                {
                    Name = entity.Name,
                    CoolingDutyKW = Get("CoolingDutyKW", 50)
                },
                "separator" => new SeparatorUnitOp
                {
                    Name = entity.Name,
                    SeparationEfficiency = Get("SeparationEfficiency", 0.98)
                },
                "precooler" => new PreCoolerUnit(Get("OutletTemperatureC", 35)),
                "misteliminator" => new MistEliminatorUnit(Get("PressureDropPa", 250)),
                "scrubber" => new ScrubberUnitOp
                {
                    Name = entity.Name,
                    TowerHeightM = Get("TowerHeightM", 5),
                    TowerAreaM2 = Get("TowerAreaM2", 2),
                    LiquidFlowKgS = Get("LiquidFlowKgS", 10),
                    LiquidInletTempC = Get("LiquidInletTempC", 25),
                    GasDensityKgM3 = Get("GasDensityKgM3", 1.2),
                    PackingSpecificAreaM2M3 = Get("PackingSpecificAreaM2M3", 250),
                    PackingNominalSizeM = Get("PackingNominalSizeM", 0.025)
                },
                _ => throw new InvalidOperationException($"Unknown unit type '{entity.Type}'")
            };
        }
    }
}