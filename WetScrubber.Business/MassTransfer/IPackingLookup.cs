namespace WetScrubber.Business.MassTransfer
{
    public sealed class PackingData
    {
        public string Code { get; set; } = "";
        public double SpecificAreaM2M3 { get; set; }
        public double NominalSizeM { get; set; }
        public double CriticalSurfaceTensionNM { get; set; }

        /// <summary>Bed voidage (porosity), 0 &lt; value &lt; 1. 0 means "not supplied".</summary>
        public double VoidageFraction { get; set; }
    }

    public interface IPackingLookup
    {
        PackingData? GetByCode(string code);
    }
}