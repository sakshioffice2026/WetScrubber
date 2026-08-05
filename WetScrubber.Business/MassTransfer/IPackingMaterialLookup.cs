using System.Collections.Generic;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Resolved packing data for GPDC sizing (ScrubberCalculationEngine)
    /// and rate-based mass transfer (OndaMassTransferCorrelation). Mirrors
    /// HenrysLawSpeciesData/DiffusionSpeciesData's shape — a flat DTO the
    /// Business layer consumes without knowing about EF.
    /// </summary>
    public sealed class PackingMaterialData
    {
        public string Code { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string PackingType { get; set; } = "";
        public double? NominalSizeMm { get; set; }

        public double PackingFactorPerM { get; set; }
        public double SpecificSurfaceAreaM2M3 { get; set; }
        public double VoidFraction { get; set; }
        public double? NominalHetpM { get; set; }
    }

    /// <summary>
    /// Same shape/reasoning as IHenrysLawLookup/IDiffusionPropertyLookup —
    /// resolve by code, plus GetAll() for a design-page packing dropdown
    /// and GetDefault() so existing calls that don't select a packing yet
    /// keep working against the current Pall Ring 50mm behavior.
    /// </summary>
    public interface IPackingMaterialLookup
    {
        PackingMaterialData? GetByCode(string code);

        IReadOnlyList<PackingMaterialData> GetAll();

        /// <summary>
        /// Falls back to the historical hardcoded Pall Ring 50mm row
        /// (Fp=66/m, ap=112 m²/m³) when no packing has been selected or
        /// the selected code isn't found — same "unsourced data never
        /// breaks a design" contract HenrysLawData/NrtlBinaryParameter use.
        /// </summary>
        PackingMaterialData GetDefault();
    }
}