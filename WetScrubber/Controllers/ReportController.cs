using System;
using System.Threading.Tasks;
using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WetScrubber.Business.AI;
using WetScrubber.Business.Reports;
using WetScrubber.Database;
using WetScrubber.Database.Enums;
using WetScrubber.Models;
using WetScrubber.Repositories.Interfaces;
using WetScrubber.Services;

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

        // GET /Report/Index
        // Lists every calculated design of the signed-in user with export links.
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var items = await _context.ScrubberDesigns
                .Where(d => d.Project.CreatedByUserId == userId)
                .OrderBy(d => d.Project.ProjectNumber)
                .ThenBy(d => d.DesignId)
                .Select(d => new ReportListItemViewModel
                {
                    DesignId = d.DesignId,
                    ProjectNumber = d.Project.ProjectNumber,
                    ProjectName = d.Project.ProjectName,
                    DesignName = d.DesignName,
                    ScrubberType = d.ScrubberType.ToString(),
                    IsCalculated = d.Geometry != null,
                    RemovalEfficiency = d.Geometry != null ? d.Geometry.RemovalEfficiency : 0.0,
                    PressureDrop = d.Geometry != null ? d.Geometry.PressureDrop : 0.0,
                    UpdatedAt = d.UpdatedAt
                })
                .ToListAsync();

            return View(items);
        }

        // GET /Report/Export?designId=5&format=pdf|docx|xlsx
        // Exports the overall calculation report (inputs + calculated results).
        [HttpGet]
        public async Task<IActionResult> Export(int designId, string format = "pdf")
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var design = await _context.ScrubberDesigns
                .Include(d => d.Project)
                .Include(d => d.GasStream).ThenInclude(g => g!.Pollutants)
                .Include(d => d.LiquidSpec)
                .Include(d => d.Geometry)
                .FirstOrDefaultAsync(d => d.DesignId == designId
                                       && d.Project.CreatedByUserId == userId);

            if (design == null) return NotFound();

            var data = await BuildCalculationReportAsync(design);

            var baseName = (string.IsNullOrWhiteSpace(design.DesignName) ? "Design" : design.DesignName)
                .Replace(' ', '_') + "_Calculation_Report";

            switch ((format ?? "pdf").ToLowerInvariant())
            {
                case "docx":
                    return File(ReportExportService.ToDocx(data),
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        baseName + ".docx");
                case "xlsx":
                    return File(ReportExportService.ToXlsx(data),
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        baseName + ".xlsx");
                default:
                    return File(ReportExportService.ToPdf(data), "application/pdf", baseName + ".pdf");
            }
        }

        // GET /Report/ExportBatch?ids=1,2,3&format=pdf|docx|xlsx
        // Zips one calculation report per selected design.
        [HttpGet]
        public async Task<IActionResult> ExportBatch(string ids, string format = "pdf")
        {
            var userId = GetUserId();
            if (userId == null) return RedirectToAction("Login", "Account");

            var idList = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : 0)
                .Where(n => n > 0)
                .Distinct()
                .Take(100)
                .ToList();

            if (idList.Count == 0) return RedirectToAction(nameof(Index));

            var designs = await _context.ScrubberDesigns
                .Include(d => d.Project)
                .Include(d => d.GasStream).ThenInclude(g => g!.Pollutants)
                .Include(d => d.LiquidSpec)
                .Include(d => d.Geometry)
                .Where(d => idList.Contains(d.DesignId) && d.Project.CreatedByUserId == userId)
                .ToListAsync();

            if (designs.Count == 0) return NotFound();

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var design in designs)
                {
                    var data = await BuildCalculationReportAsync(design);
                    var (bytes, ext) = RenderReport(data, format);

                    var raw = $"{design.Project.ProjectNumber}_{design.DesignName}_{design.DesignId}";
                    foreach (var c in Path.GetInvalidFileNameChars())
                        raw = raw.Replace(c, '_');
                    raw = raw.Replace(' ', '_');

                    var entry = zip.CreateEntry(raw + "_Calculation_Report" + ext);
                    using var es = entry.Open();
                    es.Write(bytes, 0, bytes.Length);
                }
            }

            return File(ms.ToArray(), "application/zip", "Calculation_Reports.zip");
        }

        private static (byte[] Bytes, string Ext) RenderReport(ReportExportData data, string? format)
        {
            switch ((format ?? "pdf").ToLowerInvariant())
            {
                case "docx": return (ReportExportService.ToDocx(data), ".docx");
                case "xlsx": return (ReportExportService.ToXlsx(data), ".xlsx");
                default: return (ReportExportService.ToPdf(data), ".pdf");
            }
        }

        private static string F(double v, string unit = "", string fmt = "N2") =>
            string.IsNullOrEmpty(unit) ? v.ToString(fmt) : v.ToString(fmt) + " " + unit;

        private async Task<ReportExportData> BuildCalculationReportAsync(ScrubberDesign design)
        {
            var data = new ReportExportData
            {
                ProjectNumber = design.Project.ProjectNumber,
                ProjectName = design.Project.ProjectName,
                DesignName = design.DesignName,
                ScrubberType = design.ScrubberType.ToString(),
                Revision = design.RevisionNumber
            };

            // ── Design basis ──
            var basis = new ReportSection { Title = "Design Basis" };
            basis.Rows.Add(new ReportRow("Client", design.Project.ClientName));
            basis.Rows.Add(new ReportRow("Engineer", design.Project.EngineerName));
            basis.Rows.Add(new ReportRow("Scrubber type", design.ScrubberType.ToString()));
            basis.Rows.Add(new ReportRow("Shell material", design.ShellMaterial.ToString()));
            basis.Rows.Add(new ReportRow("Internal material", design.InternalMaterial.ToString()));
            if (!string.IsNullOrWhiteSpace(design.PackingCode))
                basis.Rows.Add(new ReportRow("Packing", design.PackingCode!));
            basis.Rows.Add(new ReportRow("Revision", design.RevisionNumber.ToString()));
            data.Sections.Add(basis);

            // ── Gas stream ──
            var gas = design.GasStream;
            if (gas != null)
            {
                var g = new ReportSection { Title = "Gas Stream" };
                g.Rows.Add(new ReportRow("Normal flow rate", F(gas.NormalFlowRate, "Nm³/hr", "N0")));
                g.Rows.Add(new ReportRow("Actual flow rate", F(gas.ActualFlowRate, "m³/hr", "N0")));
                g.Rows.Add(new ReportRow("Inlet temperature", F(gas.InletTemperature, "°C", "N1")));
                g.Rows.Add(new ReportRow("Inlet pressure", F(gas.InletPressure, "Pa", "N0")));
                g.Rows.Add(new ReportRow("Moisture content", F(gas.MoistureContent, "% vol", "N2")));
                g.Rows.Add(new ReportRow("Gas density", F(gas.GasDensity, "kg/m³", "N3")));
                g.Rows.Add(new ReportRow("Gas viscosity", gas.GasViscosity.ToString("0.0000E+0") + " Pa·s"));
                data.Sections.Add(g);

                var polNames = await _context.Pollutants
                    .ToDictionaryAsync(p => p.Id, p => p.DisplayName);

                int i = 1;
                foreach (var p in gas.Pollutants)
                {
                    polNames.TryGetValue(p.PollutantType, out var name);
                    var ps = new ReportSection { Title = $"Pollutant {i} - {name ?? ("Type " + p.PollutantType)}" };
                    ps.Rows.Add(new ReportRow("Inlet concentration", F(p.InletConcentration, "mg/Nm³", "N1")));
                    ps.Rows.Add(new ReportRow("Target outlet", F(p.TargetOutletConcentration, "mg/Nm³", "N1")));
                    ps.Rows.Add(new ReportRow("Target removal", F(p.TargetRemovalEfficiency, "%", "N1")));
                    ps.Rows.Add(new ReportRow("Molecular weight", F(p.MolecularWeight, "g/mol", "N1")));
                    ps.Rows.Add(new ReportRow("Henry's law constant", F(p.HenrysLawConstant, "", "N3")));
                    data.Sections.Add(ps);
                    i++;
                }
            }

            // ── Scrubbing liquid ──
            var liq = design.LiquidSpec;
            if (liq != null)
            {
                var liqName = await _context.ScrubbingLiquids
                    .Where(l => l.Id == liq.LiquidType)
                    .Select(l => l.DisplayName)
                    .FirstOrDefaultAsync();

                var l = new ReportSection { Title = "Scrubbing Liquid" };
                l.Rows.Add(new ReportRow("Liquid", liqName ?? ("Type " + liq.LiquidType)));
                l.Rows.Add(new ReportRow("pH", F(liq.pH, "", "N1")));
                l.Rows.Add(new ReportRow("Concentration", F(liq.Concentration, "% wt", "N2")));
                l.Rows.Add(new ReportRow("Temperature", F(liq.Temperature, "°C", "N1")));
                l.Rows.Add(new ReportRow("Density", F(liq.Density, "kg/m³", "N1")));
                l.Rows.Add(new ReportRow("Viscosity", F(liq.Viscosity, "mPa·s", "N3")));
                l.Rows.Add(new ReportRow("L/G ratio (design)", F(liq.LiquidToGasRatio, "L/m³", "N2")));
                data.Sections.Add(l);
            }

            // ── Calculated results ──
            var geo = design.Geometry;
            if (geo == null)
            {
                var pending = new ReportSection { Title = "Calculated Results" };
                pending.Rows.Add(new ReportRow("Status", "Not calculated, or the last calculation was infeasible"));
                data.Sections.Add(pending);
                return data;
            }

            var r = new ReportSection { Title = "Calculated Results" };
            r.Rows.Add(new ReportRow("Tower / column diameter", F(geo.TowerDiameter, "m", "N3")));
            r.Rows.Add(new ReportRow("Tower height", F(geo.TowerHeight, "m", "N2")));
            if (geo.PackingHeight > 0)
                r.Rows.Add(new ReportRow("Packing height", F(geo.PackingHeight, "m", "N2")));
            r.Rows.Add(new ReportRow("Gas velocity", F(geo.GasVelocity, "m/s", "N2")));
            r.Rows.Add(new ReportRow("Pressure drop", F(geo.PressureDrop, "Pa", "N0")));
            r.Rows.Add(new ReportRow("Removal efficiency", F(geo.RemovalEfficiency, "%", "N2")));
            r.Rows.Add(new ReportRow("Actual L/G ratio", F(geo.ActualLGRatio, "L/m³", "N2")));
            if (geo.MinLGRatio > 0)
                r.Rows.Add(new ReportRow("Minimum L/G ratio", F(geo.MinLGRatio, "L/m³", "N3")));
            if (geo.AbsorptionFactor > 0)
                r.Rows.Add(new ReportRow("Absorption factor", F(geo.AbsorptionFactor, "", "N3")));
            data.Sections.Add(r);

            // ── Impingement plate results ──
            if (geo.ImpingementPlateStages.HasValue)
            {
                var ip = new ReportSection { Title = "Impingement Plate Results" };
                ip.Rows.Add(new ReportRow("Plate stages", geo.ImpingementPlateStages.Value.ToString()));
                if (geo.ImpingementPlateActiveAreaM2.HasValue)
                    ip.Rows.Add(new ReportRow("Active area", F(geo.ImpingementPlateActiveAreaM2.Value, "m²", "N3")));
                if (geo.ImpingementPlateGasVelocityMPerS.HasValue)
                    ip.Rows.Add(new ReportRow("Hole gas velocity", F(geo.ImpingementPlateGasVelocityMPerS.Value, "m/s", "N2")));
                if (geo.ImpingementPlateFloodingVelocityMPerS.HasValue)
                    ip.Rows.Add(new ReportRow("Flooding velocity", F(geo.ImpingementPlateFloodingVelocityMPerS.Value, "m/s", "N3")));
                if (geo.ImpingementPlatePercentFlood.HasValue)
                    ip.Rows.Add(new ReportRow("Percent flood", F(geo.ImpingementPlatePercentFlood.Value, "%", "N1")));
                if (geo.ImpingementPlatePressureDropPa.HasValue)
                    ip.Rows.Add(new ReportRow("Plate-bank pressure drop", F(geo.ImpingementPlatePressureDropPa.Value, "Pa", "N0")));
                if (geo.ImpingementPlateRemovalEfficiencyPercent.HasValue)
                    ip.Rows.Add(new ReportRow("Gas absorption removal", F(geo.ImpingementPlateRemovalEfficiencyPercent.Value, "%", "N2")));
                data.Sections.Add(ip);
            }

            // ── Wet cyclone results ──
            if (geo.CycloneCutDiameterMicron.HasValue)
            {
                var cy = new ReportSection { Title = "Wet Cyclone Results" };
                cy.Rows.Add(new ReportRow("Cut diameter", F(geo.CycloneCutDiameterMicron.Value, "µm", "N2")));
                if (geo.CycloneParallelUnits.HasValue)
                    cy.Rows.Add(new ReportRow("Parallel units", geo.CycloneParallelUnits.Value.ToString()));
                if (geo.CycloneInletAreaM2.HasValue)
                    cy.Rows.Add(new ReportRow("Inlet area", F(geo.CycloneInletAreaM2.Value, "m²", "N4")));
                if (geo.CycloneGasOutletDiameterM.HasValue)
                    cy.Rows.Add(new ReportRow("Gas outlet diameter", F(geo.CycloneGasOutletDiameterM.Value, "m", "N3")));
                if (geo.CycloneEffectiveTurns.HasValue)
                    cy.Rows.Add(new ReportRow("Effective turns", F(geo.CycloneEffectiveTurns.Value, "", "N2")));
                if (geo.CycloneFanPowerKw.HasValue)
                    cy.Rows.Add(new ReportRow("Fan power", F(geo.CycloneFanPowerKw.Value, "kW", "N2")));
                if (geo.CyclonePumpPowerKw.HasValue)
                    cy.Rows.Add(new ReportRow("Pump power", F(geo.CyclonePumpPowerKw.Value, "kW", "N2")));
                if (geo.CycloneWaterRateLMin.HasValue)
                    cy.Rows.Add(new ReportRow("Water rate", F(geo.CycloneWaterRateLMin.Value, "L/min", "N1")));
                data.Sections.Add(cy);
            }

            return data;
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

            // Approving the report is also the PE sign-off on the design
            // itself: lock it so ScrubberController.Edit refuses further
            // mutation. From here on, changes — including anything the AI
            // narrative recommends — must go through CreateRevision, which
            // clones the inputs into a fresh, unlocked design.
            var design = await _context.ScrubberDesigns.FindAsync(report.DesignId);
            if (design != null && !design.IsLocked)
            {
                design.IsLocked = true;
                design.ReviewStatus = DesignReviewStatus.Approved;
                design.ReviewedByUserId = userId;
                design.ReviewedAt = DateTime.UtcNow;
                design.UpdatedAt = DateTime.UtcNow;
            }

            await _reportRepository.SaveChangesAsync();

            return RedirectToAction(nameof(Review), new { designId = report.DesignId });
        }
    }
}