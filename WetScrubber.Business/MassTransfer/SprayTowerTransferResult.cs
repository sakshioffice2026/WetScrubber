using System;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class SprayTowerTransferResult
    {
        public double DropletDiameterM { get; set; }
        public double TerminalVelocityMS { get; set; }
        public double SpecificInterfacialAreaM2M3 { get; set; } // a, droplet surface / spray volume
        public double GasFilmCoeffMS { get; set; }               // kG, Ranz-Marshall
        public double RequiredHeightM { get; set; }
        public double NTU { get; set; }
        public double RemovalEfficiencyPercent { get; set; }
    }

    /// <summary>
    /// Spray-tower gas absorption by falling liquid droplets — replaces
    /// formulas with no physical basis (TowerHeight proportional to raw
    /// volumetric flow rate; RemovalEfficiency an arbitrary
    /// 1-exp(-0.5*L/G) with no derivation).
    ///
    /// Droplet terminal velocity: iterative Cd-Re solve using the
    /// Schiller-Naumann (1935) drag correlation — standard, universally
    /// cited for spherical particles/droplets (Perry's Ch. 6; Coulson &amp;
    /// Richardson Vol. 2).
    /// Gas-film mass transfer coefficient: Ranz &amp; Marshall (1952),
    /// Sh = 2 + 0.6*Re^0.5*Sc^(1/3) — the standard single-sphere mass
    /// transfer correlation.
    ///
    /// SCOPE CAVEAT: liquid-phase resistance is not modeled — droplets
    /// are treated as gas-film-controlling. Reasonable for highly
    /// soluble gases (NH3, HCl absorption into water) but will
    /// OVERSTATE removal for liquid-film-controlling systems (e.g. SO2
    /// into plain water without alkali). Flagged the same way every
    /// other model-scope limit is flagged elsewhere in this codebase —
    /// do not treat this as valid for liquid-film-controlled species
    /// without adding a two-film resistance term.
    ///
    /// vRel (droplet-vs-gas relative velocity) here uses terminal
    /// velocity alone — correct for a downward-spray/cross-flow
    /// chamber. For a true counter-current tower (gas rising against
    /// falling droplets), the caller should pass an already-combined
    /// relative velocity; this class does not assume tower flow
    /// direction and will not silently add gas velocity for you.
    /// </summary>
    public static class SprayTowerDropletTransfer
    {
        private const double GravityAccel = 9.81;

        public static SprayTowerTransferResult Calculate(
            double dropletDiameterM,           // Sauter mean diameter; hydraulic nozzles typically 0.0005-0.0015 m
            double dropletVsGasRelativeVelocityMs, // relative velocity between droplet and gas (caller supplies; see class remarks)
            double gasVelocityMs,               // superficial gas velocity in tower, m/s
            double liquidVolumetricFluxM3M2S,   // liquid flow / tower cross-section, m3/(m2*s)
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double gasViscosityPas,
            double gasDiffusivityM2S,            // pollutant diffusivity in gas (Fuller correlation)
            double absorptionFactor,             // L*m/G (liquid-to-gas molar ratio / Henry's constant)
            double targetOutletFraction,         // Cout/Cin desired, 0-1
            double towerHeightCapM = 15.0)
        {
            if (dropletDiameterM <= 0)
                throw new ArgumentException("Droplet diameter must be positive.", nameof(dropletDiameterM));
            if (gasDiffusivityM2S <= 0)
                throw new ArgumentException("Gas diffusivity must be positive.", nameof(gasDiffusivityM2S));

            double dp = dropletDiameterM;
            double rhoL = liquidDensityKgM3;
            double rhoG = gasDensityKgM3;
            double muG = Math.Max(gasViscosityPas, 1e-9);

            // ── Terminal velocity: iterative Cd-Re solve ────────────
            double vt = TerminalVelocity(dp, rhoL, rhoG, muG);

            double vRel = dropletVsGasRelativeVelocityMs > 0
                ? dropletVsGasRelativeVelocityMs
                : vt;

            // ── Liquid holdup & specific interfacial area ───────────
            // Continuity: holdup (volume fraction) = liquid flux / droplet velocity
            double holdup = liquidVolumetricFluxM3M2S / Math.Max(vRel, 1e-6);
            holdup = Math.Min(holdup, 0.10); // spray towers are dilute-phase; sanity cap

            double specificArea = 6.0 * holdup / dp; // m2 droplet surface / m3 spray volume

            // ── Gas-film coefficient, Ranz-Marshall (1952) ──────────
            double dropletRe = rhoG * vRel * dp / muG;
            double sc = muG / (rhoG * gasDiffusivityM2S);
            double sh = 2.0 + 0.6 * Math.Sqrt(dropletRe) * Math.Pow(sc, 1.0 / 3.0);
            double kG = sh * gasDiffusivityM2S / dp; // m/s

            // ── Required height via NTU/HTU (gas-film controlling) ──
            double gasVelocity = Math.Max(gasVelocityMs, 1e-6);
            double htu = gasVelocity / Math.Max(kG * specificArea, 1e-9);

            double outFrac = Math.Clamp(targetOutletFraction, 1e-4, 0.999);
            double ntuRequired;
            if (Math.Abs(absorptionFactor - 1.0) < 1e-6)
            {
                ntuRequired = (1.0 - outFrac) / outFrac; // A=1 limiting case, Kremser
            }
            else
            {
                double invA = 1.0 / absorptionFactor;
                double num = (1.0 - invA) / outFrac + invA;
                ntuRequired = Math.Log(num) / (1.0 - invA);
            }
            ntuRequired = Math.Max(ntuRequired, 0.0);

            double heightM = Math.Min(ntuRequired * htu, towerHeightCapM);
            double achievedNtu = heightM / Math.Max(htu, 1e-9);
            double removalFraction = 1.0 - Math.Exp(-achievedNtu);

            return new SprayTowerTransferResult
            {
                DropletDiameterM = dp,
                TerminalVelocityMS = vt,
                SpecificInterfacialAreaM2M3 = specificArea,
                GasFilmCoeffMS = kG,
                RequiredHeightM = heightM,
                NTU = achievedNtu,
                RemovalEfficiencyPercent = removalFraction * 100.0
            };
        }

        private static double TerminalVelocity(double dp, double rhoL, double rhoG, double muG)
        {
            double vt = StokesVelocity(dp, rhoL, rhoG, muG);
            for (int i = 0; i < 25; i++)
            {
                double re = rhoG * vt * dp / muG;
                double cd = DragCoefficient(re);
                double vtNew = Math.Sqrt(4.0 * GravityAccel * dp * (rhoL - rhoG) / (3.0 * cd * rhoG));
                if (double.IsNaN(vtNew) || vtNew <= 0) break;
                if (Math.Abs(vtNew - vt) / vt < 1e-4) { vt = vtNew; break; }
                vt = vtNew;
            }
            return vt;
        }

        private static double StokesVelocity(double dp, double rhoL, double rhoG, double muG)
            => GravityAccel * dp * dp * (rhoL - rhoG) / (18.0 * muG);

        /// <summary>Schiller-Naumann (1935) drag correlation — standard
        /// intermediate-regime fit, Newton regime beyond Re=1000.</summary>
        private static double DragCoefficient(double re)
        {
            if (re < 1e-6) return 24.0 / 1e-6;
            if (re < 1000.0)
                return (24.0 / re) * (1.0 + 0.15 * Math.Pow(re, 0.687));
            return 0.44;
        }
    }
}