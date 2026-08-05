using System.Collections.Generic;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// One species in a gas mixture, with the critical properties an
    /// equation of state needs. MoleFraction values across a mixture
    /// passed to IEquationOfState should sum to 1.0 (enforced by
    /// GasMixtureBuilder, not by this DTO itself).
    /// </summary>
    public sealed class EosComponentInput
    {
        public string Code { get; set; } = "";
        public double MoleFraction { get; set; }
        public double CriticalTemperatureK { get; set; }
        public double CriticalPressureKPa { get; set; }
        public double AcentricFactor { get; set; }
        public double MolecularWeight { get; set; }
    }

    /// <summary>
    /// Result of a real-gas EOS evaluation at a given T, P, composition.
    /// </summary>
    public sealed class EosResult
    {
        /// <summary>Compressibility factor Z. 1.0 = ideal gas; this is the
        /// number the old ideal-gas shortcut implicitly assumed.</summary>
        public double CompressibilityFactor { get; set; }

        public double MolarVolumeM3PerMol { get; set; }

        /// <summary>Real gas density, kg/m3 — the corrected replacement
        /// for the plain ideal-gas-law density calc.</summary>
        public double DensityKgM3 { get; set; }

        public double MixtureMolecularWeight { get; set; }

        /// <summary>Dimensionless EOS parameters, exposed for diagnostics
        /// / unit tests rather than for callers to use directly.</summary>
        public double A { get; set; }
        public double B { get; set; }

        /// <summary>All real, positive roots the cubic produced. Normally
        /// one (single-phase region) or three (inside the two-phase
        /// dome). The vapor root (largest) is what feeds DensityKgM3;
        /// the rest are kept here for anyone diagnosing a
        /// near-condensation condition.</summary>
        public IReadOnlyList<double> AllRealPositiveRoots { get; set; } = System.Array.Empty<double>();
    }

    /// <summary>
    /// Real-gas equation of state. Implementations correct for molecular
    /// volume and intermolecular attraction that the ideal gas law
    /// (PV=nRT, implicitly Z=1) ignores — see ScrubberCalculationEngine's
    /// current CalculateTowerDiameter, which still uses the ideal
    /// correction only. This interface is the seam that lets that method
    /// swap to a real EOS without its callers changing.
    /// </summary>
    public interface IEquationOfState
    {
        /// <param name="components">Gas mixture composition. Mole
        /// fractions should sum to ~1.0.</param>
        /// <param name="temperatureK">Absolute temperature, Kelvin.</param>
        /// <param name="pressureKPa">Absolute pressure, kPa.</param>
        EosResult Evaluate(
            IReadOnlyList<EosComponentInput> components,
            double temperatureK,
            double pressureKPa);
    }
}