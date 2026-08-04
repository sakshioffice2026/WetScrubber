using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WetScrubber.Database;
using WetScrubber.Database.Enums;

using WetScrubber.Models;

namespace WetScrubber.Controllers
{
    public class ProjectController : Controller
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<ProjectController> _logger;

        public ProjectController(ApplicationDbContext dbContext, ILogger<ProjectController> logger)
        {
            _dbContext = dbContext;
            _logger    = logger;
        }

        // ── Auth helper ───────────────────────────────────────────
        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");

        private IActionResult RedirectIfNotLoggedIn()
        {
            if (GetUserId() == null)
                return RedirectToAction("Login", "Account");
            return null!;
        }

        // ── GET /Project/Index ────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Index(string? search, string? status)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var userId = GetUserId()!.Value;

            // Base query — only this user's projects
            var query = _dbContext.Projects
                                  .Where(p => p.CreatedByUserId == userId)
                                  .Include(p => p.Designs)
                                  .AsQueryable();

            // Search filter
            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                query = query.Where(p =>
                    p.ProjectName.ToLower().Contains(search)   ||
                    p.ProjectNumber.ToLower().Contains(search) ||
                    p.ClientName.ToLower().Contains(search));
            }

            // Status filter
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ProjectStatus>(status, out var parsedStatus))
            {
                query = query.Where(p => p.Status == parsedStatus);
            }

            var projects = await query
                .OrderByDescending(p => p.UpdatedAt)
                .ToListAsync();

            // Map to summary VMs
            var summaries = projects.Select(p => new ProjectSummaryViewModel
            {
                ProjectId     = p.ProjectId,
                ProjectNumber = p.ProjectNumber,
                ProjectName   = p.ProjectName,
                ClientName    = p.ClientName,
                EngineerName  = p.EngineerName,
                Status        = p.Status,
                UnitSystem    = p.UnitSystem,
                DesignCount   = p.Designs.Count,
                CreatedAt     = p.CreatedAt,
                UpdatedAt     = p.UpdatedAt
            }).ToList();

            // All projects for stats (ignore filters)
            var allProjects = await _dbContext.Projects
                .Where(p => p.CreatedByUserId == userId)
                .Include(p => p.Designs)
                .ToListAsync();

            var vm = new ProjectListViewModel
            {
                Projects          = summaries,
                TotalProjects     = allProjects.Count,
                ActiveProjects    = allProjects.Count(p => p.Status == ProjectStatus.InProgress),
                CompletedProjects = allProjects.Count(p => p.Status == ProjectStatus.Completed),
                TotalDesigns      = allProjects.Sum(p => p.Designs.Count),
                SearchTerm        = search,
                StatusFilter      = status
            };

            return View(vm);
        }

        // ── GET /Project/Create ───────────────────────────────────
        [HttpGet]
        public IActionResult Create()
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            return View(new CreateProjectViewModel());
        }

        // ── POST /Project/Create ──────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateProjectViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            if (!ModelState.IsValid)
                return View(model);

            // Check duplicate project number for this user
            bool exists = await _dbContext.Projects.AnyAsync(p =>
                p.ProjectNumber == model.ProjectNumber &&
                p.CreatedByUserId == GetUserId()!.Value);

            if (exists)
            {
                ModelState.AddModelError("ProjectNumber", "This project number already exists.");
                return View(model);
            }

            var project = new Project
            {
                ProjectNumber = model.ProjectNumber.Trim(),
                ProjectName = model.ProjectName.Trim(),
                ClientName = model.ClientName.Trim(),
                EngineerName = model.EngineerName.Trim(),
                Description = model.Description.Trim(),
                UnitSystem = model.UnitSystem,
                Status = ProjectStatus.InProgress,
                CreatedByUserId = GetUserId()!.Value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _dbContext.Projects.Add(project);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Project {Number} created by user {UserId}.",
                project.ProjectNumber, project.CreatedByUserId);

            TempData["Success"] = $"Project {project.ProjectNumber} created successfully.";
            return RedirectToAction(nameof(Detail), new { id = project.ProjectId });
        }



        // ── GET /Project/Detail/{id} ──────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Detail(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var userId = GetUserId()!.Value;

            var project = await _dbContext.Projects
                .Include(p => p.Designs)
                    .ThenInclude(d => d.Geometry)
                .FirstOrDefaultAsync(p => p.ProjectId == id && p.CreatedByUserId == userId);

            if (project == null)
            {
                TempData["Error"] = "Project not found.";
                return RedirectToAction(nameof(Index));
            }

            var vm = new ProjectDetailViewModel
            {
                ProjectId     = project.ProjectId,
                ProjectNumber = project.ProjectNumber,
                ProjectName   = project.ProjectName,
                ClientName    = project.ClientName,
                EngineerName  = project.EngineerName,
                Description   = project.Description,
                Status        = project.Status,
                UnitSystem    = project.UnitSystem,
                CreatedAt     = project.CreatedAt,
                UpdatedAt     = project.UpdatedAt,
                TotalDesigns  = project.Designs.Count,
                CompletedDesigns = project.Designs.Count(d => d.Geometry != null),

                Designs = project.Designs
                    .OrderByDescending(d => d.UpdatedAt)
                    .Select(d => new DesignSummaryViewModel
                    {
                        DesignId          = d.DesignId,
                        DesignName        = d.DesignName,
                        ScrubberType      = d.ScrubberType.ToString(),
                        ShellMaterial     = d.ShellMaterial.ToString(),
                        HasResults        = d.Geometry != null,
                        RemovalEfficiency = d.Geometry?.RemovalEfficiency ?? 0,
                        CreatedAt         = d.CreatedAt,
                        UpdatedAt         = d.UpdatedAt
                    }).ToList()
            };

            return View(vm);
        }

        // ── GET /Project/Edit/{id} ────────────────────────────────
        [HttpGet]
        // ── GET /Project/Edit/{id} ────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var userId = GetUserId()!.Value;
            var project = await _dbContext.Projects
                .FirstOrDefaultAsync(p => p.ProjectId == id && p.CreatedByUserId == userId);

            if (project == null)
            {
                TempData["Error"] = "Project not found.";
                return RedirectToAction(nameof(Index));
            }

            return View(new EditProjectViewModel
            {
                ProjectId = project.ProjectId,
                ProjectNumber = project.ProjectNumber,
                ProjectName = project.ProjectName,
                ClientName = project.ClientName,
                EngineerName = project.EngineerName,
                Description = project.Description,
                Status = project.Status,
                UnitSystem = project.UnitSystem
            });
        }

        // ── POST /Project/Edit ────────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditProjectViewModel model)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            if (!ModelState.IsValid) return View(model);

            var userId = GetUserId()!.Value;
            var project = await _dbContext.Projects
                .FirstOrDefaultAsync(p => p.ProjectId == model.ProjectId && p.CreatedByUserId == userId);

            if (project == null)
            {
                TempData["Error"] = "Project not found.";
                return RedirectToAction(nameof(Index));
            }

            // Per-user duplicate ProjectNumber check (excluding this project)
            bool duplicate = await _dbContext.Projects.AnyAsync(p =>
                p.ProjectNumber == model.ProjectNumber &&
                p.CreatedByUserId == userId &&
                p.ProjectId != model.ProjectId);

            if (duplicate)
            {
                ModelState.AddModelError(nameof(model.ProjectNumber), "You already have a project with this number.");
                return View(model);
            }

            project.ProjectNumber = model.ProjectNumber.Trim();
            project.ProjectName = model.ProjectName.Trim();
            project.ClientName = model.ClientName?.Trim() ?? string.Empty;   // null-safe
            project.EngineerName = model.EngineerName?.Trim() ?? string.Empty;   // null-safe
            project.Description = model.Description?.Trim() ?? string.Empty;   // null-safe
            project.Status = model.Status;
            project.UnitSystem = model.UnitSystem;
            project.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            TempData["Success"] = "Project updated.";
            return RedirectToAction(nameof(Detail), new { id = project.ProjectId });
        }

        // ── POST /Project/Delete/{id} ─────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var redirect = RedirectIfNotLoggedIn();
            if (redirect != null) return redirect;

            var userId  = GetUserId()!.Value;
            var project = await _dbContext.Projects
                .FirstOrDefaultAsync(p => p.ProjectId == id && p.CreatedByUserId == userId);

            if (project == null)
            {
                TempData["Error"] = "Project not found.";
                return RedirectToAction(nameof(Index));
            }

            _dbContext.Projects.Remove(project);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Project {Number} deleted by user {UserId}.",
                project.ProjectNumber, userId);

            TempData["Success"] = $"Project {project.ProjectNumber} deleted.";
            return RedirectToAction(nameof(Index));
        }
    }
}
