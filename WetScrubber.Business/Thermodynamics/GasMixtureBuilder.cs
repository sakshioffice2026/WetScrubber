using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Minimal lookup contract the mixture builder needs. Deliberately
    /// NOT an EF Core repository interface — keeps WetScrubber.Business.
    /// Thermodynamics testable with a plain dictionary and defers the
    /// actual ComponentProperties table read to whoever wires this into
    /// ScrubberCalculationEngine (a future integration step, since that
    /// engine is currently instantiated with a bare `new` in
    /// ScrubberController and has no DB access today).
    /// </summary>
    public interface IComponentPropertyLookup
    {
        EosComponentInput? GetByCode(string code);

        /// <summary>
        /// Resolves via Pollutant.Id (what PollutantInputViewModel.PollutantType
        /// actually stores) -> Pollutant.Code -> ComponentProperties row.
        /// Two hops because the existing pollutant stream data model
        /// stores an int FK, not a code string — see PollutantStream.cs.
        /// </summary>
        EosComponentInput? GetByPollutantId(int pollutantId);
    }

    /// <summary>
    /// Builds an [Air, Pollutant] gas mixture for IEquationOfState from a
    /// pollutant's inlet concentration in ppm.
    ///
    /// LIMITATION (flagged, not hidden): this only handles ONE pollutant
    /// plus a bulk-air balance, matching ScrubberCalculationEngine's
    /// current single-pollutant assumption
    /// (`vm.Pollutants.FirstOrDefault()`). True multi-component gas
    /// streams (several pollutants + real flue-gas composition: CO2,
    /// H2O vapor, O2, N2 individually) are Phase 3/4 work — see the
    /// roadmap's "Conservation & thermal coupling" and "multi-component
    /// / flowsheet" phases. Extending this builder to accept a full
    /// composition list is straightforward when that data model exists;
    /// it is not built yet.
    ///
    /// Air's critical properties are hardcoded here as a pseudo-species
    /// rather than added to the ComponentProperties table, since air is
    /// a mixture itself (not a pure species like the pollutants/liquids
    /// Phase 0 seeded) — treating it as one pseudo-component with
    /// averaged critical properties is a standard, accepted
    /// simplification for this kind of calculation.
    /// </summary>
    public static class GasMixtureBuilder
    {
        // Standard pseudo-critical properties for dry air, used as a
        // single lumped pseudo-component (mixture-averaged, not a real
        // species — same convention as most process simulators use for
        // a bulk air/flue-gas balance).
        private const double AirCriticalTemperatureK = 132.5;
        private const double AirCriticalPressureKPa = 3786.0;
        private const double AirAcentricFactor = 0.035;
        private const double AirMolecularWeight = 28.97;

        public static IReadOnlyList<EosComponentInput> BuildPollutantInAirMixture(
            string pollutantCode,
            double inletConcentrationPpm,
            IComponentPropertyLookup lookup)
        {
            if (lookup == null) throw new ArgumentNullException(nameof(lookup));

            var pollutant = lookup.GetByCode(pollutantCode)
                ?? throw new InvalidOperationException(
                    $"No ComponentProperty row found for pollutant code '{pollutantCode}'. " +
                    "Cannot build an EOS mixture without its critical properties " +
                    "(Tc, Pc, omega) — populate ComponentProperties before calling this.");

            return BuildMixture(pollutant, inletConcentrationPpm);
        }

        /// <summary>
        /// Same as above, but resolves the pollutant via its
        /// PollutantInputViewModel.PollutantType int FK instead of a
        /// code string — matches what ScrubberCalculationEngine
        /// actually has on hand (see PollutantStream.PollutantType /
        /// PollutantInputViewModel.PollutantType, both plain ints, no
        /// code string stored on the stream row itself).
        /// </summary>
        public static IReadOnlyList<EosComponentInput> BuildPollutantInAirMixture(
            int pollutantTypeId,
            double inletConcentrationPpm,
            IComponentPropertyLookup lookup)
        {
            if (lookup == null) throw new ArgumentNullException(nameof(lookup));

            var pollutant = lookup.GetByPollutantId(pollutantTypeId)
                ?? throw new InvalidOperationException(
                    $"No ComponentProperty row found for PollutantType id '{pollutantTypeId}'. " +
                    "Cannot build an EOS mixture without its critical properties " +
                    "(Tc, Pc, omega) — populate ComponentProperties before calling this.");

            return BuildMixture(pollutant, inletConcentrationPpm);
        }

        private static IReadOnlyList<EosComponentInput> BuildMixture(
            EosComponentInput pollutant, double inletConcentrationPpm)
        {
            double pollutantMoleFraction = Math.Max(inletConcentrationPpm, 0.0) / 1_000_000.0;
            double airMoleFraction = 1.0 - pollutantMoleFraction;

            var air = new EosComponentInput
            {
                Code = "Air",
                MoleFraction = airMoleFraction,
                CriticalTemperatureK = AirCriticalTemperatureK,
                CriticalPressureKPa = AirCriticalPressureKPa,
                AcentricFactor = AirAcentricFactor,
                MolecularWeight = AirMolecularWeight
            };

            pollutant.MoleFraction = pollutantMoleFraction;

            return new List<EosComponentInput> { air, pollutant };
        }
    }
}