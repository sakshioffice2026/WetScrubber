using System;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class PressureDropResult
    {
        public double DryBedPressureDropPaM { get; set; }        // Pa per m of packed height (Ergun)
        public double FloodingGasVelocityMS { get; set; }         // m/s superficial (SSH)
        public double ActualGasVelocityMS { get; set; }
        public double PercentFlood { get; set; }
        public double EstimatedOperatingPressureDropPaM { get; set; } // indicative only, see remarks
        public bool ExceedsRecommendedFlood { get; set; }         // true if >70% flood
    }

    /// <summary>
    /// Packed-tower hydraulics: dry-bed pressure drop via the Ergun
    /// (1952) equation (mechanistic, universal constants 150/1.75 —
    /// NOT packing-specific fitted values) and flooding velocity via
    /// Sherwood, Shipley &amp; Holloway (1938), as reproduced in
    /// Treybal "Mass Transfer Operations" and Coulson &amp; Richardson
    /// Vol. 2.
    ///
    /// NOTE: this is NOT the full Eckert Generalized Pressure Drop
    /// Correlation (GPDC) chart. Eckert/Robbins-style GPDC requires a
    /// packing-specific factor (Fp) digitized from published charts —
    /// that data is not available here as verified figures and has
    /// deliberately NOT been fabricated (same policy as every other
    /// flagged "unvalidated constant" elsewhere in this codebase).
    ///
    /// PercentFlood from this class is the primary, defensible design
    /// safety check (same conclusion the GPDC chart would give you).
    /// EstimatedOperatingPressureDropPaM is a rule-of-thumb multiplier
    /// on dry ΔP (wet-bed ΔP commonly cited as 2-4x dry ΔP near
    /// 70-80% flood) — treat as indicative only, not final design ΔP.
    /// </summary>
    public static class PressureDropFloodingCorrelation
    {
        private const double GravityAccel = 9.81; // m/s2
        private const double RecommendedFloodCeilingPercent = 70.0;

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
            if (voidageFraction <= 0 || voidageFraction >= 1)
                throw new ArgumentException("Voidage fraction must be between 0 and 1.", nameof(voidageFraction));
            if (packingSpecificAreaM2M3 <= 0)
                throw new ArgumentException("Specific packing area must be positive.", nameof(packingSpecificAreaM2M3));
            if (nominalPackingSizeM <= 0)
                throw new ArgumentException("Nominal packing size must be positive.", nameof(nominalPackingSizeM));

            double aT = packingSpecificAreaM2M3;
            double eps = voidageFraction;
            double dp = nominalPackingSizeM;
            double G = Math.Max(gasMassVelocityKgM2S, 1e-9);
            double L = Math.Max(liquidMassVelocityKgM2S, 1e-9);
            double rhoG = Math.Max(gasDensityKgM3, 1e-6);
            double rhoL = Math.Max(liquidDensityKgM3, 1e-6);
            double muG = Math.Max(gasViscosityPas, 1e-9);
            double muL = Math.Max(liquidViscosityPas, 1e-6);

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
            //       = -1.75 - 1.75*(L/G)^0.25*(rhoG/rhoL)^0.125
            double flowRatio = L / G;
            double rhs = -1.75 - 1.75 * Math.Pow(flowRatio, 0.25) * Math.Pow(rhoG / rhoL, 0.125);
            double muLcP = muL * 1000.0; // Pa*s -> cP (correlation's original units)
            double lhsConst = aT * rhoG / (GravityAccel * Math.Pow(eps, 3) * rhoL) * Math.Pow(muLcP, 0.2);

            double uGf2 = Math.Pow(10, rhs) / lhsConst;
            double uGf = Math.Sqrt(Math.Max(uGf2, 1e-12));

            double percentFlood = uG / uGf * 100.0;

            // Rule-of-thumb wet-bed multiplier — indicative only, see class remarks.
            double floodFractionClamped = Math.Min(percentFlood, 100.0) / 100.0;
            double wetMultiplier = 1.0 + 3.0 * floodFractionClamped * floodFractionClamped;
            double estimatedWetDeltaPPerM = dryDeltaPPerM * wetMultiplier;

            return new PressureDropResult
            {
                DryBedPressureDropPaM = dryDeltaPPerM,
                FloodingGasVelocityMS = uGf,
                ActualGasVelocityMS = uG,
                PercentFlood = percentFlood,
                EstimatedOperatingPressureDropPaM = estimatedWetDeltaPPerM,
                ExceedsRecommendedFlood = percentFlood > RecommendedFloodCeilingPercent
            };
        }
    }
}