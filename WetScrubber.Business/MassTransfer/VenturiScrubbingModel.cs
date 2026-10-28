using System;

namespace WetScrubber.Business.Scrubbing
{
    /// <summary>
    /// Venturi scrubber screening calculations.
    /// The collection equation is an explicit single-droplet impaction model;
    /// it is not represented as a validated Johnstone/Calvert design correlation.
    /// Validate against an appropriate published correlation and plant/vendor data
    /// before using for final design.
    /// </summary>
    public static class VenturiScrubbingModel
    {

        public sealed class Result
        {
            public double ThroatAreaM2 { get; init; }
            public double ThroatDiameterM { get; init; }
            public double ThroatVelocityMS { get; init; }
            public double StokesNumber { get; init; }
            public double SingleDropletCollectionEfficiency { get; init; }
            public double OverallCollectionEfficiency { get; init; }
            public double PressureDropPa { get; init; }
            public bool WithinTypicalVelocityRange { get; init; }
            public string ModelWarning { get; init; } = string.Empty;
        }

        public static Result Calculate(
            double gasFlowM3S,
            double targetThroatVelocityMS,
            double gasDensityKgM3,
            double gasViscosityPas,
            double liquidToGasMassRatio,
            double liquidDensityKgM3,
            double dropletDiameterM,
            double particleDiameterM,
            double particleDensityKgM3,
            double gasMeanFreePathM = 6.6e-8,
            double pressureDropCoefficient = 1.0)
        {
            RequirePositiveFinite(gasFlowM3S, nameof(gasFlowM3S));
            RequirePositiveFinite(targetThroatVelocityMS, nameof(targetThroatVelocityMS));
            RequirePositiveFinite(gasDensityKgM3, nameof(gasDensityKgM3));
            RequirePositiveFinite(gasViscosityPas, nameof(gasViscosityPas));
            RequirePositiveFinite(liquidToGasMassRatio, nameof(liquidToGasMassRatio));
            RequirePositiveFinite(liquidDensityKgM3, nameof(liquidDensityKgM3));
            RequirePositiveFinite(dropletDiameterM, nameof(dropletDiameterM));
            RequirePositiveFinite(particleDiameterM, nameof(particleDiameterM));
            RequirePositiveFinite(particleDensityKgM3, nameof(particleDensityKgM3));
            RequirePositiveFinite(gasMeanFreePathM, nameof(gasMeanFreePathM));
            RequirePositiveFinite(pressureDropCoefficient, nameof(pressureDropCoefficient));

            double throatArea = gasFlowM3S / targetThroatVelocityMS;
            double throatDiameter = Math.Sqrt(4.0 * throatArea / Math.PI);
            double throatVelocity = gasFlowM3S / throatArea;

            double slipCorrection = 1.0 +
                (2.0 * gasMeanFreePathM / particleDiameterM) *
                (1.257 + 0.4 * Math.Exp(-1.1 * particleDiameterM / (2.0 * gasMeanFreePathM)));

            double stokes =
                particleDensityKgM3 * particleDiameterM * particleDiameterM *
                throatVelocity * slipCorrection /
                (18.0 * gasViscosityPas * dropletDiameterM);

            // Smooth screening approximation for single-droplet inertial impaction.
            // It is intentionally isolated and labeled non-final-design; do not treat
            // this equation as the Johnstone/Calvert published correlation.
            double singleDropletEfficiency = stokes / (stokes + 0.35);
            singleDropletEfficiency = Math.Clamp(singleDropletEfficiency, 0.0, 1.0);

            // Number of effective droplet encounters is represented by L/G mass ratio
            // and the liquid/gas density ratio. This remains a screening estimate.
            double encounterNumber = liquidToGasMassRatio * gasDensityKgM3 / liquidDensityKgM3;
            double overallEfficiency = 1.0 - Math.Exp(-singleDropletEfficiency * encounterNumber);

            double pressureDrop = pressureDropCoefficient *
                0.5 * gasDensityKgM3 * throatVelocity * throatVelocity *
                (1.0 + liquidToGasMassRatio * gasDensityKgM3 / liquidDensityKgM3);

            return new Result
            {
                ThroatAreaM2 = throatArea,
                ThroatDiameterM = throatDiameter,
                ThroatVelocityMS = throatVelocity,
                StokesNumber = stokes,
                SingleDropletCollectionEfficiency = singleDropletEfficiency,
                OverallCollectionEfficiency = overallEfficiency,
                PressureDropPa = pressureDrop,
                WithinTypicalVelocityRange = throatVelocity >= 60.0 && throatVelocity <= 120.0,
                ModelWarning = "Screening model only: droplet size must be supplied; collection and pressure-drop equations require validation against a published design correlation and vendor/plant data. Gas-particle collection is not gas absorption."
            };
        }

        private static void RequirePositiveFinite(double value, string name)
        {
            if (!double.IsFinite(value) || value <= 0.0)
                throw new ArgumentOutOfRangeException(name, value, "Value must be positive and finite.");
        }
    }
}
