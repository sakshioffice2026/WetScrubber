using System;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class OndaResult
    {
        public double WettedAreaM2M3 { get; set; }        // aW
        public double GasFilmCoeffKmolM2SPa { get; set; }  // kG, partial-pressure basis
        public double LiquidFilmCoeffMS { get; set; }      // kL, concentration basis
    }

    /// <summary>
    /// Onda, Takeuchi &amp; Okumoto (1968) packed-column mass transfer
    /// correlation, SI form as republished in Seader/Henley/Roper,
    /// "Separation Process Principles". NOT independently re-derived
    /// against a published worked example — checked for dimensional
    /// consistency only. Treat like every other Phase 0/1 constant:
    /// usable, not yet production-certified.
    /// </summary>
    public static class OndaMassTransferCorrelation
    {
        private const double GravityAccel = 9.81; // m/s2

        public static OndaResult Calculate(
            double packingSpecificAreaM2M3,   // aT
            double nominalPackingSizeM,        // dp
            double criticalSurfaceTensionNM,   // sigma_c, packing material
            double liquidSurfaceTensionNM,     // sigma_L
            double liquidMassVelocityKgM2S,    // L
            double gasMassVelocityKgM2S,        // G
            double liquidDensityKgM3,
            double gasDensityKgM3,
            double liquidViscosityPas,
            double gasViscosityPas,
            double liquidDiffusivityM2S,
            double gasDiffusivityM2S,
            double temperatureK,
            double pressureKPa)
        {
            double aT = packingSpecificAreaM2M3;
            double dp = nominalPackingSizeM;
            double L = Math.Max(liquidMassVelocityKgM2S, 1e-6);
            double G = Math.Max(gasMassVelocityKgM2S, 1e-6);
            double muL = Math.Max(liquidViscosityPas, 1e-6);
            double muG = Math.Max(gasViscosityPas, 1e-9);
            double rhoL = liquidDensityKgM3;
            double rhoG = gasDensityKgM3;
            double dL = liquidDiffusivityM2S;
            double dG = gasDiffusivityM2S;

            // ── Wetted (effective interfacial) area, aW/aT ──────────
            double sigmaRatio = Math.Pow(criticalSurfaceTensionNM / liquidSurfaceTensionNM, 0.75);
            double reL = L / (aT * muL);
            double frL = (L * L * aT) / (rhoL * rhoL * GravityAccel);
            double weL = (L * L) / (rhoL * liquidSurfaceTensionNM * aT);

            double exponent = -1.45 * sigmaRatio
                * Math.Pow(reL, 0.1)
                * Math.Pow(frL, -0.05)
                * Math.Pow(weL, 0.2);

            double aWOverAt = 1.0 - Math.Exp(exponent);
            double aW = Math.Max(aWOverAt, 0.01) * aT;

            // ── Liquid film coefficient, kL [m/s] ───────────────────
            //   kL*(rhoL/(muL*g))^(1/3) = 0.0051*(L/(aW*muL))^(2/3)
            //                           * (muL/(rhoL*DL))^(-1/2)
            //                           * (aT*dp)^0.4
            double reLForKl = L / (aW * muL);
            double scL = muL / (rhoL * dL);
            double gravityTerm = Math.Pow(muL * GravityAccel / rhoL, 1.0 / 3.0);

            double kL = 0.0051
                * Math.Pow(reLForKl, 2.0 / 3.0)
                * Math.Pow(scL, -0.5)
                * Math.Pow(aT * dp, 0.4)
                * gravityTerm;

            // ── Gas film coefficient, kG [kmol/(m2*s*Pa)] ───────────
            //   kG*R*T/(aT*DG) = C*(G/(aT*muG))^0.7*(muG/(rhoG*DG))^(1/3)*(aT*dp)^-2
            //   C = 5.23 for dp >= 15mm, 2.0 otherwise
            double c = dp >= 0.015 ? 5.23 : 2.0;
            double reG = G / (aT * muG);
            double scG = muG / (rhoG * dG);
            const double R = 8314.0; // J/(kmol*K) = Pa*m3/(kmol*K)

            double kG = c
                * Math.Pow(reG, 0.7)
                * Math.Pow(scG, 1.0 / 3.0)
                * Math.Pow(aT * dp, -2.0)
                * (aT * dG) / (R * temperatureK);

            return new OndaResult
            {
                WettedAreaM2M3 = aW,
                GasFilmCoeffKmolM2SPa = kG,
                LiquidFilmCoeffMS = kL
            };
        }
    }
}