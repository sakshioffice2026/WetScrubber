using System;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class MassTransferCoefficients
    {
        /// <summary>kGa — matches DefaultGasFilmCoeff's unit, kmol/(m3·hr·kPa).</summary>
        public double GasFilmCoeff { get; set; }

        /// <summary>kLa — 1/hr (DefaultLiquidFilmCoeff's intended unit; the
        /// "m/hr" in that constant's comment is kL alone, pre-multiplied
        /// by area a it becomes 1/hr — see ScrubberCalculationEngine's
        /// KGa formula, which treats both as already area-inclusive).</summary>
        public double LiquidFilmCoeff { get; set; }
    }

    /// <summary>
    /// Onda, Takeuchi &amp; Okumoto (1968) correlations for gas- and
    /// liquid-film mass transfer coefficients in random packed columns,
    /// via Reynolds/Schmidt dimensionless groups:
    ///
    ///   kG·R·T/(at·DG) = C1 · ReG^0.7 · ScG^(1/3) · (at·dp)^-2.0
    ///   kL·(rhoL/(muL·g))^(1/3) = 0.0051 · ReL'^(2/3) · ScL^-0.5 · (at·dp)^0.4
    ///
    /// Replaces ScrubberCalculationEngine's fixed DefaultGasFilmCoeff /
    /// DefaultLiquidFilmCoeff (0.03 / 0.01) with values derived from real
    /// fluid properties (viscosity, density, diffusivity — from
    /// WilkeChangDiffusivity / GasPhaseDiffusivity) and packing geometry.
    ///
    /// SIMPLIFICATION (flagged, not hidden): wetted interfacial area aw is
    /// approximated as the total packing area `at` (i.e. aw/at = 1). Onda's
    /// own wetted-area correlation needs the packing material's critical
    /// surface tension and the liquid's surface tension, neither of which
    /// exist in ComponentProperty/DiffusionProperty yet — sourcing those is
    /// a follow-up, not something to fabricate here. aw=at is the standard
    /// conservative starting assumption used before that data exists.
    ///
    /// Characteristic packing size dp is derived geometrically from the
    /// existing ap/void-fraction constants (dp = 6(1-eps)/ap) rather than
    /// requiring a new stored value.
    /// </summary>
    public static class OndaMassTransferCorrelation
    {
        private const double GasConstantKPaM3PerKmolK = 8.314; // kPa·m3/(kmol·K)
        private const double GravityAccel = 9.81;               // m/s2

        public static MassTransferCoefficients Calculate(
            double gasMassVelocityKgM2S,
            double liquidMassVelocityKgM2S,
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double gasViscosityPas,
            double liquidViscosityPas,
            double gasDiffusivityM2S,
            double liquidDiffusivityM2S,
            double packingSurfaceAreaM2M3, // at
            double voidFraction,
            double temperatureK,
            double pressureKPa)
        {
            if (gasDiffusivityM2S <= 0 || liquidDiffusivityM2S <= 0
                || packingSurfaceAreaM2M3 <= 0 || gasViscosityPas <= 0
                || liquidViscosityPas <= 0 || gasDensityKgM3 <= 0 || liquidDensityKgM3 <= 0)
            {
                throw new ArgumentException(
                    "All fluid properties and packing area must be positive — " +
                    "caller should fall back to DefaultGasFilmCoeff/DefaultLiquidFilmCoeff " +
                    "on missing data instead of calling this with zeros.");
            }

            double at = packingSurfaceAreaM2M3;
            double dp = 6.0 * (1.0 - voidFraction) / at; // equivalent packing size, m
            double atDp = at * dp;

            // ── Gas film (kG) ────────────────────────────────────────
            double reG = gasMassVelocityKgM2S / (at * gasViscosityPas);
            double scG = gasViscosityPas / (gasDensityKgM3 * gasDiffusivityM2S);
            double c1 = dp > 0.012 ? 5.23 : 2.00; // Onda's large/small packing split, dp in m

            double kG = c1 * Math.Pow(reG, 0.7) * Math.Pow(scG, 1.0 / 3.0)
                        * Math.Pow(atDp, -2.0)
                        * (at * gasDiffusivityM2S) / (GasConstantKPaM3PerKmolK * temperatureK);
            // kG here: kmol/(m2·s·kPa)

            double gasFilmCoeff = kG * at * 3600.0; // -> kGa, kmol/(m3·hr·kPa)

            // ── Liquid film (kL) ─────────────────────────────────────
            double reL = liquidMassVelocityKgM2S / (at * liquidViscosityPas); // aw≈at
            double scL = liquidViscosityPas / (liquidDensityKgM3 * liquidDiffusivityM2S);

            double kL = 0.0051 * Math.Pow(reL, 2.0 / 3.0) * Math.Pow(scL, -0.5)
                        * Math.Pow(atDp, 0.4)
                        * Math.Pow(liquidDensityKgM3 / (liquidViscosityPas * GravityAccel), -1.0 / 3.0);
            // kL here: m/s

            double liquidFilmCoeff = kL * at * 3600.0; // -> kLa, 1/hr

            return new MassTransferCoefficients
            {
                GasFilmCoeff = gasFilmCoeff,
                LiquidFilmCoeff = liquidFilmCoeff
            };
        }
    }
}