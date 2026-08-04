using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WetScrubber.Database;
using WetScrubber.Database.Enums;
using WetScrubber.Models;
using WetScrubber.Services;

namespace WetScrubber.Controllers
{
    public class ScrubberController : Controller
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<ScrubberController> _logger;
        private readonly ScrubberCalculationEngine _engine;

        public ScrubberController(
            ApplicationDbContext dbContext,
            ILogger<ScrubberController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
            _engine = new ScrubberCalculationEngine();
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
                Pollutants = b.Pollutants
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

            return View(BuildDetailViewModel(design));
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

            ViewBag.Design = BuildDetailViewModel(design);
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

        private DesignDetailViewModel BuildDetailViewModel(ScrubberDesign d) => new()
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
            RemovalEfficiency = d.Geometry?.RemovalEfficiency ?? 0
        };

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