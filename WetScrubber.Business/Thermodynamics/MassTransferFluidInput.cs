namespace WetScrubber.Business.Thermodynamics
{
    public sealed class MassTransferFluidInput
    {
        public double GasDensityKgM3 { get; set; }
        public double GasViscosityPas { get; set; }
        public double GasDiffusivityM2S { get; set; }
        public double LiquidDensityKgM3 { get; set; }
        public double LiquidViscosityPas { get; set; }
        public double LiquidDiffusivityM2S { get; set; }
        public double PressureKPa { get; set; }
        public double HenrysDimensionless { get; set; }   // Cg/Cl
    }
}
