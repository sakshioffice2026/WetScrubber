using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WetScrubber.Business.Diagnostics;
using WetScrubber.Database;
using WetScrubber.Database.Enums;
using WetScrubber.Models;
using WetScrubber.Repositories.Interfaces;
using WetScrubber.Services;

namespace WetScrubber.Controllers
{
    public class ScrubberController : Controller
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<ScrubberController> _logger;
        private readonly ScrubberCalculationEngine _engine;
        private readonly IDesignDiagnosticsEngine _diagnosticsEngine;
        private readonly IDesignReportRepository _reportRepository;

        public ScrubberController(
            ApplicationDbContext dbContext,
            ILogger<ScrubberController> logger,
            IDesignDiagnosticsEngine diagnosticsEngine,
            IDesignReportRepository reportRepository)
        {
            _dbContext = dbContext;
            _logger = logger;
            _engine = new ScrubberCalculationEngine();
            _diagnosticsEngine = diagnosticsEngine;
            _reportRepository = reportRepository;
        }

        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");

        private IActionResult? RedirectIfNotLoggedIn()
        {
            if (GetUserId() == null)
                return RedirectToAction("Login", "Account");
            return null;
        }

        // ── GET /Scrubber/Create?projectId=5 ─────────────────────
        [HttpGet]
        public async Task<IActionResult> Create(int projectId)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var userId = GetUserId()!.Value;
            var project = await _dbContext.Projects
                .FirstOrDefaultAsync(p => p.ProjectId == projectId &&
                                          p.CreatedByUserId == userId);

            if (project == null)
            {
                TempData["Error"] = "Project not found.";
                return RedirectToAction("Index", "Project");
            }

            var createVm = new CreateDesignViewModel
            {
                ProjectId = project.ProjectId,
                ProjectNumber = project.ProjectNumber,
                ProjectName = project.ProjectName
            };
            PopulateMasterLists(createVm);
            return View(createVm);
        }

        // ── POST /Scrubber/Create ─────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateDesignViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            // Remove pollutant model-state errors — handled client-side
            var keys = ModelState.Keys.Where(k => k.StartsWith("Pollutants")).ToList();
            foreach (var k in keys) ModelState.Remove(k);

            if (!ModelState.IsValid)
            {
                var proj = await _dbContext.Projects.FirstOrDefaultAsync(p => p.ProjectId == model.ProjectId);
                if (proj != null) { model.ProjectNumber = proj.ProjectNumber; model.ProjectName = proj.ProjectName; }
                PopulateMasterLists(model);
                return View(model);
            }

            // 1. ScrubberDesign
            var design = new ScrubberDesign
            {
                ProjectId = model.ProjectId,
                DesignName = model.DesignName.Trim(),
                ScrubberType = model.ScrubberType,
                ShellMaterial = model.ShellMaterial,
                InternalMaterial = model.InternalMaterial,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.ScrubberDesigns.Add(design);
            await _dbContext.SaveChangesAsync();

            // 2. GasStream
            var gas = new GasStream
            {
                DesignId = design.DesignId,
                NormalFlowRate = model.NormalFlowRate,
                ActualFlowRate = model.ActualFlowRate,
                InletTemperature = model.InletTemperature,
                InletPressure = model.InletPressure,
                MoistureContent = model.MoistureContent,
                GasDensity = model.GasDensity,
                GasViscosity = model.GasViscosity
            };
            _dbContext.GasStreams.Add(gas);
            await _dbContext.SaveChangesAsync();

            // 3. Pollutants
            if (model.Pollutants?.Any() == true)
            {
                foreach (var p in model.Pollutants)
                {
                    _dbContext.PollutantStreams.Add(new PollutantStream
                    {
                        GasStreamId = gas.GasStreamId,
                        PollutantType = p.PollutantType,
                        InletConcentration = p.InletConcentration,
                        TargetOutletConcentration = p.TargetOutletConcentration,
                        TargetRemovalEfficiency = p.TargetRemovalEfficiency,
                        MolecularWeight = p.MolecularWeight,
                        HenrysLawConstant = p.HenrysLawConstant
                    });
                }
            }

            // 4. LiquidSpec
            _dbContext.ScrubbingLiquidSpecs.Add(new ScrubbingLiquidSpec
            {
                DesignId = design.DesignId,
                LiquidType = model.LiquidType,
                Concentration = model.LiquidConcentration,
                pH = model.LiquidPH,
                Temperature = model.LiquidTemperature,
                Density = model.LiquidDensity,
                Viscosity = model.LiquidViscosity,
                LiquidToGasRatio = model.LiquidToGasRatio
            });
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Design '{Name}' created for ProjectId {Id}.", design.DesignName, design.ProjectId);

            TempData["Success"] = $"Design '{design.DesignName}' saved. Review inputs and run calculation.";
            return RedirectToAction(nameof(DesignDetail), new { id = design.DesignId });
        }

        // ── GET /Scrubber/DesignDetail/{id} ───────────────────────

        // ── GET /Scrubber/Edit/{id} ───────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var design = await LoadDesign(id);
            if (design == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            if (design.IsLocked)
            {
                TempData["Error"] = "This design is approved and locked. Use 'Redesign as per AI narrative' to start a new revision.";
                return RedirectToAction(nameof(DesignDetail), new { id });
            }

            // Reuse BuildCreateViewModel then layer on the ids the edit form needs.
            var b = BuildCreateViewModel(design);
            var vm = new EditDesignViewModel
            {
                DesignId = design.DesignId,
                ProjectId = design.ProjectId,
                ProjectNumber = design.Project.ProjectNumber,
                ProjectName = design.Project.ProjectName,
                DesignName = b.DesignName,
                ScrubberType = b.ScrubberType,
                ShellMaterial = b.ShellMaterial,
                InternalMaterial = b.InternalMaterial,
                NormalFlowRate = b.NormalFlowRate,
                ActualFlowRate = b.ActualFlowRate,
                InletTemperature = b.InletTemperature,
                InletPressure = b.InletPressure,
                MoistureContent = b.MoistureContent,
                GasDensity = b.GasDensity,
                GasViscosity = b.GasViscosity,
                LiquidType = b.LiquidType,
                LiquidConcentration = b.LiquidConcentration,
                LiquidPH = b.LiquidPH,
                LiquidTemperature = b.LiquidTemperature,
                LiquidDensity = b.LiquidDensity,
                LiquidViscosity = b.LiquidViscosity,
                LiquidToGasRatio = b.LiquidToGasRatio,
                Pollutants = b.Pollutants,
                Diagnostics = BuildDiagnosticsViewModels(design)
            };

            PopulateMasterLists(vm);          // fills PollutantOptions / LiquidOptions
            return View(vm);
        }

        // ── POST /Scrubber/Edit ───────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditDesignViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            // Pollutant rows are validated client-side.
            var keys = ModelState.Keys.Where(k => k.StartsWith("Pollutants")).ToList();
            foreach (var k in keys) ModelState.Remove(k);

            var design = await LoadDesign(model.DesignId);
            if (design == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            if (design.IsLocked)
            {
                TempData["Error"] = "This design is approved and locked. Use 'Redesign as per AI narrative' to start a new revision.";
                return RedirectToAction(nameof(DesignDetail), new { id = design.DesignId });
            }

            if (!ModelState.IsValid)
            {
                model.ProjectId = design.ProjectId;
                model.ProjectNumber = design.Project.ProjectNumber;
                model.ProjectName = design.Project.ProjectName;
                PopulateMasterLists(model);
                return View(model);
            }

            // 1. Design scalars
            design.DesignName = model.DesignName.Trim();
            design.ScrubberType = model.ScrubberType;
            design.ShellMaterial = model.ShellMaterial;
            design.InternalMaterial = model.InternalMaterial;
            design.UpdatedAt = DateTime.UtcNow;

            // 2. Gas stream
            if (design.GasStream == null)
            {
                design.GasStream = new GasStream { DesignId = design.DesignId };
                _dbContext.GasStreams.Add(design.GasStream);
            }
            design.GasStream.NormalFlowRate = model.NormalFlowRate;
            design.GasStream.ActualFlowRate = model.ActualFlowRate;
            design.GasStream.InletTemperature = model.InletTemperature;
            design.GasStream.InletPressure = model.InletPressure;
            design.GasStream.MoistureContent = model.MoistureContent;
            design.GasStream.GasDensity = model.GasDensity;
            design.GasStream.GasViscosity = model.GasViscosity;

            // 3. Pollutants — replace the whole set (PollutantType is now an int id)
            if (design.GasStream.Pollutants.Any())
                _dbContext.PollutantStreams.RemoveRange(design.GasStream.Pollutants);

            if (model.Pollutants?.Any() == true)
            {
                foreach (var p in model.Pollutants)
                {
                    design.GasStream.Pollutants.Add(new PollutantStream
                    {
                        GasStreamId = design.GasStream.GasStreamId,
                        PollutantType = p.PollutantType,   // int FK -> pollutants.Id
                        InletConcentration = p.InletConcentration,
                        TargetOutletConcentration = p.TargetOutletConcentration,
                        TargetRemovalEfficiency = p.TargetRemovalEfficiency,
                        MolecularWeight = p.MolecularWeight,
                        HenrysLawConstant = p.HenrysLawConstant
                    });
                }
            }

            // 4. Scrubbing liquid
            if (design.LiquidSpec == null)
            {
                design.LiquidSpec = new ScrubbingLiquidSpec { DesignId = design.DesignId };
                _dbContext.ScrubbingLiquidSpecs.Add(design.LiquidSpec);
            }
            design.LiquidSpec.LiquidType = model.LiquidType;    // int FK -> scrubbingliquids.Id
            design.LiquidSpec.Concentration = model.LiquidConcentration;
            design.LiquidSpec.pH = model.LiquidPH;
            design.LiquidSpec.Temperature = model.LiquidTemperature;
            design.LiquidSpec.Density = model.LiquidDensity;
            design.LiquidSpec.Viscosity = model.LiquidViscosity;
            design.LiquidSpec.LiquidToGasRatio = model.LiquidToGasRatio;

            // 5. Inputs changed → previous results are stale; remove them.
            if (design.Geometry != null)
                _dbContext.ScrubberGeometries.Remove(design.Geometry);

            await _dbContext.SaveChangesAsync();

            TempData["Success"] = $"Design '{design.DesignName}' updated. Re-run the calculation to refresh results.";
            return RedirectToAction(nameof(DesignDetail), new { id = design.DesignId });
        }



        [HttpGet]
        public async Task<IActionResult> DesignDetail(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var design = await LoadDesign(id);
            if (design == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            var report = await _reportRepository.GetByDesignIdAsync(id);
            var vm = BuildDetailViewModel(design, report);
            vm.Diagnostics = BuildDiagnosticsViewModels(design);
            return View(vm);
        }

        // ── POST /Scrubber/RunCalculation/{id} ────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunCalculation(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var design = await LoadDesign(id);
            if (design == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            // Build ViewModel from DB data to pass into engine
            var vm = BuildCreateViewModel(design);

            // Run the calculation
            CalculationResult calcResult;
            try
            {
                calcResult = _engine.RunCalculation(vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Calculation failed for DesignId {Id}.", id);
                TempData["Error"] = "Calculation failed: " + ex.Message;
                return RedirectToAction(nameof(DesignDetail), new { id });
            }

            // Save results to ScrubberGeometry
            var existing = await _dbContext.ScrubberGeometries
                .FirstOrDefaultAsync(g => g.DesignId == id);

            if (existing != null)
            {
                existing.TowerDiameter = calcResult.TowerDiameter;
                existing.TowerHeight = calcResult.TowerHeight;
                existing.PackingHeight = calcResult.PackingHeight;
                existing.PressureDrop = calcResult.PressureDrop;
                existing.RemovalEfficiency = calcResult.RemovalEfficiency;
                existing.AbsorptionFactor = calcResult.AbsorptionFactor;
                existing.ActualLGRatio = calcResult.ActualLGRatio;
                existing.MinLGRatio = calcResult.MinLGRatio;
                existing.GasVelocity = calcResult.GasVelocity;
            }
            else
            {
                _dbContext.ScrubberGeometries.Add(new ScrubberGeometry
                {
                    DesignId = id,
                    TowerDiameter = calcResult.TowerDiameter,
                    TowerHeight = calcResult.TowerHeight,
                    PackingHeight = calcResult.PackingHeight,
                    PressureDrop = calcResult.PressureDrop,
                    RemovalEfficiency = calcResult.RemovalEfficiency,
                    AbsorptionFactor = calcResult.AbsorptionFactor,
                    ActualLGRatio = calcResult.ActualLGRatio,
                    MinLGRatio = calcResult.MinLGRatio,
                    GasVelocity = calcResult.GasVelocity
                });
            }

            // Update design UpdatedAt
            design.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Calculation complete for DesignId {Id}. Efficiency={Eff}%",
                id, calcResult.RemovalEfficiency);

            // Store full result in TempData for Results view
            TempData["CalcResult"] = System.Text.Json.JsonSerializer.Serialize(calcResult);

            return RedirectToAction(nameof(Results), new { id });
        }

        // ── GET /Scrubber/Results/{id} ────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Results(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var design = await LoadDesign(id);
            if (design == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            // Try to get latest calc result from TempData first
            CalculationResult? calcResult = null;
            if (TempData["CalcResult"] is string json)
            {
                calcResult = System.Text.Json.JsonSerializer.Deserialize<CalculationResult>(json);
            }

            // If no TempData (page refresh), re-run calculation from DB data
            if (calcResult == null && design.Geometry != null)
            {
                var vm = BuildCreateViewModel(design);
                calcResult = _engine.RunCalculation(vm);
            }

            if (calcResult == null)
            {
                TempData["Error"] = "No results found. Please run the calculation first.";
                return RedirectToAction(nameof(DesignDetail), new { id });
            }

            var report = await _reportRepository.GetByDesignIdAsync(id);
            ViewBag.Design = BuildDetailViewModel(design, report);
            ViewBag.CalcResult = calcResult;

            return View();
        }

        // ── GET /Scrubber/ChemicalReactions ──────────────────────
        [HttpGet]
        public IActionResult ChemicalReactions()
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;
            return View();
        }

        // ── POST /Scrubber/Delete/{id} ────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var userId = GetUserId()!.Value;
            var design = await _dbContext.ScrubberDesigns
                .Include(d => d.Project)
                .FirstOrDefaultAsync(d => d.DesignId == id &&
                                          d.Project.CreatedByUserId == userId);

            if (design == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            int projectId = design.ProjectId;
            _dbContext.ScrubberDesigns.Remove(design);
            await _dbContext.SaveChangesAsync();

            TempData["Success"] = $"Design '{design.DesignName}' deleted.";
            return RedirectToAction("Detail", "Project", new { id = projectId });
        }

        // ── POST /Scrubber/CreateRevision/{id} ─────────────────────
        // "Redesign as per AI narrative": only reachable once a report has
        // been generated for the design (Generate Report first). Clones
        // inputs — gas stream, pollutants, liquid spec — into a brand-new
        // Draft design so the engineer can act on the report's findings
        // without mutating the original. Results/geometry are NOT copied
        // — they're stale until RunCalculation is executed again on the
        // new revision.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateRevision(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var source = await LoadDesign(id);
            if (source == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            var existingReport = await _reportRepository.GetByDesignIdAsync(id);
            if (existingReport == null)
            {
                TempData["Error"] = "Generate a report for this design before redesigning.";
                return RedirectToAction(nameof(DesignDetail), new { id });
            }

            var revision = new ScrubberDesign
            {
                ProjectId = source.ProjectId,
                DesignName = NextRevisionName(source.DesignName),
                ScrubberType = source.ScrubberType,
                ShellMaterial = source.ShellMaterial,
                InternalMaterial = source.InternalMaterial,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ReviewStatus = DesignReviewStatus.Draft,
                IsLocked = false,
                PreviousDesignId = source.DesignId,
                RevisionNumber = source.RevisionNumber + 1
            };
            _dbContext.ScrubberDesigns.Add(revision);
            await _dbContext.SaveChangesAsync();

            if (source.GasStream != null)
            {
                var gas = new GasStream
                {
                    DesignId = revision.DesignId,
                    NormalFlowRate = source.GasStream.NormalFlowRate,
                    ActualFlowRate = source.GasStream.ActualFlowRate,
                    InletTemperature = source.GasStream.InletTemperature,
                    InletPressure = source.GasStream.InletPressure,
                    MoistureContent = source.GasStream.MoistureContent,
                    GasDensity = source.GasStream.GasDensity,
                    GasViscosity = source.GasStream.GasViscosity
                };
                _dbContext.GasStreams.Add(gas);
                await _dbContext.SaveChangesAsync();

                foreach (var p in source.GasStream.Pollutants)
                {
                    _dbContext.PollutantStreams.Add(new PollutantStream
                    {
                        GasStreamId = gas.GasStreamId,
                        PollutantType = p.PollutantType,
                        InletConcentration = p.InletConcentration,
                        TargetOutletConcentration = p.TargetOutletConcentration,
                        TargetRemovalEfficiency = p.TargetRemovalEfficiency,
                        MolecularWeight = p.MolecularWeight,
                        HenrysLawConstant = p.HenrysLawConstant
                    });
                }
            }

            if (source.LiquidSpec != null)
            {
                _dbContext.ScrubbingLiquidSpecs.Add(new ScrubbingLiquidSpec
                {
                    DesignId = revision.DesignId,
                    LiquidType = source.LiquidSpec.LiquidType,
                    Concentration = source.LiquidSpec.Concentration,
                    pH = source.LiquidSpec.pH,
                    Temperature = source.LiquidSpec.Temperature,
                    Density = source.LiquidSpec.Density,
                    Viscosity = source.LiquidSpec.Viscosity,
                    LiquidToGasRatio = source.LiquidSpec.LiquidToGasRatio
                });
            }

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Revision '{New}' (DesignId {NewId}) created from locked DesignId {OldId} for redesign.",
                revision.DesignName, revision.DesignId, id);

            TempData["Success"] = $"New revision '{revision.DesignName}' created from the approved design. " +
                                   "Edit the inputs below per the AI narrative's recommendations, then re-run the calculation.";
            return RedirectToAction(nameof(Edit), new { id = revision.DesignId });
        }

        // ── GET /Scrubber/Compare/{id} ─────────────────────────────
        // {id} is the NEW (redesigned) design. Diffs it against
        // PreviousDesignId — inputs, results, and which findings on the
        // OLD design this revision actually addressed — plus the two
        // narrative blocks side by side.
        [HttpGet]
        public async Task<IActionResult> Compare(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var newDesign = await LoadDesign(id);
            if (newDesign == null)
            {
                TempData["Error"] = "Design not found.";
                return RedirectToAction("Index", "Project");
            }

            if (newDesign.PreviousDesignId == null)
            {
                TempData["Error"] = "This design has no previous revision to compare against.";
                return RedirectToAction(nameof(DesignDetail), new { id });
            }

            var oldDesign = await LoadDesign(newDesign.PreviousDesignId.Value);
            if (oldDesign == null)
            {
                TempData["Error"] = "The previous revision could not be found.";
                return RedirectToAction(nameof(DesignDetail), new { id });
            }

            // Findings from the OLD design are what a redesign is judged
            // against: for each affected field, did the new value move
            // toward what was recommended?
            var oldFindings = DesignDiagnosticsMapper.Evaluate(oldDesign, _diagnosticsEngine);
            var newFindingsVm = BuildDiagnosticsViewModels(newDesign);

            var vm = new DesignCompareViewModel
            {
                OldDesignId = oldDesign.DesignId,
                OldDesignName = oldDesign.DesignName,
                NewDesignId = newDesign.DesignId,
                NewDesignName = newDesign.DesignName,
                NewRevisionNumber = newDesign.RevisionNumber,
                NewDiagnostics = newFindingsVm,
                Rows = BuildCompareRows(oldDesign, newDesign, oldFindings)
            };

            var oldReport = await _reportRepository.GetByDesignIdAsync(oldDesign.DesignId);
            var newReport = await _reportRepository.GetByDesignIdAsync(newDesign.DesignId);

            // Approved narrative no longer exists as a step in the flow —
            // fall back through AI-drafted to the deterministic template so
            // this panel isn't blank just because nobody approved anything.
            vm.OldApprovedNarrative = oldReport?.ApprovedNarrative
                ?? oldReport?.AiNarrative
                ?? oldReport?.TemplateNarrative;
            vm.NewAiNarrative = newReport?.AiNarrative;
            vm.NewApprovedNarrative = newReport?.ApprovedNarrative
                ?? newReport?.AiNarrative
                ?? newReport?.TemplateNarrative;
            vm.NewReportStatus = newReport?.Status.ToString() ?? "Not started";

            return View(vm);
        }

        // Builds the field-by-field diff. "MatchesRecommendation" only
        // gets a value for a field an OLD-design finding actually named —
        // every other field is a plain before/after with no judgement
        // attached, since there was nothing to recommend on it.
        private static List<CompareRowViewModel> BuildCompareRows(
            ScrubberDesign oldDesign, ScrubberDesign newDesign, IReadOnlyList<DesignFinding> oldFindings)
        {
            var rows = new List<CompareRowViewModel>();

            void AddRow(string field, string label, string unit, double oldVal, double newVal)
            {
                var relevant = oldFindings.FirstOrDefault(f => f.AffectedFields.Contains(field));
                bool? matches = null;
                if (relevant?.SuggestedValue is double target)
                {
                    // Every quantified suggestion in this rule table is a
                    // "raise it to at least X" recommendation, so reaching
                    // that target is what counts as addressed.
                    matches = newVal >= target;
                }

                rows.Add(new CompareRowViewModel
                {
                    Label = label,
                    Unit = unit,
                    OldValue = oldVal,
                    NewValue = newVal,
                    MatchesRecommendation = matches
                });
            }

            AddRow("NormalFlowRate", "Normal Gas Flow", "Nm³/hr", oldDesign.GasStream?.NormalFlowRate ?? 0, newDesign.GasStream?.NormalFlowRate ?? 0);
            AddRow("ActualFlowRate", "Actual Gas Flow", "m³/hr", oldDesign.GasStream?.ActualFlowRate ?? 0, newDesign.GasStream?.ActualFlowRate ?? 0);
            AddRow("LiquidToGasRatio", "L/G Ratio", "L/m³", oldDesign.LiquidSpec?.LiquidToGasRatio ?? 0, newDesign.LiquidSpec?.LiquidToGasRatio ?? 0);
            AddRow("LiquidPH", "Liquid pH", "", oldDesign.LiquidSpec?.pH ?? 0, newDesign.LiquidSpec?.pH ?? 0);

            // Results — old is frozen (locked), new is whatever the last
            // RunCalculation on the revision produced (0 until then).
            AddRow("TowerDiameter", "Tower Diameter", "m", oldDesign.Geometry?.TowerDiameter ?? 0, newDesign.Geometry?.TowerDiameter ?? 0);
            AddRow("TowerHeight", "Tower Height", "m", oldDesign.Geometry?.TowerHeight ?? 0, newDesign.Geometry?.TowerHeight ?? 0);
            AddRow("PressureDrop", "Pressure Drop", "Pa", oldDesign.Geometry?.PressureDrop ?? 0, newDesign.Geometry?.PressureDrop ?? 0);
            AddRow("RemovalEfficiency", "Removal Efficiency", "%", oldDesign.Geometry?.RemovalEfficiency ?? 0, newDesign.Geometry?.RemovalEfficiency ?? 0);

            return rows;
        }

        // "Cooling Tower A" -> "Cooling Tower A (Rev 2)" -> "... (Rev 3)" ...
        private string NextRevisionName(string currentName)
        {
            var match = System.Text.RegularExpressions.Regex.Match(currentName, @"^(.*)\(Rev (\d+)\)$");
            if (match.Success)
            {
                var basePart = match.Groups[1].Value.TrimEnd();
                var nextNum = int.Parse(match.Groups[2].Value) + 1;
                return $"{basePart} (Rev {nextNum})";
            }
            return $"{currentName} (Rev 2)";
        }

        // ── Private helpers ───────────────────────────────────────
        private async Task<ScrubberDesign?> LoadDesign(int id)
        {
            var userId = GetUserId()!.Value;
            return await _dbContext.ScrubberDesigns
                .Include(d => d.Project)
                .Include(d => d.GasStream).ThenInclude(g => g!.Pollutants)
                .Include(d => d.LiquidSpec)
                .Include(d => d.Geometry)
                .FirstOrDefaultAsync(d => d.DesignId == id &&
                                          d.Project.CreatedByUserId == userId);
        }

        private DesignDetailViewModel BuildDetailViewModel(ScrubberDesign d, DesignReport? report = null) => new()
        {
            DesignId = d.DesignId,
            ProjectId = d.ProjectId,
            ProjectNumber = d.Project.ProjectNumber,
            ProjectName = d.Project.ProjectName,
            DesignName = d.DesignName,
            ScrubberType = d.ScrubberType.ToString(),
            ShellMaterial = d.ShellMaterial.ToString(),
            InternalMaterial = d.InternalMaterial.ToString(),
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
            NormalFlowRate = d.GasStream?.NormalFlowRate ?? 0,
            ActualFlowRate = d.GasStream?.ActualFlowRate ?? 0,
            InletTemperature = d.GasStream?.InletTemperature ?? 0,
            InletPressure = d.GasStream?.InletPressure ?? 0,
            MoistureContent = d.GasStream?.MoistureContent ?? 0,
            GasDensity = d.GasStream?.GasDensity ?? 0,
            LiquidType = d.LiquidSpec?.LiquidType.ToString() ?? "-",
            LiquidPH = d.LiquidSpec?.pH ?? 0,
            LiquidConcentration = d.LiquidSpec?.Concentration ?? 0,
            LiquidToGasRatio = d.LiquidSpec?.LiquidToGasRatio ?? 0,
            Pollutants = d.GasStream?.Pollutants.Select(p => new PollutantInputViewModel
            {
                PollutantType = p.PollutantType,
                InletConcentration = p.InletConcentration,
                TargetOutletConcentration = p.TargetOutletConcentration,
                TargetRemovalEfficiency = p.TargetRemovalEfficiency,
                MolecularWeight = p.MolecularWeight,
                HenrysLawConstant = p.HenrysLawConstant
            }).ToList() ?? new(),
            HasResults = d.Geometry != null,
            TowerDiameter = d.Geometry?.TowerDiameter ?? 0,
            TowerHeight = d.Geometry?.TowerHeight ?? 0,
            PackingHeight = d.Geometry?.PackingHeight ?? 0,
            PressureDrop = d.Geometry?.PressureDrop ?? 0,
            RemovalEfficiency = d.Geometry?.RemovalEfficiency ?? 0,
            IsLocked = d.IsLocked,
            PreviousDesignId = d.PreviousDesignId,
            RevisionNumber = d.RevisionNumber,
            HasReport = report != null,
            ReportId = report?.ReportId
        };

        // Runs the deterministic diagnostics rule table against a loaded
        // design and maps it onto the lightweight view-model DTO. Used by
        // both DesignDetail and Edit so the same findings — same
        // wording, same field tags, same suggested values — show up
        // wherever the engineer is looking, not just inside a report.
        private List<DesignFindingViewModel> BuildDiagnosticsViewModels(ScrubberDesign d)
        {
            var findings = DesignDiagnosticsMapper.Evaluate(d, _diagnosticsEngine);

            return findings.Select(f => new DesignFindingViewModel
            {
                Code = f.Code,
                Severity = f.Severity.ToString(),
                Symptom = f.Symptom,
                Diagnosis = f.Diagnosis,
                Recommendation = f.Recommendation,
                AffectedFields = f.AffectedFields.ToList(),
                SuggestedValue = f.SuggestedValue,
                SuggestedValueLabel = f.SuggestedValueLabel
            }).ToList();
        }

        // Fill pollutant + liquid dropdowns from the master tables.
        // Works for CreateDesignViewModel and its subclass EditDesignViewModel.
        private void PopulateMasterLists(CreateDesignViewModel vm)
        {
            vm.PollutantOptions = _dbContext.Pollutants
                .Where(p => p.IsActive).OrderBy(p => p.Id).ToList();
            vm.LiquidOptions = _dbContext.ScrubbingLiquids
                .Where(l => l.IsActive).OrderBy(l => l.Id).ToList();
        }

        private CreateDesignViewModel BuildCreateViewModel(ScrubberDesign d)
        {
            var pollutants = d.GasStream?.Pollutants.Select(p => new PollutantInputViewModel
            {
                PollutantType = p.PollutantType,
                InletConcentration = p.InletConcentration,
                TargetOutletConcentration = p.TargetOutletConcentration,
                TargetRemovalEfficiency = p.TargetRemovalEfficiency,
                MolecularWeight = p.MolecularWeight,
                HenrysLawConstant = p.HenrysLawConstant
            }).ToList() ?? new List<PollutantInputViewModel> { new() };

            return new CreateDesignViewModel
            {
                ProjectId = d.ProjectId,
                DesignName = d.DesignName,
                ScrubberType = d.ScrubberType,
                ShellMaterial = d.ShellMaterial,
                InternalMaterial = d.InternalMaterial,
                NormalFlowRate = d.GasStream?.NormalFlowRate ?? 1000,
                ActualFlowRate = d.GasStream?.ActualFlowRate ?? 1200,
                InletTemperature = d.GasStream?.InletTemperature ?? 150,
                InletPressure = d.GasStream?.InletPressure ?? 101325,
                MoistureContent = d.GasStream?.MoistureContent ?? 0,
                GasDensity = d.GasStream?.GasDensity > 0 ? d.GasStream.GasDensity : 1.2,
                GasViscosity = d.GasStream?.GasViscosity > 0 ? d.GasStream.GasViscosity : 1.85e-5,
                LiquidType = d.LiquidSpec?.LiquidType ?? 2,   // 2 = Caustic Soda (scrubbingliquids.Id)
                LiquidConcentration = d.LiquidSpec?.Concentration ?? 10,
                LiquidPH = d.LiquidSpec?.pH ?? 12,
                LiquidTemperature = d.LiquidSpec?.Temperature ?? 25,
                LiquidDensity = d.LiquidSpec?.Density > 0 ? d.LiquidSpec.Density : 1050,
                LiquidViscosity = d.LiquidSpec?.Viscosity > 0 ? d.LiquidSpec.Viscosity : 1.0,
                LiquidToGasRatio = d.LiquidSpec?.LiquidToGasRatio > 0 ? d.LiquidSpec.LiquidToGasRatio : 3.0,
                Pollutants = pollutants
            };
        }
    }
}