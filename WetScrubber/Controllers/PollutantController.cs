using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WetScrubber.Database;
using WetScrubber.Models;
using WetScrubber.Repositories.Contracts;
using WetScrubber.Repositories.Repositories;

namespace WetScrubber.Controllers
{
    // Manage the Pollutant master (the list that feeds every dropdown).
    public class PollutantController : Controller
    {
        private readonly UnitOfWorks _uow;
        public PollutantController(IUnitOfWork uow) { _uow = uow as UnitOfWorks; }

        private int? CurrentUserId() => HttpContext.Session.GetInt32("UserId");

        [HttpGet]
        public IActionResult Index()
            => View(_uow.pollutantRepository.GetAll());   // include inactive so they can re-activate

        [HttpGet]
        public IActionResult Create() => View(new PollutantFormViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PollutantFormViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            _uow.pollutantRepository.Add(new Pollutant
            {
                Code = model.Code.Trim(),
                DisplayName = model.DisplayName.Trim(),
                Formula = model.Formula?.Trim() ?? "",
                DefaultMolecularWeight = model.DefaultMolecularWeight,
                DefaultHenrysLawConstant = model.DefaultHenrysLawConstant,
                Description = model.Description?.Trim(),
                IsActive = model.IsActive
            }, CurrentUserId());
            await _uow.Commit();

            TempData["Success"] = "Pollutant added.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult Edit(int id)
        {
            var p = _uow.pollutantRepository.GetById(id);
            if (p == null) { TempData["Error"] = "Pollutant not found."; return RedirectToAction(nameof(Index)); }

            return View(new PollutantFormViewModel
            {
                Id = p.Id,
                Code = p.Code,
                DisplayName = p.DisplayName,
                Formula = p.Formula,
                DefaultMolecularWeight = p.DefaultMolecularWeight,
                DefaultHenrysLawConstant = p.DefaultHenrysLawConstant,
                Description = p.Description,
                IsActive = p.IsActive
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(PollutantFormViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            _uow.pollutantRepository.Update(new Pollutant
            {
                Id = model.Id,
                Code = model.Code.Trim(),
                DisplayName = model.DisplayName.Trim(),
                Formula = model.Formula?.Trim() ?? "",
                DefaultMolecularWeight = model.DefaultMolecularWeight,
                DefaultHenrysLawConstant = model.DefaultHenrysLawConstant,
                Description = model.Description?.Trim(),
                IsActive = model.IsActive
            });
            await _uow.Commit();

            TempData["Success"] = "Pollutant updated.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            _uow.pollutantRepository.Delete(id);   // soft delete (IsActive = false)
            await _uow.Commit();
            TempData["Success"] = "Pollutant deactivated.";
            return RedirectToAction(nameof(Index));
        }
    }
}