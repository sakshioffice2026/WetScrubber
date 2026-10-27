using System;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class PressureDropResult
    {
        public double DryBedPressureDropPaM { get; set; }        // Pa per m of packed height (Ergun, dry bed)
        public double FloodingGasVelocityMS { get; set; }         // m/s superficial (SSH, ring/saddle packings)
        public double ActualGasVelocityMS { get; set; }
        public double PercentFlood { get; set; }
        public bool ExceedsRecommendedFlood { get; set; }         // true if >70% flood
    }

    /// <summary>
    /// Packed-tower hydraulics.
    ///
    /// Dry-bed pressure drop: Ergun (1952), universal constants 150 / 1.75.
    /// Valid for dry random beds only. No wet-bed (irrigated) pressure drop
    /// is computed here: no sourced packing-specific wet correlation is
    /// available, and no unsourced multiplier is applied.
    ///
    /// Flooding velocity: Sherwood-Shipley-Holloway (1938) as reproduced
    /// in Treybal and Coulson &amp; Richardson Vol. 2:
    ///
    ///   log10[ uGf^2 * aT * rhoG / (g * eps^3 * rhoL) * muL[cP]^0.2 ]
    ///       = A - 1.75 * (L/G)^0.25 * (rhoG/rhoL)^0.125
    ///
    /// with L/G the MASS flow ratio and A the packing-dependent intercept.
    /// This is a RING / SADDLE correlation. It is not valid for structured
    /// packing or high-performance random packings. The intercept A used
    /// below (0.022) has NOT been verified against the primary source;
    /// treat PercentFlood as an indicative screening check until a
    /// packing-specific flooding correlation (GPDC with verified Fp, or
    /// Billet-Schultes) replaces it.
    /// </summary>
    public static class PressureDropFloodingCorrelation
    {
        private const double GravityAccel = 9.81; // m/s2
        private const double RecommendedFloodCeilingPercent = 70.0;

        // SSH intercept A (ring/saddle packings). UNVERIFIED against source.
        private const double SshIntercept = 0.022;

        /// <summary>Superficial flooding gas velocity (m/s). flowRatio = L/G mass ratio; muL in Pa*s.</summary>
        public static double FloodingVelocity(
            double packingSpecificAreaM2M3,
            double voidageFraction,
            double flowRatio,
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double liquidViscosityPas)
        {
            RequirePositiveFinite(packingSpecificAreaM2M3, nameof(packingSpecificAreaM2M3));
            RequirePositiveFinite(gasDensityKgM3, nameof(gasDensityKgM3));
            RequirePositiveFinite(liquidDensityKgM3, nameof(liquidDensityKgM3));
            RequirePositiveFinite(liquidViscosityPas, nameof(liquidViscosityPas));

            if (double.IsNaN(voidageFraction) || voidageFraction <= 0 || voidageFraction >= 1)
                throw new ArgumentException("Voidage fraction must be between 0 and 1.", nameof(voidageFraction));

            if (double.IsNaN(flowRatio) || double.IsInfinity(flowRatio) || flowRatio < 0)
                throw new ArgumentException("Mass flow ratio L/G must be finite and non-negative.", nameof(flowRatio));

            double rhoG = gasDensityKgM3;
            double rhoL = liquidDensityKgM3;
            double muLcP = liquidViscosityPas * 1000.0;

            // L/G = 0 (no irrigation) is evaluated at a vanishing ratio.
            double ratio = Math.Max(flowRatio, 1e-9);

            double rhs = SshIntercept - 1.75 * Math.Pow(ratio, 0.25) * Math.Pow(rhoG / rhoL, 0.125);
            double lhsConst = packingSpecificAreaM2M3 * rhoG
                / (GravityAccel * Math.Pow(voidageFraction, 3) * rhoL) * Math.Pow(muLcP, 0.2);

            double uGfSquared = Math.Pow(10, rhs) / lhsConst;

            if (double.IsNaN(uGfSquared) || double.IsInfinity(uGfSquared) || uGfSquared <= 0)
                throw new InvalidOperationException("Flooding velocity evaluation produced a non-finite or non-positive value.");

            return Math.Sqrt(uGfSquared);
        }

        public static PressureDropResult Calculate(
            double packingSpecificAreaM2M3,   // aT
            double voidageFraction,            // epsilon (bed porosity)
            double nominalPackingSizeM,        // dp, characteristic packing size
            double gasMassVelocityKgM2S,       // G, gas mass flux based on empty tower area
            double liquidMassVelocityKgM2S,    // L, liquid mass flux based on empty tower area
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double gasViscosityPas,
            double liquidViscosityPas)
        {
            if (double.IsNaN(voidageFraction) || voidageFraction <= 0 || voidageFraction >= 1)
                throw new ArgumentException("Voidage fraction must be between 0 and 1.", nameof(voidageFraction));

            RequirePositiveFinite(packingSpecificAreaM2M3, nameof(packingSpecificAreaM2M3));
            RequirePositiveFinite(nominalPackingSizeM, nameof(nominalPackingSizeM));
            RequirePositiveFinite(gasMassVelocityKgM2S, nameof(gasMassVelocityKgM2S));
            RequirePositiveFinite(gasDensityKgM3, nameof(gasDensityKgM3));
            RequirePositiveFinite(liquidDensityKgM3, nameof(liquidDensityKgM3));
            RequirePositiveFinite(gasViscosityPas, nameof(gasViscosityPas));
            RequirePositiveFinite(liquidViscosityPas, nameof(liquidViscosityPas));

            if (double.IsNaN(liquidMassVelocityKgM2S) || double.IsInfinity(liquidMassVelocityKgM2S) || liquidMassVelocityKgM2S < 0)
                throw new ArgumentException("Liquid mass velocity must be finite and non-negative.", nameof(liquidMassVelocityKgM2S));

            double aT = packingSpecificAreaM2M3;
            double eps = voidageFraction;
            double dp = nominalPackingSizeM;
            double G = gasMassVelocityKgM2S;
            double L = liquidMassVelocityKgM2S;
            double rhoG = gasDensityKgM3;
            double rhoL = liquidDensityKgM3;
            double muG = gasViscosityPas;
            double muL = liquidViscosityPas;

            double uG = G / rhoG; // superficial gas velocity, m/s

            // ── Dry-bed pressure drop, Ergun (1952) ─────────────────
            //   dP/dz = 150*(1-eps)^2*muG*uG/(eps^3*dp^2)
            //         + 1.75*(1-eps)*rhoG*uG^2/(eps^3*dp)
            double viscousTerm = 150.0 * Math.Pow(1 - eps, 2) * muG * uG
                / (Math.Pow(eps, 3) * dp * dp);
            double inertialTerm = 1.75 * (1 - eps) * rhoG * uG * uG
                / (Math.Pow(eps, 3) * dp);
            double dryDeltaPPerM = viscousTerm + inertialTerm;

            // ── Flooding velocity, Sherwood-Shipley-Holloway (1938) ─
            //   log10[ uGf^2 * aT * rhoG / (g*eps^3*rhoL) * muL[cP]^0.2 ]
            //       = A - 1.75*(L/G)^0.25*(rhoG/rhoL)^0.125
            double uGf = FloodingVelocity(aT, eps, L / G, rhoG, rhoL, muL);

            double percentFlood = uG / uGf * 100.0;

            return new PressureDropResult
            {
                DryBedPressureDropPaM = dryDeltaPPerM,
                FloodingGasVelocityMS = uGf,
                ActualGasVelocityMS = uG,
                PercentFlood = percentFlood,
                ExceedsRecommendedFlood = percentFlood > RecommendedFloodCeilingPercent
            };
        }

        private static void RequirePositiveFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentException($"{name} must be positive and finite.", name);
        }
    }
}