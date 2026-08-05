namespace WetScrubber.Business.MassTransfer
{
    public sealed class PackingData
    {
        public string Code { get; set; } = "";
        public double SpecificAreaM2M3 { get; set; }
        public double NominalSizeM { get; set; }
        public double CriticalSurfaceTensionNM { get; set; }
    }

    public interface IPackingLookup
    {
        PackingData? GetByCode(string code);
    }
}