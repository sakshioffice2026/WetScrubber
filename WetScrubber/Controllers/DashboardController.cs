using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WetScrubber.Database;
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
            // ── Session auth check ────────────────────────────────
            // If user is not logged in redirect to login page
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            // ── Load logged-in user from DB ───────────────────────
            var user = await _dbContext.Users
                                       .Include(u => u.Role)
                                       .FirstOrDefaultAsync(u => u.UserId == userId);

            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            // ── KPI: Total Projects for this user ─────────────────
            var totalProjects = await _dbContext.Projects
                                                .CountAsync(p => p.CreatedByUserId == userId);

            // ── KPI: Total Designs across user's projects ─────────
            var totalDesigns = await _dbContext.ScrubberDesigns
                                               .CountAsync(d => d.Project.CreatedByUserId == userId);

            // ── Chart: Designs per month (last 6 months) ──────────
            var sixMonthsAgo = DateTime.UtcNow.AddMonths(-5);

            var designsByMonth = await _dbContext.ScrubberDesigns
                .Where(d => d.Project.CreatedByUserId == userId
                         && d.CreatedAt >= sixMonthsAgo)
                .GroupBy(d => new { d.CreatedAt.Year, d.CreatedAt.Month })
                .Select(g => new
                {
                    Year  = g.Key.Year,
                    Month = g.Key.Month,
                    Count = g.Count()
                })
                .OrderBy(g => g.Year)
                .ThenBy(g => g.Month)
                .ToListAsync();

            // Build full 6-month series (fill missing months with 0)
            var monthLabels = new List<string>();
            var monthValues = new List<int>();

            for (int i = 5; i >= 0; i--)
            {
                var date  = DateTime.UtcNow.AddMonths(-i);
                var label = date.ToString("MMM");
                var count = designsByMonth
                    .FirstOrDefault(x => x.Year == date.Year && x.Month == date.Month)?.Count ?? 0;

                monthLabels.Add(label);
                monthValues.Add(count);
            }

            // ── Scrubber type breakdown ───────────────────────────
            var scrubberTypes = await _dbContext.ScrubberDesigns
                .Where(d => d.Project.CreatedByUserId == userId)
                .GroupBy(d => d.ScrubberType)
                .Select(g => new ScrubberTypeStat
                {
                    TypeName = g.Key.ToString(),
                    Count    = g.Count()
                })
                .ToListAsync();

            // ── Recent projects (last 5) ──────────────────────────
            var recentProjects = await _dbContext.Projects
                .Where(p => p.CreatedByUserId == userId)
                .Include(p => p.Designs)
                .OrderByDescending(p => p.UpdatedAt)
                .Take(5)
                .ToListAsync();

            // ── Build ViewModel ───────────────────────────────────
            var vm = new DashboardViewModel
            {
                UserFullName   = user.FullName,
                UserInitials   = GetInitials(user.FullName),
                UserRole       = user.Role?.RoleName ?? "Engineer",
                UserJobTitle   = user.JobTitle,
                UserCompany    = user.Company,
                LastLoginAt    = user.LastLoginAt,

                TotalProjects  = totalProjects,
                TotalDesigns   = totalDesigns,

                ChartMonths    = string.Join(",", monthLabels),
                ChartValues    = string.Join(",", monthValues),

                ScrubberTypes  = scrubberTypes,
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
