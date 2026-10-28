using System;
using System.Collections.Generic;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class SprayTowerTransferResult
    {
        public double DropletDiameterM { get; set; }
        public double TerminalVelocityMS { get; set; }
        public double SpecificInterfacialAreaM2M3 { get; set; } // a, droplet surface / spray volume
        public double GasFilmCoeffMS { get; set; }               // kG, Ranz-Marshall
        public double HtuM { get; set; }
        public double NtuRequired { get; set; }                  // NaN when target is beyond the equilibrium limit
        public double RequiredHeightM { get; set; }              // height actually used (capped at towerHeightCapM)
        public double NTU { get; set; }                          // NTU achieved at RequiredHeightM
        public double OutletFractionAchieved { get; set; }       // Cout/Cin at RequiredHeightM, A-aware
        public double MinAchievableOutletFraction { get; set; }  // equilibrium limit, 1 - A for A < 1, else 0
        public double RemovalEfficiencyPercent { get; set; }
        public bool TargetFeasible { get; set; }                 // false when target is beyond the equilibrium limit
        public bool HeightCapped { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Spray-tower gas absorption by falling liquid droplets.
    ///
    /// Droplet terminal velocity: iterative Cd-Re solve using the
    /// Schiller-Naumann (1935) drag correlation.
    /// Gas-film mass transfer coefficient: Ranz and Marshall (1952),
    /// Sh = 2 + 0.6*Re^0.5*Sc^(1/3).
    /// Height: Z = NTU * HTU, with the counter-current absorber NTU
    /// NTU = ln[(1 - 1/A) * (Cin/Cout) + 1/A] / (1 - 1/A), A = L/(m*G).
    /// Outlet fraction at any NTU (including a capped height):
    /// Cout/Cin = (1 - 1/A) / (exp(NTU * (1 - 1/A)) - 1/A).
    /// For A &lt; 1 the equilibrium limit is Cout/Cin = 1 - A (maximum
    /// removal A); targets beyond it are reported infeasible, not clamped.
    ///
    /// SCOPE: gas-film control only. Liquid-phase resistance is not
    /// modeled, so removal is overstated for liquid-film-controlled
    /// species (e.g. SO2 in plain water). A warning is attached to every
    /// result.
    ///
    /// vRel (droplet-vs-gas relative velocity): if the caller passes a
    /// value &lt;= 0, terminal velocity is used. For true counter-current
    /// flow the caller should pass the combined relative velocity.
    /// </summary>
    public static class SprayTowerDropletTransfer
    {
        private const double GravityAccel = 9.81;
        private const double MaxDilutePhaseHoldup = 0.10;

        public static SprayTowerTransferResult Calculate(
            double dropletDiameterM,               // Sauter mean diameter; hydraulic nozzles typically 0.0005-0.0015 m
            double dropletVsGasRelativeVelocityMs, // relative velocity between droplet and gas (<= 0 uses terminal velocity)
            double gasVelocityMs,                  // superficial gas velocity in tower, m/s
            double liquidVolumetricFluxM3M2S,      // liquid flow / tower cross-section, m3/(m2*s)
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double gasViscosityPas,
            double gasDiffusivityM2S,              // pollutant diffusivity in gas (Fuller correlation)
            double absorptionFactor,               // A = L/(m*G), liquid-to-gas molar ratio over Henry's constant (y/x)
            double targetOutletFraction,           // Cout/Cin desired, strictly between 0 and 1
            double towerHeightCapM = 15.0)
        {
            RequirePositive(nameof(dropletDiameterM), dropletDiameterM);
            RequirePositive(nameof(gasVelocityMs), gasVelocityMs);
            RequirePositive(nameof(liquidVolumetricFluxM3M2S), liquidVolumetricFluxM3M2S);
            RequirePositive(nameof(gasDensityKgM3), gasDensityKgM3);
            RequirePositive(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositive(nameof(gasViscosityPas), gasViscosityPas);
            RequirePositive(nameof(gasDiffusivityM2S), gasDiffusivityM2S);
            RequirePositive(nameof(absorptionFactor), absorptionFactor);
            RequirePositive(nameof(towerHeightCapM), towerHeightCapM);

            if (!(targetOutletFraction > 0.0 && targetOutletFraction < 1.0))
                throw new ArgumentOutOfRangeException(
                    nameof(targetOutletFraction),
                    targetOutletFraction,
                    "Target outlet fraction must be strictly between 0 and 1.");
            if (!(liquidDensityKgM3 > gasDensityKgM3))
                throw new ArgumentException("Liquid density must exceed gas density.", nameof(liquidDensityKgM3));
            if (double.IsNaN(dropletVsGasRelativeVelocityMs) || double.IsInfinity(dropletVsGasRelativeVelocityMs))
                throw new ArgumentOutOfRangeException(nameof(dropletVsGasRelativeVelocityMs), dropletVsGasRelativeVelocityMs, "Must be finite.");

            var result = new SprayTowerTransferResult();
            result.Warnings.Add("Gas-film control only; removal is overstated for liquid-film-controlled species.");

            double dp = dropletDiameterM;
            double rhoL = liquidDensityKgM3;
            double rhoG = gasDensityKgM3;
            double muG = gasViscosityPas;

            // ── Terminal velocity: iterative Cd-Re solve ────────────
            double vt = TerminalVelocity(dp, rhoL, rhoG, muG, out bool vtConverged);
            if (!vtConverged)
                result.Warnings.Add("Terminal velocity iteration did not converge; Stokes-regime start value may be inaccurate.");

            double vRel = dropletVsGasRelativeVelocityMs > 0.0
                ? dropletVsGasRelativeVelocityMs
                : vt;

            // ── Liquid holdup & specific interfacial area ───────────
            double holdup = liquidVolumetricFluxM3M2S / vRel;
            if (holdup > MaxDilutePhaseHoldup)
                result.Warnings.Add(
                    $"Liquid holdup {holdup:0.####} exceeds the dilute-phase limit {MaxDilutePhaseHoldup}; spray-tower model not applicable at this liquid flux.");

            double specificArea = 6.0 * holdup / dp; // m2 droplet surface / m3 spray volume

            // ── Gas-film coefficient, Ranz-Marshall (1952) ──────────
            double dropletRe = rhoG * vRel * dp / muG;
            double sc = muG / (rhoG * gasDiffusivityM2S);
            double sh = 2.0 + 0.6 * Math.Sqrt(dropletRe) * Math.Pow(sc, 1.0 / 3.0);
            double kG = sh * gasDiffusivityM2S / dp; // m/s

            // ── HTU ──────────────────────────────────────────────────
            double htu = gasVelocityMs / (kG * specificArea);

            // ── NTU required (A-aware) and equilibrium limit ────────
            double invA = 1.0 / absorptionFactor;
            double minOutFrac = absorptionFactor < 1.0 ? 1.0 - absorptionFactor : 0.0;

            double ntuRequired;
            bool feasible;
            if (absorptionFactor < 1.0 && targetOutletFraction <= minOutFrac)
            {
                feasible = false;
                ntuRequired = double.NaN;
                result.Warnings.Add(
                    $"Target outlet fraction {targetOutletFraction:0.####} is at or below the equilibrium limit {minOutFrac:0.####} (A = {absorptionFactor:0.###}); increase L/G or use a more soluble system / reagent.");
            }
            else
            {
                feasible = true;
                ntuRequired = Math.Abs(absorptionFactor - 1.0) < 1e-9
                    ? (1.0 - targetOutletFraction) / targetOutletFraction
                    : Math.Log((1.0 - invA) / targetOutletFraction + invA) / (1.0 - invA);
            }

            // ── Height (capped, never silently) ─────────────────────
            double heightM;
            bool capped = false;
            if (!feasible)
            {
                heightM = towerHeightCapM;
                capped = true;
            }
            else
            {
                double heightNeeded = ntuRequired * htu;
                if (heightNeeded > towerHeightCapM)
                {
                    heightM = towerHeightCapM;
                    capped = true;
                    result.Warnings.Add(
                        $"Required height {heightNeeded:0.##} m exceeds the cap {towerHeightCapM:0.##} m; target not met at the cap.");
                }
                else
                {
                    heightM = heightNeeded;
                }
            }

            double achievedNtu = heightM / htu;
            double outFracAchieved = OutletFractionAtNtu(achievedNtu, absorptionFactor);

            result.DropletDiameterM = dp;
            result.TerminalVelocityMS = vt;
            result.SpecificInterfacialAreaM2M3 = specificArea;
            result.GasFilmCoeffMS = kG;
            result.HtuM = htu;
            result.NtuRequired = ntuRequired;
            result.RequiredHeightM = heightM;
            result.NTU = achievedNtu;
            result.OutletFractionAchieved = outFracAchieved;
            result.MinAchievableOutletFraction = minOutFrac;
            result.RemovalEfficiencyPercent = (1.0 - outFracAchieved) * 100.0;
            result.TargetFeasible = feasible;
            result.HeightCapped = capped;
            return result;
        }

        /// <summary>Cout/Cin of a counter-current absorber with the given NTU and A = L/(m*G).</summary>
        public static double OutletFractionAtNtu(double ntu, double absorptionFactor)
        {
            if (!(ntu >= 0.0) || double.IsInfinity(ntu))
                throw new ArgumentOutOfRangeException(nameof(ntu), ntu, "NTU must be a non-negative finite number.");
            RequirePositive(nameof(absorptionFactor), absorptionFactor);

            if (Math.Abs(absorptionFactor - 1.0) < 1e-9)
                return 1.0 / (1.0 + ntu);

            double invA = 1.0 / absorptionFactor;
            double k = 1.0 - invA;
            return k / (Math.Exp(ntu * k) - invA);
        }

        private static double TerminalVelocity(double dp, double rhoL, double rhoG, double muG, out bool converged)
        {
            converged = false;
            double vt = StokesVelocity(dp, rhoL, rhoG, muG);
            for (int i = 0; i < 50; i++)
            {
                double re = rhoG * vt * dp / muG;
                double cd = DragCoefficient(re);
                double vtNew = Math.Sqrt(4.0 * GravityAccel * dp * (rhoL - rhoG) / (3.0 * cd * rhoG));
                if (double.IsNaN(vtNew) || vtNew <= 0.0) break;
                if (Math.Abs(vtNew - vt) / vt < 1e-4) { vt = vtNew; converged = true; break; }
                vt = vtNew;
            }
            return vt;
        }

        private static double StokesVelocity(double dp, double rhoL, double rhoG, double muG)
            => GravityAccel * dp * dp * (rhoL - rhoG) / (18.0 * muG);

        /// <summary>Schiller-Naumann (1935) drag correlation; Newton regime beyond Re=1000.</summary>
        private static double DragCoefficient(double re)
        {
            if (re < 1e-6) return 24.0 / 1e-6;
            if (re < 1000.0)
                return (24.0 / re) * (1.0 + 0.15 * Math.Pow(re, 0.687));
            return 0.44;
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        }
    }
}