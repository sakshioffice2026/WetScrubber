using System.ComponentModel;
using System.Text.Json;
using EngineeringAI.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using WetScrubber.Database;

namespace WetScrubber.Plugins
{
    public class ScrubberDatabasePlugin : IDatabasePlugin
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly ApplicationDbContext _db;

        public ScrubberDatabasePlugin(ApplicationDbContext db)
        {
            _db = db;
        }

        public string PluginName => "ScrubberDatabase";

        // ── Typed read-only lookups (used by ScrubberDesignPlugin) ──────────

        public async Task<Pollutant?> FindPollutantAsync(string? nameOrCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(nameOrCode))
            {
                return null;
            }

            var key = nameOrCode.Trim().ToLower();

            return await _db.Pollutants
                .AsNoTracking()
                .Where(p => p.IsActive &&
                            (p.Code.ToLower() == key ||
                             p.DisplayName.ToLower() == key ||
                             p.Formula.ToLower() == key))
                .FirstOrDefaultAsync(ct);
        }

        public async Task<ScrubbingLiquid?> FindLiquidAsync(string? nameOrCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(nameOrCode))
            {
                return null;
            }

            var key = nameOrCode.Trim().ToLower();

            return await _db.ScrubbingLiquids
                .AsNoTracking()
                .Where(l => l.IsActive &&
                            (l.Code.ToLower() == key ||
                             l.DisplayName.ToLower() == key ||
                             l.Formula.ToLower() == key))
                .FirstOrDefaultAsync(ct);
        }

        public async Task<Packing?> FindPackingAsync(string? code, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            var key = code.Trim().ToLower();

            return await _db.Packings
                .AsNoTracking()
                .Where(p => p.IsActive && p.Code.ToLower() == key)
                .FirstOrDefaultAsync(ct);
        }

        // ── Kernel functions (read-only) ────────────────────────────────────

        [KernelFunction("get_pollutant_info")]
        [Description("Looks up a pollutant in the master catalog by name, code, or formula. Returns molecular weight and Henry's law constant.")]
        public async Task<string> GetPollutantInfoAsync(
            [Description("Pollutant name, code, or formula, for example SO2")] string pollutant,
            CancellationToken ct = default)
        {
            var p = await FindPollutantAsync(pollutant, ct);

            if (p is null)
            {
                return JsonSerializer.Serialize(new { found = false, query = pollutant }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                found = true,
                p.Id,
                p.Code,
                p.DisplayName,
                p.Formula,
                molecularWeight = p.DefaultMolecularWeight,
                henrysLawConstant = p.DefaultHenrysLawConstant
            }, JsonOptions);
        }

        [KernelFunction("get_scrubbing_liquid_info")]
        [Description("Looks up a scrubbing liquid or reagent in the master catalog by name, code, or formula.")]
        public async Task<string> GetScrubbingLiquidInfoAsync(
            [Description("Liquid name, code, or formula, for example NaOH")] string liquid,
            CancellationToken ct = default)
        {
            var l = await FindLiquidAsync(liquid, ct);

            if (l is null)
            {
                return JsonSerializer.Serialize(new { found = false, query = liquid }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                found = true,
                l.Id,
                l.Code,
                l.DisplayName,
                l.Formula,
                density = l.DefaultDensity,
                ph = l.DefaultPH
            }, JsonOptions);
        }

        [KernelFunction("get_packing_info")]
        [Description("Looks up a packing type by code and returns its geometry and material.")]
        public async Task<string> GetPackingInfoAsync(
            [Description("Packing code, for example PallRing50")] string packingCode,
            CancellationToken ct = default)
        {
            var p = await FindPackingAsync(packingCode, ct);

            if (p is null)
            {
                return JsonSerializer.Serialize(new { found = false, query = packingCode }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                found = true,
                p.Code,
                p.DisplayName,
                p.ManufacturerType,
                p.Material,
                nominalSizeM = p.NominalSizeM,
                specificAreaM2M3 = p.SpecificAreaM2M3,
                voidageFraction = p.VoidageFraction
            }, JsonOptions);
        }

        [KernelFunction("get_similar_designs")]
        [Description("Returns recent saved designs for the same pollutant as historical benchmarks.")]
        public async Task<string> GetSimilarDesignsAsync(
            [Description("Pollutant name, code, or formula")] string pollutant,
            [Description("Maximum number of designs to return")] int limit = 5,
            CancellationToken ct = default)
        {
            var p = await FindPollutantAsync(pollutant, ct);

            if (p is null)
            {
                return JsonSerializer.Serialize(new { found = false, query = pollutant }, JsonOptions);
            }

            var take = Math.Clamp(limit, 1, 20);

            var rows = await _db.ScrubberDesigns
                .AsNoTracking()
                .Where(d => d.Geometry != null &&
                            d.GasStream != null &&
                            d.GasStream.Pollutants.Any(x => x.PollutantType == p.Id))
                .OrderByDescending(d => d.CreatedAt)
                .Take(take)
                .Select(d => new
                {
                    d.DesignName,
                    d.PackingCode,
                    shellMaterial = d.ShellMaterial.ToString(),
                    internalMaterial = d.InternalMaterial.ToString(),
                    normalFlowRate = d.GasStream!.NormalFlowRate,
                    inletTemperature = d.GasStream.InletTemperature,
                    towerDiameter = d.Geometry!.TowerDiameter,
                    packingHeight = d.Geometry.PackingHeight,
                    pressureDrop = d.Geometry.PressureDrop,
                    removalEfficiency = d.Geometry.RemovalEfficiency
                })
                .ToListAsync(ct);

            return JsonSerializer.Serialize(new { found = rows.Count > 0, pollutant = p.Code, designs = rows }, JsonOptions);
        }
    }
}