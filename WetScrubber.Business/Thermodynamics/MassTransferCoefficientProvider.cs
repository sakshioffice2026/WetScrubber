using System;
using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Builds Onda film coefficients and combines them into overall volumetric
    /// coefficients (partial-pressure basis, kmol/(m3*hr*kPa)).
    ///   1/KGa = 1/kGa + H' / kLa,   H' = H_cc * R * T   [kPa*m3/kmol]
    /// The reactive enhancement factor multiplies the liquid-side kL only.
    /// </summary>
    public static class MassTransferCoefficientProvider
    {
        private const double GasConstantKPaM3KmolK = 8.314;

        public static MassTransferCoefficients Compute(
            PackingMassTransferInput packing,
            MassTransferFluidInput fluid,
            double temperatureK,
            double enhancementFactor)
        {
            if (packing == null) throw new ArgumentNullException(nameof(packing));
            if (fluid == null) throw new ArgumentNullException(nameof(fluid));

            double area = Math.Max(packing.TowerAreaM2, 1e-9);
            double liquidMassVelocity = packing.LiquidMassFlowKgS / area;
            double gasMassVelocity = packing.GasMassFlowKgS / area;
            double factor = Math.Max(enhancementFactor, 1.0);

            var onda = OndaMassTransferCorrelation.Calculate(
                packingSpecificAreaM2M3: packing.SpecificAreaM2M3,
                nominalPackingSizeM: packing.NominalSizeM,
                criticalSurfaceTensionNM: packing.CriticalSurfaceTensionNM,
                liquidSurfaceTensionNM: packing.LiquidSurfaceTensionNM,
                liquidMassVelocityKgM2S: liquidMassVelocity,
                gasMassVelocityKgM2S: gasMassVelocity,
                liquidDensityKgM3: fluid.LiquidDensityKgM3,
                gasDensityKgM3: fluid.GasDensityKgM3,
                liquidViscosityPas: fluid.LiquidViscosityPas,
                gasViscosityPas: fluid.GasViscosityPas,
                liquidDiffusivityM2S: fluid.LiquidDiffusivityM2S,
                gasDiffusivityM2S: fluid.GasDiffusivityM2S,
                temperatureK: temperatureK,
                pressureKPa: fluid.PressureKPa);

            // kmol/(m3*s*kPa) and 1/s, then per hour
            double kGaPerHr = onda.GasFilmCoeffKmolM2SPa * 1000.0 * onda.WettedAreaM2M3 * 3600.0;
            double kLaPerHr = onda.LiquidFilmCoeffMS * factor * onda.WettedAreaM2M3 * 3600.0;

            double hPrime = Math.Max(fluid.HenrysDimensionless, 1e-12) * GasConstantKPaM3KmolK * temperatureK;

            double gasResistance = 1.0 / Math.Max(kGaPerHr, 1e-12);
            double liquidResistance = hPrime / Math.Max(kLaPerHr, 1e-12);
            double totalResistance = gasResistance + liquidResistance;

            return new MassTransferCoefficients
            {
                WettedAreaM2M3 = onda.WettedAreaM2M3,
                GasFilmCoeffKmolM2SPa = onda.GasFilmCoeffKmolM2SPa,
                LiquidFilmCoeffMS = onda.LiquidFilmCoeffMS,
                EnhancementFactor = factor,
                GasSideKgaKmolM3HrKPa = kGaPerHr,
                LiquidSideKlaKmolM3HrMolL = kLaPerHr,
                OverallKGaKmolM3HrKPa = 1.0 / totalResistance,
                GasSideResistanceFraction = gasResistance / totalResistance,
                LiquidSideResistanceFraction = liquidResistance / totalResistance
            };
        }
    }
}
