using System;

namespace WetScrubber.Business.Particulate
{
    /// <summary>
    /// Cunningham slip correction factor and gas mean free path.
    /// Slip factor: Davies (1945)  C = 1 + Kn * (1.257 + 0.400 * exp(-1.10 / Kn)),  Kn = 2 * lambda / dp.
    /// Mean free path (kinetic theory): lambda = mu / (0.499 * P * sqrt(8 * M / (pi * R * T))).
    /// Constants: Davies 1.257 / 0.400 / 1.10; kinetic-theory factor 0.499 (Chapman-Enskog hard-sphere).
    /// Status: standard textbook correlation; verify against Cooper and Alley / Hinds before release.
    /// </summary>
    public static class CunninghamSlip
    {
        public const double DaviesA = 1.257;
        public const double DaviesB = 0.400;
        public const double DaviesC = 1.10;
        public const double KineticFactor = 0.499;

        public const double UniversalGasConstant = 8.314462618;   // J/(mol K)
        public const double AirMolarMassKgMol = 0.028964;         // kg/mol

        /// <summary>
        /// Gas mean free path (m).
        /// </summary>
        /// <param name="gasViscosityPas">Gas dynamic viscosity (Pa s).</param>
        /// <param name="temperatureK">Gas temperature (K).</param>
        /// <param name="pressurePa">Absolute gas pressure (Pa).</param>
        /// <param name="molarMassKgMol">Gas molar mass (kg/mol), default air.</param>
        public static double MeanFreePathM(
            double gasViscosityPas,
            double temperatureK,
            double pressurePa,
            double molarMassKgMol = AirMolarMassKgMol)
        {
            RequirePositive(nameof(gasViscosityPas), gasViscosityPas);
            RequirePositive(nameof(temperatureK), temperatureK);
            RequirePositive(nameof(pressurePa), pressurePa);
            RequirePositive(nameof(molarMassKgMol), molarMassKgMol);

            double meanSpeed = Math.Sqrt(8.0 * UniversalGasConstant * temperatureK / (Math.PI * molarMassKgMol));
            double molarOverR = molarMassKgMol / (UniversalGasConstant * temperatureK);
            double rho = pressurePa * molarOverR;
            return gasViscosityPas / (KineticFactor * rho * meanSpeed);
        }

        /// <summary>
        /// Knudsen number for a particle of the given diameter.
        /// </summary>
        public static double KnudsenNumber(double particleDiameterM, double meanFreePathM)
        {
            RequirePositive(nameof(particleDiameterM), particleDiameterM);
            RequirePositive(nameof(meanFreePathM), meanFreePathM);
            return 2.0 * meanFreePathM / particleDiameterM;
        }

        /// <summary>
        /// Cunningham slip correction factor (dimensionless, >= 1).
        /// </summary>
        public static double Factor(double particleDiameterM, double meanFreePathM)
        {
            double kn = KnudsenNumber(particleDiameterM, meanFreePathM);
            return 1.0 + kn * (DaviesA + DaviesB * Math.Exp(-DaviesC / kn));
        }

        /// <summary>
        /// Slip factor from gas state: computes mean free path then C.
        /// </summary>
        public static double Factor(
            double particleDiameterM,
            double gasViscosityPas,
            double temperatureK,
            double pressurePa,
            double molarMassKgMol = AirMolarMassKgMol)
        {
            double lambda = MeanFreePathM(gasViscosityPas, temperatureK, pressurePa, molarMassKgMol);
            return Factor(particleDiameterM, lambda);
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Value must be a positive finite number.");
        }
    }
}
