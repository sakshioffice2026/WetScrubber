using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WetScrubber.Database;
using WetScrubber.Database.Enums;
using WetScrubber.Models;

namespace WetScrubber.Controllers
{
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        public DashboardController(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ── GET /Dashboard/Index ──────────────────────────────────
        public async Task<IActionResult> Index()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var user = await _dbContext.Users
                                       .Include(u => u.Role)
                                       .FirstOrDefaultAsync(u => u.UserId == userId);

            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            var now = DateTime.UtcNow;
            var designsQ = _dbContext.ScrubberDesigns
                                     .Where(d => d.Project.CreatedByUserId == userId);

            // ── KPIs ──────────────────────────────────────────────
            var totalProjects = await _dbContext.Projects
                .CountAsync(p => p.CreatedByUserId == userId);

            var activeProjects = await _dbContext.Projects
                .CountAsync(p => p.CreatedByUserId == userId &&
                                 p.Status == ProjectStatus.InProgress);

            var totalDesigns = await designsQ.CountAsync();

            var flowsheetCount = await _dbContext.Flowsheets
                .CountAsync(f => f.Project.CreatedByUserId == userId);

            // ── Review pipeline ───────────────────────────────────
            var statusCounts = await designsQ
                .GroupBy(d => d.ReviewStatus)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            int StatusCount(DesignReviewStatus s) =>
                statusCounts.Where(x => x.Status == s).Select(x => x.Count).FirstOrDefault();

            // ── Action queue (needs attention) ────────────────────
            var staleCutoff = now.AddDays(-14);

            var attentionRaw = await designsQ
                .Where(d => d.ReviewStatus == DesignReviewStatus.ChangesRequested
                         || d.ReviewStatus == DesignReviewStatus.UnderReview
                         || (d.ReviewStatus == DesignReviewStatus.Draft && d.UpdatedAt < staleCutoff))
                .Select(d => new
                {
                    d.DesignId,
                    d.DesignName,
                    d.Project.ProjectNumber,
                    d.Project.ProjectName,
                    d.ReviewStatus,
                    d.UpdatedAt
                })
                .OrderBy(x => x.UpdatedAt)
                .Take(60)
                .ToListAsync();

            var attention = attentionRaw
                .Select(x => new
                {
                    Item = new AttentionItem
                    {
                        DesignId = x.DesignId,
                        DesignName = x.DesignName,
                        ProjectNumber = x.ProjectNumber,
                        ProjectName = x.ProjectName,
                        Kind = x.ReviewStatus == DesignReviewStatus.ChangesRequested ? "changes"
                                      : x.ReviewStatus == DesignReviewStatus.UnderReview ? "review"
                                      : "stale",
                        AgeDays = Math.Max(0, (int)(now - x.UpdatedAt).TotalDays)
                    },
                    Priority = x.ReviewStatus == DesignReviewStatus.ChangesRequested ? 0
                             : x.ReviewStatus == DesignReviewStatus.UnderReview ? 1
                             : 2,
                    x.UpdatedAt
                })
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.UpdatedAt)
                .Take(6)
                .Select(x => x.Item)
                .ToList();

            // ── Activity heatmap (last 26 weeks) ──────────────────
            var heatStart = now.Date.AddDays(-(26 * 7));

            var stamps = await designsQ
                .Where(d => d.CreatedAt >= heatStart || d.UpdatedAt >= heatStart)
                .Select(d => new { d.CreatedAt, d.UpdatedAt })
                .ToListAsync();

            var activity = new Dictionary<string, int>();
            void Bump(DateTime dt)
            {
                if (dt < heatStart) return;
                var key = dt.ToString("yyyy-MM-dd");
                activity.TryGetValue(key, out var current);
                activity[key] = current + 1;
            }

            foreach (var s in stamps)
            {
                Bump(s.CreatedAt);
                if (s.UpdatedAt.Date != s.CreatedAt.Date) Bump(s.UpdatedAt);
            }

            var thisWeek = stamps.Count(s => s.CreatedAt >= now.AddDays(-7));
            var lastWeek = stamps.Count(s => s.CreatedAt >= now.AddDays(-14) &&
                                             s.CreatedAt < now.AddDays(-7));

            // ── Operating envelope (gas flow vs target removal) ───
            var envelopeRaw = await designsQ
                .Where(d => d.GasStream != null)
                .Select(d => new
                {
                    d.DesignId,
                    d.DesignName,
                    ProjectName = d.Project.ProjectName,
                    d.ScrubberType,
                    d.ReviewStatus,
                    Flow = d.GasStream!.ActualFlowRate,
                    Removal = d.GasStream!.Pollutants.Max(p => (double?)p.TargetRemovalEfficiency) ?? 0.0
                })
                .OrderByDescending(x => x.DesignId)
                .Take(300)
                .ToListAsync();

            // ── Pollutant mix ─────────────────────────────────────
            var mixRaw = await _dbContext.PollutantStreams
                .Where(ps => ps.GasStream.Design.Project.CreatedByUserId == userId)
                .GroupBy(ps => ps.PollutantType)
                .Select(g => new { Type = g.Key, Count = g.Count() })
                .ToListAsync();

            var pollutantNames = await _dbContext.Pollutants
                .Select(p => new { p.Id, p.Code, p.DisplayName })
                .ToListAsync();

            var pollutantMix = mixRaw
                .OrderByDescending(x => x.Count)
                .Take(8)
                .Select(x =>
                {
                    var match = pollutantNames.FirstOrDefault(p => p.Id == x.Type);
                    return new PollutantShare
                    {
                        Code = match?.Code ?? ("#" + x.Type),
                        Name = match?.DisplayName ?? ("Pollutant " + x.Type),
                        Count = x.Count
                    };
                })
                .ToList();

            // ── Scrubber type breakdown (kept for legend colours) ─
            var scrubberTypes = await designsQ
                .GroupBy(d => d.ScrubberType)
                .Select(g => new ScrubberTypeStat
                {
                    TypeName = g.Key.ToString(),
                    Count = g.Count()
                })
                .ToListAsync();

            // ── Recent projects ───────────────────────────────────
            var recentProjects = await _dbContext.Projects
                .Where(p => p.CreatedByUserId == userId)
                .Include(p => p.Designs)
                .OrderByDescending(p => p.UpdatedAt)
                .Take(5)
                .ToListAsync();

            // ── Client payload ────────────────────────────────────
            var data = new
            {
                today = now.ToString("yyyy-MM-dd"),
                activity,
                envelope = envelopeRaw.Select(e => new
                {
                    id = e.DesignId,
                    name = e.DesignName,
                    project = e.ProjectName,
                    type = e.ScrubberType.ToString(),
                    status = e.ReviewStatus.ToString(),
                    flow = e.Flow,
                    removal = e.Removal
                }),
                mix = pollutantMix.Select(m => new { code = m.Code, name = m.Name, count = m.Count })
            };

            var vm = new DashboardViewModel
            {
                UserFullName = user.FullName,
                UserInitials = GetInitials(user.FullName),
                UserRole = user.Role?.RoleName ?? "Engineer",
                UserJobTitle = user.JobTitle,
                UserCompany = user.Company,
                LastLoginAt = user.LastLoginAt,

                TotalProjects = totalProjects,
                ActiveProjects = activeProjects,
                TotalDesigns = totalDesigns,
                FlowsheetCount = flowsheetCount,
                DesignsThisWeek = thisWeek,
                DesignsLastWeek = lastWeek,

                DraftCount = StatusCount(DesignReviewStatus.Draft),
                UnderReviewCount = StatusCount(DesignReviewStatus.UnderReview),
                ChangesRequestedCount = StatusCount(DesignReviewStatus.ChangesRequested),
                ApprovedCount = StatusCount(DesignReviewStatus.Approved),

                Attention = attention,
                PollutantMix = pollutantMix,
                DataJson = JsonSerializer.Serialize(data),

                ScrubberTypes = scrubberTypes,
                RecentProjects = recentProjects
            };

            return View(vm);
        }

        // ── Helper: get initials from full name ───────────────────
        private static string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "??";

            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0][0].ToString().ToUpper();

            return $"{parts[0][0]}{parts[^1][0]}".ToUpper();
        }
    }
}