using System;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Hydraulic-property estimate for a limestone/water FGD slurry. It does
    /// not model reaction kinetics, oxidation, gypsum precipitation, or erosion.
    /// </summary>
    public static class LimestoneSlurryHydraulics
    {
        private const double LimestoneDensityKgM3 = 2710.0;
        private const double MaximumPackingFraction = 0.63;

        public static SlurryHydraulicProperties Calculate(
            double carrierLiquidDensityKgM3,
            double carrierLiquidViscosityMPas,
            double solidsLoadingWtPercent)
        {
            if (carrierLiquidDensityKgM3 <= 0) throw new ArgumentOutOfRangeException(nameof(carrierLiquidDensityKgM3));
            if (carrierLiquidViscosityMPas <= 0) throw new ArgumentOutOfRangeException(nameof(carrierLiquidViscosityMPas));
            if (solidsLoadingWtPercent < 0 || solidsLoadingWtPercent >= 50)
                throw new ArgumentOutOfRangeException(nameof(solidsLoadingWtPercent));

            double massFraction = solidsLoadingWtPercent / 100.0;
            double solidsVolumeFraction = (massFraction / LimestoneDensityKgM3) /
                ((massFraction / LimestoneDensityKgM3) + ((1.0 - massFraction) / carrierLiquidDensityKgM3));
            double slurryDensity = 1.0 /
                ((massFraction / LimestoneDensityKgM3) + ((1.0 - massFraction) / carrierLiquidDensityKgM3));

            // Krieger-Dougherty with an intrinsic viscosity of 2.5.
            double relativeViscosity = Math.Pow(
                1.0 - solidsVolumeFraction / MaximumPackingFraction,
                -2.5 * MaximumPackingFraction);

            return new SlurryHydraulicProperties
            {
                SolidsVolumeFraction = solidsVolumeFraction,
                DensityKgM3 = slurryDensity,
                ApparentViscosityMPas = carrierLiquidViscosityMPas * relativeViscosity
            };
        }
    }

    public sealed class SlurryHydraulicProperties
    {
        public double SolidsVolumeFraction { get; init; }
        public double DensityKgM3 { get; init; }
        public double ApparentViscosityMPas { get; init; }
    }
}
