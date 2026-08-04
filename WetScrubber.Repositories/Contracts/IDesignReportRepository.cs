using System.Collections.Generic;
using System.Threading.Tasks;
using WetScrubber.Database;

namespace WetScrubber.Repositories.Interfaces
{
    public interface IDesignReportRepository
    {
        Task<DesignReport?> GetByIdAsync(int reportId);

        Task<DesignReport?> GetByDesignIdAsync(int designId);

        Task<List<DesignReport>> GetByProjectIdAsync(int projectId);

        Task AddAsync(DesignReport report);

        Task UpdateAsync(DesignReport report);

        Task DeleteAsync(int reportId);

        Task<bool> ExistsForDesignAsync(int designId);

        Task SaveChangesAsync();
    }
}