using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WetScrubber.Database;
using WetScrubber.Models;
using WetScrubber.Repositories.Contracts;
using WetScrubber.Repositories.Repositories;
using WetScrubber.Services;

namespace WetScrubber.Controllers
{
    // Example of the new pattern: the controller depends on IUnitOfWork only,
    // never on ApplicationDbContext. All DB work is inside the repositories.
    public class ChemistryController : Controller
    {
        private readonly UnitOfWorks _uow;
        private readonly ChemistryUIService _chemistryUIService;

        public ChemistryController(IUnitOfWork uow, ChemistryUIService chemistryUIService)
        {
            _uow = uow as UnitOfWorks;
            _chemistryUIService = chemistryUIService;
        }

        // The "key data from session" bit: read the logged-in user id from
        // Session and hand it to the repository to stamp onto the row.
        private int? CurrentUserId() => HttpContext.Session.GetInt32("UserId");

        // ── LIST ─────────────────────────────────────────────────
        [HttpGet]
        public IActionResult Index()
        {
            var vm = new ChemistryIndexViewModel
            {
                Pollutants = _uow.pollutantRepository.GetAll(activeOnly: true),
                Reactions = _uow.chemicalReactionRepository.GetAll(activeOnly: true),
                Liquids = _uow.scrubbingLiquidRepository.GetLookup()
            };
            return View(vm);
        }

        // ── CREATE ───────────────────────────────────────────────
        [HttpGet]
        public IActionResult Create()
        {
            var vm = new ReactionFormViewModel();
            PopulateDropdowns(vm);
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ReactionFormViewModel model)
        {
            if (!ModelState.IsValid)
            {
                PopulateDropdowns(model);
                return View(model);
            }

            _uow.chemicalReactionRepository.Add(ToEntity(model), CurrentUserId());
            await _uow.Commit();

            TempData["Success"] = "Reaction added.";
            return RedirectToAction(nameof(Index));
        }

        // ── EDIT ─────────────────────────────────────────────────
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var r = _uow.chemicalReactionRepository.GetById(id);
            if (r == null)
            {
                TempData["Error"] = "Reaction not found.";
                return RedirectToAction(nameof(Index));
            }

            var vm = new ReactionFormViewModel
            {
                Id = r.Id,
                PollutantId = r.PollutantId,
                ScrubbingLiquidId = r.ScrubbingLiquidId,
                Equation = r.Equation,
                ReactionType = r.ReactionType,
                ProductName = r.ProductName,
                StoichiometricRatio = r.StoichiometricRatio,
                MaxRemovalEfficiency = r.MaxRemovalEfficiency,
                MinOperatingPH = r.MinOperatingPH,
                MaxOperatingPH = r.MaxOperatingPH,
                HeatOfReaction = r.HeatOfReaction,
                DesignNotes = r.DesignNotes,
                IsPrimary = r.IsPrimary,
                IsActive = r.IsActive
            };
            PopulateDropdowns(vm);
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(ReactionFormViewModel model)
        {
            if (!ModelState.IsValid)
            {
                PopulateDropdowns(model);
                return View(model);
            }

            var entity = ToEntity(model);
            entity.Id = model.Id;

            if (!_uow.chemicalReactionRepository.Update(entity))
            {
                TempData["Error"] = "Reaction not found.";
                return RedirectToAction(nameof(Index));
            }
            await _uow.Commit();

            TempData["Success"] = "Reaction updated.";
            return RedirectToAction(nameof(Index));
        }

        // ── DELETE (soft) ────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            _uow.chemicalReactionRepository.Delete(id);
            await _uow.Commit();

            TempData["Success"] = "Reaction removed.";
            return RedirectToAction(nameof(Index));
        }

        // ── CALCULATION ──────────────────────────────────────────
        [HttpGet]
        public IActionResult Calculation()
        {
            var vm = _chemistryUIService.BuildForm();
            return View("ChemistryCalculation", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Calculation(ChemistryCalculationFormViewModel model)
        {
            if (!ModelState.IsValid)
            {
                _chemistryUIService.PopulateDropdowns(model);
                return View("ChemistryCalculation", model);
            }

            try
            {
                var report = _chemistryUIService.RunCalculation(model);
                return View("ChemistryReport", report);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError("", ex.Message);
                _chemistryUIService.PopulateDropdowns(model);
                return View("ChemistryCalculation", model);
            }
        }

        // ── AJAX lookup for the design page ──────────────────────
        [HttpGet]
        public IActionResult GetReaction(int pollutantId, int liquidId)
        {
            var r = _uow.chemicalReactionRepository.GetPrimaryForPair(pollutantId, liquidId);
            if (r == null) return Json(new { found = false });

            return Json(new
            {
                found = true,
                equation = r.Equation,
                reactionType = r.ReactionType,
                product = r.ProductName,
                ratio = r.StoichiometricRatio,
                maxRemoval = r.MaxRemovalEfficiency,
                phMin = r.MinOperatingPH,
                phMax = r.MaxOperatingPH,
                notes = r.DesignNotes
            });
        }

        // ── Helpers ──────────────────────────────────────────────
        private void PopulateDropdowns(ReactionFormViewModel vm)
        {
            vm.Pollutants = _uow.pollutantRepository.GetAll(activeOnly: true);
            vm.Liquids = _uow.scrubbingLiquidRepository.GetAll(activeOnly: true);
        }

        private static ChemicalReaction ToEntity(ReactionFormViewModel m) => new ChemicalReaction
        {
            PollutantId = m.PollutantId,
            ScrubbingLiquidId = m.ScrubbingLiquidId,
            Equation = m.Equation.Trim(),
            ReactionType = m.ReactionType?.Trim() ?? "",
            ProductName = m.ProductName?.Trim() ?? "",
            StoichiometricRatio = m.StoichiometricRatio,
            MaxRemovalEfficiency = m.MaxRemovalEfficiency,
            MinOperatingPH = m.MinOperatingPH,
            MaxOperatingPH = m.MaxOperatingPH,
            HeatOfReaction = m.HeatOfReaction,
            DesignNotes = m.DesignNotes?.Trim(),
            IsPrimary = m.IsPrimary,
            IsActive = m.IsActive
        };
    }
}