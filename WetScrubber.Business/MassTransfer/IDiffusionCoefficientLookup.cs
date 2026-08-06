namespace WetScrubber.Business.MassTransfer
{
    public sealed class DiffusionSpeciesData
    {
        public string ComponentCode { get; set; }
        public string Code { get; set; } = "";
        public double? MolarVolumeAtBoilingPointCm3Mol { get; set; }
        public double? AssociationFactor { get; set; }

        public double FullerDiffusionVolumeCm3Mol { get; set; }
    }

    public interface IDiffusionCoefficientLookup
    {
        DiffusionSpeciesData? GetByCode(string code);
    }
}