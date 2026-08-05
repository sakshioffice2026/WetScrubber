using System;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Fuller-Schettler-Giddings (1966) estimation of the binary gas-phase
    /// diffusion coefficient, used here for pollutant-in-air:
    ///
    ///   D_AB [cm^2/s] = 0.00143 * T^1.75
    ///       / ( P[atm] * sqrt(M_AB) * (SumV_A^(1/3) + SumV_B^(1/3))^2 )
    ///
    ///   M_AB = 2 / (1/M_A + 1/M_B)   (harmonic mean MW, g/mol)
    ///
    /// Air's Fuller diffusion volume (19.7 cm^3/mol) is a standard
    /// published constant, same convention as GasMixtureBuilder's
    /// hardcoded air pseudo-critical properties.
    ///
    /// Feeds the Sherwood/Schmidt correlation for kg, mirroring the role
    /// WilkeChangDiffusivity plays for kl.
    /// </summary>
    public static class GasPhaseDiffusivity
    {
        private const double AirFullerVolumeCm3Mol = 19.7;
        private const double FullerConstant = 0.00143;

        /// <summary>
        /// Null when required data is missing — same fallback contract as
        /// WilkeChangDiffusivity.TryCalculate: caller keeps the current
        /// hardcoded DefaultGasFilmCoeff rather than get an exception.
        /// </summary>
        public static double? TryCalculate(
            DiffusionSpeciesData? solute,
            double soluteMolecularWeight,
            double temperatureK,
            double pressureKPa)
        {
            if (solute?.FullerDiffusionVolumeCm3Mol is not double sumVA || sumVA <= 0)
                return null;

            if (soluteMolecularWeight <= 0 || temperatureK <= 0 || pressureKPa <= 0)
                return null;

            const double airMolecularWeight = 28.97;
            double mAB = 2.0 / (1.0 / soluteMolecularWeight + 1.0 / airMolecularWeight);

            double pressureAtm = pressureKPa / 101.325;

            double volTerm = Math.Pow(sumVA, 1.0 / 3.0) + Math.Pow(AirFullerVolumeCm3Mol, 1.0 / 3.0);

            double dAbCm2S = FullerConstant * Math.Pow(temperatureK, 1.75)
                / (pressureAtm * Math.Sqrt(mAB) * volTerm * volTerm);

            return dAbCm2S > 0 ? dAbCm2S : null;
        }

        /// <summary>Convenience unit conversion: cm^2/s -> m^2/s.</summary>
        public static double CentimeterSqPerSecToMeterSqPerSec(double dAbCm2S)
            => dAbCm2S * 1e-4;
    }
}