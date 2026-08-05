namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Resolved DiffusionProperty data needed for Wilke-Chang. Solute and
    /// solvent rows are looked up separately (see DiffusionProperty.cs —
    /// one row can hold either a solute's MolarVolumeAtBoilingPointCm3Mol
    /// or a solvent's AssociationFactor).
    /// </summary>
    public sealed class DiffusionSpeciesData
    {
        public string ComponentCode { get; set; } = "";
        public double? MolarVolumeAtBoilingPointCm3Mol { get; set; }
        public double? AssociationFactor { get; set; }

        // Fuller-Schettler-Giddings atomic diffusion volume, SUM(v_i)
        // [cm^3/mol], for gas-phase diffusivity (GasPhaseDiffusivity.cs).
        // NEW COLUMN — not on DiffusionProperty yet. Computed once per
        // species from Fuller's published per-atom increments (universal
        // constants, not per-substance experimental data, so safe to
        // derive rather than needing fresh sourcing like AssociationFactor).
        public double? FullerDiffusionVolumeCm3Mol { get; set; }
    }

    /// <summary>
    /// Same two-hop reasoning as IHenrysLawLookup / IComponentPropertyLookup:
    /// callers resolve via a code string here; the pollutant-id hop happens
    /// one layer up in ScrubberCalculationEngine, same as the Phase 1 lookups.
    /// </summary>
    public interface IDiffusionPropertyLookup
    {
        DiffusionSpeciesData? GetByComponentCode(string code);
    }
}