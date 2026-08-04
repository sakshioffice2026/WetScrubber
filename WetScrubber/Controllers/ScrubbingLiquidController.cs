using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WetScrubber.Database;
using WetScrubber.Models;
using WetScrubber.Repositories.Contracts;
using WetScrubber.Repositories.Repositories;

namespace WetScrubber.Controllers
{
    // Manage the ScrubbingLiquid master (reagents that feed every dropdown).
    public class ScrubbingLiquidController : Controller
    {
        private readonly UnitOfWorks _uow;
        public ScrubbingLiquidController(IUnitOfWork uow) { _uow = uow as UnitOfWorks; }

        private int? CurrentUserId() => HttpContext.Session.GetInt32("UserId");

        [HttpGet]
        public IActionResult Index()
            => View(_uow.scrubbingLiquidRepository.GetAll());

        [HttpGet]
        public IActionResult Create() => View(new LiquidFormViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(LiquidFormViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            _uow.scrubbingLiquidRepository.Add(new ScrubbingLiquid
            {
                Code = model.Code.Trim(),
                DisplayName = model.DisplayName.Trim(),
                Formula = model.Formula?.Trim() ?? "",
                ReagentMolecularWeight = model.ReagentMolecularWeight,
                DefaultDensity = model.DefaultDensity,
                DefaultPH = model.DefaultPH,
                Description = model.Description?.Trim(),
                IsActive = model.IsActive
            }, CurrentUserId());
            await _uow.Commit();

            TempData["Success"] = "Scrubbing liquid added.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult Edit(int id)
        {
            var l = _uow.scrubbingLiquidRepository.GetById(id);
            if (l == null) { TempData["Error"] = "Liquid not found."; return RedirectToAction(nameof(Index)); }

            return View(new LiquidFormViewModel
            {
                Id = l.Id,
                Code = l.Code,
                DisplayName = l.DisplayName,
                Formula = l.Formula,
                ReagentMolecularWeight = l.ReagentMolecularWeight,
                DefaultDensity = l.DefaultDensity,
                DefaultPH = l.DefaultPH,
                Description = l.Description,
                IsActive = l.IsActive
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(LiquidFormViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            _uow.scrubbingLiquidRepository.Update(new ScrubbingLiquid
            {
                Id = model.Id,
                Code = model.Code.Trim(),
                DisplayName = model.DisplayName.Trim(),
                Formula = model.Formula?.Trim() ?? "",
                ReagentMolecularWeight = model.ReagentMolecularWeight,
                DefaultDensity = model.DefaultDensity,
                DefaultPH = model.DefaultPH,
                Description = model.Description?.Trim(),
                IsActive = model.IsActive
            });
            await _uow.Commit();

            TempData["Success"] = "Scrubbing liquid updated.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            _uow.scrubbingLiquidRepository.Delete(id);   // soft delete
            await _uow.Commit();
            TempData["Success"] = "Scrubbing liquid deactivated.";
            return RedirectToAction(nameof(Index));
        }
    }
}
