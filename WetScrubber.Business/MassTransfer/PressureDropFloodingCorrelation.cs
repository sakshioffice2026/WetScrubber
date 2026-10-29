using System;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class PressureDropResult
    {
        public double DryBedPressureDropPaM { get; set; }        // Pa per m of packed height (Ergun, dry bed)
        public double FloodingGasVelocityMS { get; set; }         // m/s superficial (SSH-type, ring/saddle)
        public double ActualGasVelocityMS { get; set; }
        public double PercentFlood { get; set; }
        public double PercentFloodUpperBound { get; set; }          // PercentFlood x 1.30 (screening uncertainty)
        public bool ExceedsRecommendedFloodUpperBound { get; set; }
        public string FloodingModelNote { get; set; } = string.Empty;
    }

    /// <summary>
    /// Packed-tower hydraulics.
    ///
    /// Dry-bed pressure drop: Ergun (1952), universal constants 150 / 1.75.
    /// Wet-bed pressure drop is NOT computed here; the engine applies a
    /// single indicative wet factor (ScrubberCalculationEngine.
    /// CalculateIndicativeWetFactor). There is one wet-bed implementation.
    ///
    /// Flooding velocity: Sherwood-Shipley-Holloway (SSH) type correlation,
    ///   log10[ uGf^2 * aT * rhoG / (g * eps^3 * rhoL) * muL[cP]^0.2 ]
    ///       = A - 1.75 * (L/G)^0.25 * (rhoG/rhoL)^0.125
    /// with A = SshIntercept (see constant).
    ///
    /// LIMITS: SSH is a ring/saddle correlation. It is not packing-specific
    /// (no Fp / Billet constants) and is not a GPDC chart. Treat the
    /// result as a screening-level flooding estimate (typical uncertainty
    /// +-20-30 %) until a packing-specific correlation (GPDC with a
    /// verified Fp, or Billet flooding) is implemented. The intercept A
    /// must be confirmed against Treybal / Coulson &amp; Richardson before
    /// the result is used for final design.
    /// </summary>
    public static class PressureDropFloodingCorrelation
    {
        private const double GravityAccel = 9.81; // m/s2
        private const double RecommendedFloodCeilingPercent = 70.0;

        // SSH intercept A. UNVERIFIED against the primary source; the
        // formula above is written with this same symbol so that code and
        // documentation agree. Confirm before final-design use.
        // Screening default only. Callers may override it per packing via
        // the sshIntercept parameter once a verified value is available.
        public const double DefaultSshIntercept = 0.022;

        public const string FloodingCorrelationNote =
            "SSH-type flooding correlation (ring/saddle packings): " +
            "screening-level estimate, not packing-specific.";

        private static void RequirePositiveFinite(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                throw new ArgumentOutOfRangeException(
                    name, value, name + " must be positive and finite.");
        }

        /// <summary>Superficial flooding gas velocity (m/s). flowRatio = L/G mass ratio; muL in Pa*s.</summary>
        public static double FloodingVelocity(
            double packingSpecificAreaM2M3,
            double voidageFraction,
            double flowRatio,
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double liquidViscosityPas,
            double sshIntercept = DefaultSshIntercept)
        {
            if (double.IsNaN(sshIntercept) || double.IsInfinity(sshIntercept))
                throw new ArgumentOutOfRangeException(
                    nameof(sshIntercept), sshIntercept,
                    "SSH intercept must be finite.");

            RequirePositiveFinite(nameof(packingSpecificAreaM2M3), packingSpecificAreaM2M3);
            RequirePositiveFinite(nameof(flowRatio), flowRatio);
            RequirePositiveFinite(nameof(gasDensityKgM3), gasDensityKgM3);
            RequirePositiveFinite(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositiveFinite(nameof(liquidViscosityPas), liquidViscosityPas);

            if (double.IsNaN(voidageFraction) || voidageFraction <= 0.0 || voidageFraction >= 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(voidageFraction), voidageFraction,
                    "Voidage fraction must be between 0 and 1.");

            double rhoG = gasDensityKgM3;
            double rhoL = liquidDensityKgM3;
            double muLcP = liquidViscosityPas * 1000.0;

            double rhs = sshIntercept
                - 1.75 * Math.Pow(flowRatio, 0.25) * Math.Pow(rhoG / rhoL, 0.125);

            double lhsConst = packingSpecificAreaM2M3 * rhoG
                / (GravityAccel * Math.Pow(voidageFraction, 3) * rhoL)
                * Math.Pow(muLcP, 0.2);

            double uGfSquared = Math.Pow(10.0, rhs) / lhsConst;

            if (double.IsNaN(uGfSquared) || double.IsInfinity(uGfSquared) || uGfSquared <= 0.0)
                throw new InvalidOperationException(
                    "Flooding velocity could not be evaluated for the supplied inputs.");

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
            double liquidViscosityPas,
            double sshIntercept = DefaultSshIntercept)
        {
            if (voidageFraction <= 0 || voidageFraction >= 1)
                throw new ArgumentException("Voidage fraction must be between 0 and 1.", nameof(voidageFraction));

            RequirePositiveFinite(nameof(packingSpecificAreaM2M3), packingSpecificAreaM2M3);
            RequirePositiveFinite(nameof(nominalPackingSizeM), nominalPackingSizeM);
            RequirePositiveFinite(nameof(gasMassVelocityKgM2S), gasMassVelocityKgM2S);
            RequirePositiveFinite(nameof(liquidMassVelocityKgM2S), liquidMassVelocityKgM2S);
            RequirePositiveFinite(nameof(gasDensityKgM3), gasDensityKgM3);
            RequirePositiveFinite(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositiveFinite(nameof(gasViscosityPas), gasViscosityPas);
            RequirePositiveFinite(nameof(liquidViscosityPas), liquidViscosityPas);

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

            // Dry-bed pressure drop, Ergun (1952)
            //   dP/dz = 150*(1-eps)^2*muG*uG/(eps^3*dp^2)
            //         + 1.75*(1-eps)*rhoG*uG^2/(eps^3*dp)
            // Equivalent particle diameter from specific area: d = 6(1-eps)/aT
            double dEq = 6.0 * (1.0 - eps) / aT;
            double viscousTerm = 150.0 * Math.Pow(1 - eps, 2) * muG * uG
                / (Math.Pow(eps, 3) * dEq * dEq);
            double inertialTerm = 1.75 * (1 - eps) * rhoG * uG * uG
                / (Math.Pow(eps, 3) * dEq);
            double dryDeltaPPerM = viscousTerm + inertialTerm;

            double uGf = FloodingVelocity(aT, eps, L / G, rhoG, rhoL, muL, sshIntercept);

            double percentFlood = uG / uGf * 100.0;

            const double floodingUncertaintyFraction = 0.30;

            return new PressureDropResult
            {
                DryBedPressureDropPaM = dryDeltaPPerM,
                FloodingGasVelocityMS = uGf,
                ActualGasVelocityMS = uG,
                PercentFlood = percentFlood,
                ExceedsRecommendedFlood = percentFlood > RecommendedFloodCeilingPercent,
                PercentFloodUpperBound = percentFlood * (1.0 + floodingUncertaintyFraction),
                ExceedsRecommendedFloodUpperBound =
                    percentFlood * (1.0 + floodingUncertaintyFraction) > RecommendedFloodCeilingPercent,
                FloodingModelNote = FloodingCorrelationNote
            
        };
        }
    }
}