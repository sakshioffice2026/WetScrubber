using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WetScrubber.Business.AI;
using WetScrubber.Business.Reports;
using WetScrubber.Database;
using WetScrubber.Database.Enums;
using WetScrubber.Models;
using WetScrubber.Repositories.Interfaces;

namespace WetScrubber.Controllers
{
    /// <summary>
    /// Report generation and review.
    ///
    /// Flow: Generate (deterministic template) -> optionally DraftWithAi
    /// (rewording only) -> Review (human reads both) -> Approve (human
    /// picks/edits the final text). Nothing here ever computes or edits an
    /// engineering number — those come only from ScrubberCalculationEngine
    /// via ScrubberDesign.Geometry.
    /// </summary>
    public class ReportController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITemplateNarrativeBuilder _templateBuilder;
        private readonly IAiNarrativeService _aiNarrativeService;
        private readonly IDesignReportRepository _reportRepository;

        public ReportController(
            ApplicationDbContext context,
            ITemplateNarrativeBuilder templateBuilder,
            IAiNarrativeService aiNarrativeService,
            IDesignReportRepository reportRepository)
        {
            _context = context;
            _templateBuilder = templateBuilder;
            _aiNarrativeService = aiNarrativeService;
            _reportRepository = reportRepository;
        }

        private int? GetUserId() => HttpContext.Session.GetInt32("UserId");

        // GET /Report/Generate/5   (5 = designId)
        // Builds the deterministic template narrative and creates the
        // DesignReport row if one doesn't exist yet. Never calls AI.
        [HttpGet]
        public async Task<IActionResult> Generate(int designId)
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var design = await _context.ScrubberDesigns
                .Include(d => d.Project)
                .Include(d => d.GasStream).ThenInclude(g => g!.Pollutants)
                .Include(d => d.LiquidSpec).ThenInclude(l => l!.ScrubbingLiquid)
                .Include(d => d.Geometry)
                .FirstOrDefaultAsync(d => d.DesignId == designId
                                       && d.Project.CreatedByUserId == userId);

            if (design == null) return NotFound();

            // Always rebuild the deterministic text from the CURRENT design
            // numbers. Previously this was only done when no report existed
            // yet, so recalculating a design never refreshed its report —
            // the same stale text (and stale AI narrative) kept showing up
            // no matter how many times the numbers changed.
            var templateText = _templateBuilder.Build(design);

            var existing = await _reportRepository.GetByDesignIdAsync(designId);

            if (existing == null)
            {
                var report = new DesignReport
                {
                    DesignId = designId,
                    TemplateNarrative = templateText,
                    NarrativeSource = NarrativeSource.TemplateOnly,
                    Status = ReportStatus.Draft
                };

                await _reportRepository.AddAsync(report);
                await _reportRepository.SaveChangesAsync();
            }
            else if (existing.Status != ReportStatus.Approved
                     && existing.TemplateNarrative != templateText)
            {
                // Numbers changed since this report was last built. Refresh
                // the deterministic text, and drop any previous AI draft —
                // it was worded around the OLD numbers, so keeping it would
                // show an AI narrative that no longer matches the figures
                // right above it. Approved reports are left untouched; once
                // signed off, they're frozen on purpose.
                existing.TemplateNarrative = templateText;
                existing.AiNarrative = null;
                existing.NarrativeSource = NarrativeSource.TemplateOnly;
                existing.UpdatedAt = DateTime.UtcNow;

                await _reportRepository.UpdateAsync(existing);
                await _reportRepository.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Review), new { designId });
        }

        // GET /Report/Review/5   (5 = designId)
        [HttpGet]
        public async Task<IActionResult> Review(int designId)
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var report = await _reportRepository.GetByDesignIdAsync(designId);
            if (report == null) return RedirectToAction(nameof(Generate), new { designId });

            var vm = new ReportReviewViewModel
            {
                ReportId = report.ReportId,
                DesignId = report.DesignId,
                DesignName = report.Design?.DesignName ?? "",
                Status = report.Status,
                NarrativeSource = report.NarrativeSource,
                TemplateNarrative = report.TemplateNarrative,
                AiNarrative = report.AiNarrative,
                ApprovedNarrative = report.ApprovedNarrative,
                ReviewerComments = report.ReviewerComments,
                AiDraftFailed = TempData["AiDraftFailed"] as bool? ?? false,
                AiDraftError = TempData["AiDraftError"] as string
            };

            return View(vm);
        }

        // POST /Report/DraftWithAi/5   (5 = reportId)
        // Asks the LLM (Groq) to draft the DIAGNOSIS / PRESCRIPTION /
        // SUMMARY CONCLUSION narrative from the deterministic template. If
        // Groq is unreachable, misconfigured, or errors, the template-only
        // report is left exactly as it was — this never blocks the
        // engineer from proceeding without AI.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DraftWithAi(int reportId)
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var report = await _reportRepository.GetByIdAsync(reportId);
            if (report == null) return NotFound();

            try
            {
                var aiText = await _aiNarrativeService.DraftNarrativeAsync(report.TemplateNarrative);

                report.AiNarrative = aiText;
                report.NarrativeSource = NarrativeSource.AiDrafted;
                report.Status = ReportStatus.NarrativeDrafted;
                report.UpdatedAt = DateTime.UtcNow;

                await _reportRepository.UpdateAsync(report);
                await _reportRepository.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Deliberately swallow and redirect with a flash message
                // rather than a 500 page — the template report still works.
                TempData["AiDraftFailed"] = true;
                TempData["AiDraftError"] = ex.Message;
            }

            return RedirectToAction(nameof(Review), new { designId = report.DesignId });
        }

        // POST /Report/Approve
        // The engineer's explicit sign-off. approvedText is whatever they
        // ended up with in the textarea (template, AI draft, or their own
        // hand edits) — this method never chooses one for them.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int reportId, string approvedText, string? reviewerComments)
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(approvedText))
            {
                ModelState.AddModelError("", "Approved text cannot be empty.");
                var report0 = await _reportRepository.GetByIdAsync(reportId);
                return RedirectToAction(nameof(Review), new { designId = report0?.DesignId });
            }

            var report = await _reportRepository.GetByIdAsync(reportId);
            if (report == null) return NotFound();

            report.ApprovedNarrative = approvedText;
            report.ReviewerComments = reviewerComments;
            report.Status = ReportStatus.Approved;
            report.ReviewedByUserId = userId;
            report.ReviewedAt = DateTime.UtcNow;
            report.UpdatedAt = DateTime.UtcNow;

            await _reportRepository.UpdateAsync(report);
            await _reportRepository.SaveChangesAsync();

            return RedirectToAction(nameof(Review), new { designId = report.DesignId });
        }
    }
}