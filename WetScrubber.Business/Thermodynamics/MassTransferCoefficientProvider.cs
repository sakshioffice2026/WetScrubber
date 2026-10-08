using System;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.MassTransfer;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Builds Onda film coefficients and combines them into overall volumetric
    /// coefficients (partial-pressure basis, kmol/(m3*hr*kPa)).
    ///   1/KGa = 1/kGa + H' / kLa,   H' = H_cc * R * T_liquid   [kPa*m3/kmol]
    /// Gas-film properties are evaluated at the gas temperature, liquid-film
    /// properties and the Henry's-law conversion at the liquid temperature.
    /// The reactive enhancement factor multiplies the liquid-side kL only.
    /// No silent clamping: invalid input throws.
    /// </summary>
    public static class MassTransferCoefficientProvider
    {
        private const double GasConstantKPaM3KmolK = 8.314462;

        public static MassTransferCoefficients Compute(
            PackingMassTransferInput packing,
            MassTransferFluidInput fluid,
            double gasTemperatureK,
            double liquidTemperatureK,
            double enhancementFactor)
        {
            if (packing == null) throw new ArgumentNullException(nameof(packing));
            if (fluid == null) throw new ArgumentNullException(nameof(fluid));

            RequireFinitePositive(nameof(gasTemperatureK), gasTemperatureK);
            RequireFinitePositive(nameof(liquidTemperatureK), liquidTemperatureK);
            RequireFinitePositive(nameof(fluid.PressureKPa), fluid.PressureKPa);

            if (double.IsNaN(enhancementFactor)
                || double.IsInfinity(enhancementFactor)
                || enhancementFactor < 1.0 - 1e-9)
                throw new PropertyOutOfBoundsException(
                    nameof(enhancementFactor), enhancementFactor, 1.0, double.MaxValue);

            double factor = Math.Max(enhancementFactor, 1.0);

            if (double.IsNaN(fluid.HenrysDimensionless)
                || double.IsInfinity(fluid.HenrysDimensionless)
                || fluid.HenrysDimensionless <= 0)
                throw new PropertyOutOfBoundsException(
                    nameof(fluid.HenrysDimensionless), fluid.HenrysDimensionless, 0.0, double.MaxValue);

            var gas = new GasPhaseProperties(
                gasTemperatureK,
                fluid.GasDensityKgM3,
                fluid.GasViscosityPas,
                fluid.GasDiffusivityM2S);

            var liquid = new LiquidPhaseProperties(
                liquidTemperatureK,
                fluid.LiquidDensityKgM3,
                fluid.LiquidViscosityPas,
                fluid.LiquidDiffusivityM2S,
                packing.LiquidSurfaceTensionNM);

            var onda = OndaMassTransferCorrelation.Calculate(packing, gas, liquid);

            // kmol/(m3*s*kPa) and 1/s, then per hour
            double kGaPerHr = onda.GasFilmCoeffKmolM2SPa * 1000.0 * onda.WettedAreaM2M3 * 3600.0;
            double kLaPerHr = onda.LiquidFilmCoeffMS * factor * onda.WettedAreaM2M3 * 3600.0;

            if (!(kGaPerHr > 0) || double.IsInfinity(kGaPerHr))
                throw new PropertyOutOfBoundsException(nameof(kGaPerHr), kGaPerHr, 0.0, double.MaxValue);
            if (!(kLaPerHr > 0) || double.IsInfinity(kLaPerHr))
                throw new PropertyOutOfBoundsException(nameof(kLaPerHr), kLaPerHr, 0.0, double.MaxValue);

            double hPrime = fluid.HenrysDimensionless * GasConstantKPaM3KmolK * liquidTemperatureK;

            double gasResistance = 1.0 / kGaPerHr;
            double liquidResistance = hPrime / kLaPerHr;
            double totalResistance = gasResistance + liquidResistance;

            if (!(totalResistance > 0) || double.IsInfinity(totalResistance))
                throw new PropertyOutOfBoundsException(
                    nameof(totalResistance), totalResistance, 0.0, double.MaxValue);

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

        /// <summary>
        /// Legacy single-temperature entry point. The same temperature is applied
        /// to gas and liquid phases; use the overload with separate temperatures.
        /// </summary>
        [Obsolete("Use Compute(packing, fluid, gasTemperatureK, liquidTemperatureK, enhancementFactor).")]
        public static MassTransferCoefficients Compute(
            PackingMassTransferInput packing,
            MassTransferFluidInput fluid,
            double temperatureK,
            double enhancementFactor)
        {
            return Compute(packing, fluid, temperatureK, temperatureK, enhancementFactor);
        }

        private static void RequireFinitePositive(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new PropertyOutOfBoundsException(name, value, double.Epsilon, double.MaxValue);
        }
    }
}