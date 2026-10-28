using System;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class SprayTowerTransferResult
    {
        public double DropletDiameterM { get; set; }
        public double TerminalVelocityMS { get; set; }
        public double SpecificInterfacialAreaM2M3 { get; set; }
        public double GasFilmCoeffMS { get; set; }
        public double RequiredHeightM { get; set; }
        public double InstalledHeightM { get; set; }
        public double NTU { get; set; }
        public double RemovalEfficiencyPercent { get; set; }
        public double OutletFraction { get; set; }
        public bool IsFeasible { get; set; }
        public string? InfeasibilityReason { get; set; }
    }

    /// <summary>
    /// Gas-film-controlled spray-tower screening model.
    /// Uses Schiller-Naumann drag and Ranz-Marshall gas-film mass transfer.
    /// The counter-current absorption relation is absorption-factor aware.
    /// Liquid-film resistance, reaction enhancement, droplet-size distribution,
    /// coalescence, and entrainment are not represented.
    /// </summary>
    public static class SprayTowerDropletTransfer
    {
        private const double GravityAccel = 9.80665;
        private const double MinimumPositive = 1e-12;

        public static SprayTowerTransferResult Calculate(
            double dropletDiameterM,
            double dropletVsGasRelativeVelocityMs,
            double gasVelocityMs,
            double liquidVolumetricFluxM3M2S,
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double gasViscosityPas,
            double gasDiffusivityM2S,
            double absorptionFactor,
            double targetOutletFraction,
            double towerHeightCapM = 15.0,
            bool evaluateAtTowerHeight = false)
        {
            RequirePositiveFinite(dropletDiameterM, nameof(dropletDiameterM));
            RequirePositiveFinite(dropletVsGasRelativeVelocityMs, nameof(dropletVsGasRelativeVelocityMs));
            RequirePositiveFinite(gasVelocityMs, nameof(gasVelocityMs));
            RequirePositiveFinite(liquidVolumetricFluxM3M2S, nameof(liquidVolumetricFluxM3M2S));
            RequirePositiveFinite(gasDensityKgM3, nameof(gasDensityKgM3));
            RequirePositiveFinite(liquidDensityKgM3, nameof(liquidDensityKgM3));
            RequirePositiveFinite(gasViscosityPas, nameof(gasViscosityPas));
            RequirePositiveFinite(gasDiffusivityM2S, nameof(gasDiffusivityM2S));
            RequirePositiveFinite(absorptionFactor, nameof(absorptionFactor));
            RequirePositiveFinite(towerHeightCapM, nameof(towerHeightCapM));

            if (liquidDensityKgM3 <= gasDensityKgM3)
                throw new ArgumentOutOfRangeException(nameof(liquidDensityKgM3),
                    "Liquid density must exceed gas density for a falling liquid droplet.");

            if (!double.IsFinite(targetOutletFraction) ||
                targetOutletFraction <= 0.0 || targetOutletFraction >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(targetOutletFraction),
                    "Target outlet fraction must be strictly between 0 and 1.");

            double dp = dropletDiameterM;
            double vt = TerminalVelocity(dp, liquidDensityKgM3, gasDensityKgM3, gasViscosityPas);
            double vRel = dropletVsGasRelativeVelocityMs;
            double holdup = liquidVolumetricFluxM3M2S / vRel;

            if (!double.IsFinite(holdup) || holdup <= 0.0 || holdup >= 0.10)
                throw new ArgumentOutOfRangeException(nameof(liquidVolumetricFluxM3M2S),
                    "Calculated spray holdup must be greater than zero and below 0.10; revise the spray design inputs.");

            double specificArea = 6.0 * holdup / dp;
            double dropletRe = gasDensityKgM3 * vRel * dp / gasViscosityPas;
            double sc = gasViscosityPas / (gasDensityKgM3 * gasDiffusivityM2S);
            double sh = 2.0 + 0.6 * Math.Sqrt(dropletRe) * Math.Pow(sc, 1.0 / 3.0);
            double kG = sh * gasDiffusivityM2S / dp;
            double htu = gasVelocityMs / (kG * specificArea);

            if (!double.IsFinite(htu) || htu <= 0.0)
                throw new InvalidOperationException("Calculated HTU is not positive and finite.");

            double minOutletFraction = Math.Max(0.0, 1.0 - absorptionFactor);
            if (targetOutletFraction <= minOutletFraction + 1e-12)
            {
                double limitNtu = towerHeightCapM / htu;
                double limitOutlet = OutletFractionAtNtu(limitNtu, absorptionFactor);

                return new SprayTowerTransferResult
                {
                    DropletDiameterM = dp,
                    TerminalVelocityMS = vt,
                    SpecificInterfacialAreaM2M3 = specificArea,
                    GasFilmCoeffMS = kG,
                    RequiredHeightM = double.PositiveInfinity,
                    InstalledHeightM = towerHeightCapM,
                    NTU = limitNtu,
                    OutletFraction = limitOutlet,
                    RemovalEfficiencyPercent = 100.0 * (1.0 - limitOutlet),
                    IsFeasible = false,
                    InfeasibilityReason = "The requested outlet fraction is at or below the equilibrium limit for the supplied absorption factor."
                };
            }

            double requiredNtu = RequiredNtu(targetOutletFraction, absorptionFactor);
            if (!double.IsFinite(requiredNtu) || requiredNtu < 0.0)
                throw new InvalidOperationException("Required NTU could not be calculated from the supplied absorption factor and target.");

            double requiredHeight = requiredNtu * htu;
            double installedHeight = evaluateAtTowerHeight
                ? towerHeightCapM
                : Math.Min(requiredHeight, towerHeightCapM);
            double achievedNtu = installedHeight / htu;
            double outletFraction = OutletFractionAtNtu(achievedNtu, absorptionFactor);
            bool heightFeasible = requiredHeight <= towerHeightCapM;

            return new SprayTowerTransferResult
            {
                DropletDiameterM = dp,
                TerminalVelocityMS = vt,
                SpecificInterfacialAreaM2M3 = specificArea,
                GasFilmCoeffMS = kG,
                RequiredHeightM = requiredHeight,
                InstalledHeightM = installedHeight,
                NTU = achievedNtu,
                OutletFraction = outletFraction,
                RemovalEfficiencyPercent = 100.0 * (1.0 - outletFraction),
                IsFeasible = heightFeasible,
                InfeasibilityReason = heightFeasible ? null :
                    $"Required height {requiredHeight:F3} m exceeds the configured cap {towerHeightCapM:F3} m."
            };
        }

        public static double RequiredNtu(double targetOutletFraction, double absorptionFactor)
        {
            RequirePositiveFinite(absorptionFactor, nameof(absorptionFactor));
            if (!double.IsFinite(targetOutletFraction) || targetOutletFraction <= 0.0 || targetOutletFraction >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(targetOutletFraction));

            if (Math.Abs(absorptionFactor - 1.0) < 1e-7)
                return (1.0 / targetOutletFraction) - 1.0;

            double equilibriumLimit = Math.Max(0.0, 1.0 - absorptionFactor);
            if (targetOutletFraction <= equilibriumLimit)
                return double.PositiveInfinity;

            double numerator = 1.0 - (1.0 / absorptionFactor);
            double denominator = targetOutletFraction;
            double logArgument = (numerator / denominator) + (1.0 / absorptionFactor);
            if (!double.IsFinite(logArgument) || logArgument <= 0.0)
                return double.PositiveInfinity;

            double ntu = Math.Log(logArgument) / numerator;
            return ntu >= 0.0 && double.IsFinite(ntu) ? ntu : double.PositiveInfinity;
        }

        public static double OutletFractionAtNtu(double ntu, double absorptionFactor)
        {
            RequirePositiveFinite(absorptionFactor, nameof(absorptionFactor));
            if (!double.IsFinite(ntu) || ntu < 0.0)
                throw new ArgumentOutOfRangeException(nameof(ntu));

            if (Math.Abs(absorptionFactor - 1.0) < 1e-7)
                return 1.0 / (1.0 + ntu);

            double exponent = ntu * (1.0 - (1.0 / absorptionFactor));
            double denominator = (Math.Exp(exponent) - 1.0) + 1.0 - (1.0 / absorptionFactor);
            double numerator = 1.0 - (1.0 / absorptionFactor);
            double outlet = numerator / denominator;

            if (!double.IsFinite(outlet))
                return Math.Max(0.0, 1.0 - absorptionFactor);

            return Math.Clamp(outlet, Math.Max(0.0, 1.0 - absorptionFactor), 1.0);
        }

        private static double TerminalVelocity(double dp, double rhoL, double rhoG, double muG)
        {
            double vt = GravityAccel * dp * dp * (rhoL - rhoG) / (18.0 * muG);
            vt = Math.Max(vt, MinimumPositive);

            for (int i = 0; i < 100; i++)
            {
                double re = rhoG * vt * dp / muG;
                double cd = DragCoefficient(re);
                double vtNew = Math.Sqrt(4.0 * GravityAccel * dp * (rhoL - rhoG) / (3.0 * cd * rhoG));
                if (!double.IsFinite(vtNew) || vtNew <= 0.0)
                    throw new InvalidOperationException("Droplet terminal-velocity iteration did not produce a valid value.");
                if (Math.Abs(vtNew - vt) / Math.Max(vt, MinimumPositive) < 1e-7)
                    return vtNew;
                vt = vtNew;
            }

            throw new InvalidOperationException("Droplet terminal-velocity iteration did not converge.");
        }

        private static double DragCoefficient(double reynolds)
        {
            if (!double.IsFinite(reynolds) || reynolds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(reynolds));
            if (reynolds < 1000.0)
                return (24.0 / reynolds) * (1.0 + 0.15 * Math.Pow(reynolds, 0.687));
            return 0.44;
        }

        private static void RequirePositiveFinite(double value, string name)
        {
            if (!double.IsFinite(value) || value <= 0.0)
                throw new ArgumentOutOfRangeException(name, value, "Value must be positive and finite.");
        }
    }
}