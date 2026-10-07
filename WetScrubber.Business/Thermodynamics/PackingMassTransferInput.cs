namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Packing and tower data required for Onda film coefficients.
    /// </summary>
    public sealed class PackingMassTransferInput
    {
        public double SpecificAreaM2M3 { get; set; }            // aT
        public double NominalSizeM { get; set; }                // dp
        public double CriticalSurfaceTensionNM { get; set; }    // sigma_c
        public double LiquidSurfaceTensionNM { get; set; } = 0.072;
        public double TowerAreaM2 { get; set; }
        public double GasMassFlowKgS { get; set; }
        public double LiquidMassFlowKgS { get; set; }

        public bool IsComplete =>
            SpecificAreaM2M3 > 0
            && NominalSizeM > 0
            && CriticalSurfaceTensionNM > 0
            && LiquidSurfaceTensionNM > 0
            && TowerAreaM2 > 0
            && GasMassFlowKgS > 0
            && LiquidMassFlowKgS > 0;
    }
}
