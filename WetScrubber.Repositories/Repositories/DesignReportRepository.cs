using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WetScrubber.Database;
using WetScrubber.Repositories.Interfaces;

namespace WetScrubber.Repositories
{
    public class DesignReportRepository : IDesignReportRepository
    {
        private readonly ApplicationDbContext _context;

        public DesignReportRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<DesignReport?> GetByIdAsync(int reportId)
        {
            return await _context.DesignReports
                .Include(x => x.Design)
                .FirstOrDefaultAsync(x => x.ReportId == reportId);
        }

        public async Task<DesignReport?> GetByDesignIdAsync(int designId)
        {
            return await _context.DesignReports
                .Include(x => x.Design)
                .FirstOrDefaultAsync(x => x.DesignId == designId);
        }

        public async Task<List<DesignReport>> GetByProjectIdAsync(int projectId)
        {
            return await _context.DesignReports
                .Include(x => x.Design)
                .Where(x => x.Design.ProjectId == projectId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
        }

        public async Task AddAsync(DesignReport report)
        {
            await _context.DesignReports.AddAsync(report);
        }

        public async Task UpdateAsync(DesignReport report)
        {
            _context.DesignReports.Update(report);
            await Task.CompletedTask;
        }

        public async Task DeleteAsync(int reportId)
        {
            var report = await GetByIdAsync(reportId);

            if (report != null)
            {
                _context.DesignReports.Remove(report);
            }
        }

        public async Task<bool> ExistsForDesignAsync(int designId)
        {
            return await _context.DesignReports
                .AnyAsync(x => x.DesignId == designId);
        }

        public async Task SaveChangesAsync()
        {
            await _context.SaveChangesAsync();
        }
    }
}