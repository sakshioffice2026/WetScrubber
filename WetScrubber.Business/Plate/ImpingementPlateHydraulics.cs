using System;
using System.Collections.Generic;
using WetScrubber.Business.Common;

namespace WetScrubber.Business.Plate
{
    /// <summary>
    /// Inputs for the single-plate hydraulic calculation (SI units).
    /// Every default below is a screening value that still needs source confirmation
    /// (see ImpingementPlateHydraulics.Constants for source, range and status).
    /// </summary>
    public sealed class PlateHydraulicsInput
    {
        public double GasFlowM3s { get; set; }
        public double GasDensityKgM3 { get; set; }
        public double LiquidDensityKgM3 { get; set; } = 1000.0;
        public double LiquidFlowM3s { get; set; }
        public double SurfaceTensionNm { get; set; } = 0.072;

        // Plate geometry
        public double ColumnDiameterM { get; set; }
        public double WeirLengthFraction { get; set; } = ImpingementPlateHydraulics.DefaultWeirLengthFraction;     // Lw / D
        public double OpenAreaFraction { get; set; } = ImpingementPlateHydraulics.DefaultOpenAreaFraction;      // hole area / active area
        public double HoleDiameterM { get; set; } = ImpingementPlateHydraulics.DefaultHoleDiameterM;
        public double WeirHeightM { get; set; } = ImpingementPlateHydraulics.DefaultWeirHeightM;
        public double TraySpacingM { get; set; } = ImpingementPlateHydraulics.DefaultTraySpacingM;

        // Correlation parameters (UNCONFIRMED defaults)
        public double OrificeCoefficient { get; set; } = ImpingementPlateHydraulics.DefaultOrificeCoefficient;
        public double AerationFactor { get; set; } = ImpingementPlateHydraulics.DefaultAerationFactor;
        public double FloodCapacityFactorMs { get; set; } = ImpingementPlateHydraulics.DefaultFloodCapacityFactorMs;

        // Limits
        public double MinHoleFFactor { get; set; } = ImpingementPlateHydraulics.DefaultMinHoleFFactor;
        public double MaxBackupRatio { get; set; } = ImpingementPlateHydraulics.DefaultMaxBackupRatio;
        public double RecommendedFloodPercent { get; set; } = ImpingementPlateHydraulics.RecommendedFloodPercent;
    }

    /// <summary>
    /// Output of the single-plate hydraulic calculation.
    /// Heads are metres of clear liquid; pressure drops are Pa.
    /// </summary>
    public sealed class PlateHydraulicsResult
    {
        // Areas
        public double ColumnDiameterM { get; set; }
        public double ColumnAreaM2 { get; set; }
        public double DowncomerAreaFraction { get; set; }
        public double DowncomerAreaM2 { get; set; }
        public double NetAreaM2 { get; set; }
        public double ActiveAreaM2 { get; set; }
        public double HoleAreaM2 { get; set; }
        public double WeirLengthM { get; set; }

        // Velocities and flooding
        public double HoleVelocityMs { get; set; }
        public double HoleFFactor { get; set; }                 // v_h * sqrt(rho_g)
        public double NetVelocityMs { get; set; }
        public double FloodingVelocityMs { get; set; }
        public double PercentFlood { get; set; }

        // Heads
        public double DryHeadM { get; set; }
        public double WeirCrestM { get; set; }
        public double HydraulicHeadM { get; set; }
        public double SurfaceTensionHeadM { get; set; }
        public double TotalHeadM { get; set; }
        public double BackupRatio { get; set; }                 // total head / tray spacing
        public double WeirLoadingM2s { get; set; }              // q_L / Lw

        // Pressure drop
        public double DryPressureDropPa { get; set; }
        public double LiquidHeadPressureDropPa { get; set; }
        public double SurfaceTensionPressureDropPa { get; set; }
        public double PressureDropPerPlatePa { get; set; }

        // Limit flags
        public bool IsWeeping { get; set; }
        public bool IsFlooding { get; set; }                    // percent flood above 100
        public bool ExceedsRecommendedFlood { get; set; }
        public bool ExcessiveBackup { get; set; }
    }

    /// <summary>
    /// Sieve-type plate hydraulics for the impingement plate scrubber, using the
    /// Treybal head model: plate loss = dry head h1 + hydraulic head h2 + surface-tension head h3.
    ///   h1 = (rho_g / rho_L) * v_h^2 / (2 g C0^2)                     (dry orifice head)
    ///   h2 = phi * (h_w + h_ow),  h_ow = 0.666 * (q_L / L_w)^(2/3)     (aerated liquid head, Francis weir)
    ///   h3 = 6 sigma / (d_h rho_L g)                                   (bubble-formation head)
    ///   dP = rho_L g (h1 + h2 + h3)
    /// Weir length and downcomer area come from exact circular-segment geometry.
    /// Weeping: hole F-factor v_h * sqrt(rho_g) below a minimum.
    /// Flooding: Souders-Brown form u_flood = C * sqrt((rho_L - rho_g) / rho_g) on net area.
    /// Status: NotYetValidated. C0, phi, the flooding capacity factor and the weeping limit are
    /// screening values that need source confirmation before any benchmark gate.
    /// </summary>
    public static class ImpingementPlateHydraulics
    {
        public const double GravityMs2 = 9.80665;

        // UNCONFIRMED screening values (see Constants).
        public const double DefaultOrificeCoefficient = 0.70;
        public const double DefaultAerationFactor = 0.60;
        public const double DefaultFloodCapacityFactorMs = 0.08;
        public const double DefaultMinHoleFFactor = 6.0;
        public const double DefaultMaxBackupRatio = 0.50;
        public const double RecommendedFloodPercent = 70.0;

        // Shared geometry defaults and screening ceiling (single source of truth).
        public const double DefaultWeirLengthFraction = 0.7;
        public const double DefaultOpenAreaFraction = 0.10;
        public const double DefaultHoleDiameterM = 0.005;
        public const double DefaultWeirHeightM = 0.05;
        public const double DefaultTraySpacingM = 0.45;
        public const double DefaultPressureDropCeilingPa = 3000.0;

        public const double WeirCrestCoefficient = 0.666;
        public const double SurfaceTensionHeadCoefficient = 6.0;

        /// <summary>
        /// Source, range and status of every constant used by this model.
        /// </summary>
        public static IReadOnlyList<ModelConstantInfo> Constants { get; } = new List<ModelConstantInfo>
        {
            new ModelConstantInfo
            {
                Name = nameof(DefaultOrificeCoefficient), Value = DefaultOrificeCoefficient, Unit = "-",
                Source = "Treybal, Mass-Transfer Operations, sieve-tray dry head; coefficient is read from a chart. Confirm.",
                ValidMin = 0.5, ValidMax = 0.9, Status = ModelValidationStatus.NotYetValidated
            },
            new ModelConstantInfo
            {
                Name = nameof(DefaultAerationFactor), Value = DefaultAerationFactor, Unit = "-",
                Source = "Treybal effective froth density factor. Confirm.",
                ValidMin = 0.4, ValidMax = 0.9, Status = ModelValidationStatus.NotYetValidated
            },
            new ModelConstantInfo
            {
                Name = nameof(WeirCrestCoefficient), Value = WeirCrestCoefficient, Unit = "m^(1/3) s^(2/3)",
                Source = "Francis weir, h_ow = 0.666 (q/Lw)^(2/3). Confirm.",
                ValidMin = null, ValidMax = null, Status = ModelValidationStatus.NotYetValidated
            },
            new ModelConstantInfo
            {
                Name = nameof(SurfaceTensionHeadCoefficient), Value = SurfaceTensionHeadCoefficient, Unit = "-",
                Source = "Treybal residual head h3 = 6 sigma / (d_h rho_L g). Confirm.",
                ValidMin = null, ValidMax = null, Status = ModelValidationStatus.NotYetValidated
            },
            new ModelConstantInfo
            {
                Name = nameof(DefaultFloodCapacityFactorMs), Value = DefaultFloodCapacityFactorMs, Unit = "m/s",
                Source = "Souders-Brown capacity factor for plate flooding; chart value depends on spacing and flow parameter. Confirm.",
                ValidMin = 0.03, ValidMax = 0.15, Status = ModelValidationStatus.NotYetValidated
            },
            new ModelConstantInfo
            {
                Name = nameof(DefaultMinHoleFFactor), Value = DefaultMinHoleFFactor, Unit = "m/s (kg/m3)^0.5",
                Source = "Weeping screen on hole F-factor. Confirm against Eduljee or vendor data.",
                ValidMin = 1.0, ValidMax = 30.0, Status = ModelValidationStatus.NotYetValidated
            },
            new ModelConstantInfo
            {
                Name = nameof(DefaultMaxBackupRatio), Value = DefaultMaxBackupRatio, Unit = "-",
                Source = "Plate head loss as a fraction of tray spacing (downcomer backup screen). Confirm.",
                ValidMin = 0.2, ValidMax = 0.8, Status = ModelValidationStatus.NotYetValidated
            },
            new ModelConstantInfo
            {
                Name = nameof(RecommendedFloodPercent), Value = RecommendedFloodPercent, Unit = "%",
                Source = "Same recommended flooding limit already shown on the Results view.",
                ValidMin = 40.0, ValidMax = 90.0, Status = ModelValidationStatus.NotYetValidated
            }
        };

        // ───────────────────────── Building blocks ─────────────────────────

        /// <summary>
        /// Single downcomer segment area as a fraction of the column cross-section, from the
        /// weir chord ratio Lw/D (exact circular-segment geometry).
        /// </summary>
        public static double DowncomerAreaFraction(double weirLengthFraction)
        {
            if (!(weirLengthFraction > 0.0 && weirLengthFraction < 1.0))
                throw new ArgumentOutOfRangeException(
                    nameof(weirLengthFraction), weirLengthFraction, "Must be strictly between 0 and 1.");

            double theta = Math.Asin(weirLengthFraction);
            return (theta - Math.Sin(theta) * Math.Cos(theta)) / Math.PI;
        }

        public static double HoleVelocityMs(double gasFlowM3s, double holeAreaM2)
        {
            RequirePositive(nameof(gasFlowM3s), gasFlowM3s);
            RequirePositive(nameof(holeAreaM2), holeAreaM2);
            return gasFlowM3s / holeAreaM2;
        }

        public static double HoleFFactor(double holeVelocityMs, double gasDensityKgM3)
        {
            RequirePositive(nameof(holeVelocityMs), holeVelocityMs);
            RequirePositive(nameof(gasDensityKgM3), gasDensityKgM3);
            return holeVelocityMs * Math.Sqrt(gasDensityKgM3);
        }

        /// <summary>Dry orifice head h1 (m of clear liquid).</summary>
        public static double DryHeadM(
            double holeVelocityMs,
            double gasDensityKgM3,
            double liquidDensityKgM3,
            double orificeCoefficient)
        {
            RequirePositive(nameof(holeVelocityMs), holeVelocityMs);
            RequirePositive(nameof(gasDensityKgM3), gasDensityKgM3);
            RequirePositive(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositive(nameof(orificeCoefficient), orificeCoefficient);

            return (gasDensityKgM3 / liquidDensityKgM3)
                   * holeVelocityMs * holeVelocityMs
                   / (2.0 * GravityMs2 * orificeCoefficient * orificeCoefficient);
        }

        /// <summary>Weir crest height over the weir (Francis form), m.</summary>
        public static double WeirCrestHeightM(double liquidFlowM3s, double weirLengthM)
        {
            RequirePositive(nameof(weirLengthM), weirLengthM);
            if (!(liquidFlowM3s >= 0.0) || double.IsInfinity(liquidFlowM3s))
                throw new ArgumentOutOfRangeException(
                    nameof(liquidFlowM3s), liquidFlowM3s, "Must be a non-negative finite number.");

            if (liquidFlowM3s == 0.0) return 0.0;
            return WeirCrestCoefficient * Math.Pow(liquidFlowM3s / weirLengthM, 2.0 / 3.0);
        }

        /// <summary>Aerated liquid head h2 (m of clear liquid).</summary>
        public static double HydraulicHeadM(double weirHeightM, double weirCrestM, double aerationFactor)
        {
            RequirePositive(nameof(weirHeightM), weirHeightM);
            if (!(weirCrestM >= 0.0) || double.IsInfinity(weirCrestM))
                throw new ArgumentOutOfRangeException(
                    nameof(weirCrestM), weirCrestM, "Must be a non-negative finite number.");
            RequirePositive(nameof(aerationFactor), aerationFactor);

            return aerationFactor * (weirHeightM + weirCrestM);
        }

        /// <summary>Residual (surface-tension) head h3 (m of clear liquid).</summary>
        public static double SurfaceTensionHeadM(
            double surfaceTensionNm,
            double holeDiameterM,
            double liquidDensityKgM3)
        {
            RequirePositive(nameof(surfaceTensionNm), surfaceTensionNm);
            RequirePositive(nameof(holeDiameterM), holeDiameterM);
            RequirePositive(nameof(liquidDensityKgM3), liquidDensityKgM3);

            return SurfaceTensionHeadCoefficient * surfaceTensionNm
                   / (holeDiameterM * liquidDensityKgM3 * GravityMs2);
        }

        /// <summary>Souders-Brown flooding velocity on net area, m/s.</summary>
        public static double FloodingVelocityMs(
            double liquidDensityKgM3,
            double gasDensityKgM3,
            double capacityFactorMs)
        {
            RequirePositive(nameof(liquidDensityKgM3), liquidDensityKgM3);
            RequirePositive(nameof(gasDensityKgM3), gasDensityKgM3);
            RequirePositive(nameof(capacityFactorMs), capacityFactorMs);

            if (!(liquidDensityKgM3 > gasDensityKgM3))
                throw new ArgumentOutOfRangeException(
                    nameof(liquidDensityKgM3), liquidDensityKgM3, "Liquid density must exceed gas density.");

            return capacityFactorMs * Math.Sqrt((liquidDensityKgM3 - gasDensityKgM3) / gasDensityKgM3);
        }

        /// <summary>
        /// Column diameter (m) that puts the net-area gas velocity at the given fraction of flooding.
        /// </summary>
        public static double ColumnDiameterForFloodFractionM(
            double gasFlowM3s,
            double liquidDensityKgM3,
            double gasDensityKgM3,
            double capacityFactorMs,
            double weirLengthFraction,
            double designFloodFraction)
        {
            RequirePositive(nameof(gasFlowM3s), gasFlowM3s);
            if (!(designFloodFraction > 0.0 && designFloodFraction <= 1.0))
                throw new ArgumentOutOfRangeException(
                    nameof(designFloodFraction), designFloodFraction, "Must be in (0, 1].");

            double uFlood = FloodingVelocityMs(liquidDensityKgM3, gasDensityKgM3, capacityFactorMs);
            double fd = DowncomerAreaFraction(weirLengthFraction);

            double columnArea = gasFlowM3s / ((1.0 - fd) * designFloodFraction * uFlood);
            return Math.Sqrt(4.0 * columnArea / Math.PI);
        }

        // ───────────────────────── Full evaluation ─────────────────────────

        public static PlateHydraulicsResult Evaluate(PlateHydraulicsInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            RequirePositive(nameof(input.GasFlowM3s), input.GasFlowM3s);
            RequirePositive(nameof(input.GasDensityKgM3), input.GasDensityKgM3);
            RequirePositive(nameof(input.LiquidDensityKgM3), input.LiquidDensityKgM3);
            RequirePositive(nameof(input.SurfaceTensionNm), input.SurfaceTensionNm);
            RequirePositive(nameof(input.ColumnDiameterM), input.ColumnDiameterM);
            RequirePositive(nameof(input.HoleDiameterM), input.HoleDiameterM);
            RequirePositive(nameof(input.WeirHeightM), input.WeirHeightM);
            RequirePositive(nameof(input.TraySpacingM), input.TraySpacingM);
            RequirePositive(nameof(input.OrificeCoefficient), input.OrificeCoefficient);
            RequirePositive(nameof(input.AerationFactor), input.AerationFactor);
            RequirePositive(nameof(input.FloodCapacityFactorMs), input.FloodCapacityFactorMs);
            RequirePositive(nameof(input.MinHoleFFactor), input.MinHoleFFactor);
            RequirePositive(nameof(input.MaxBackupRatio), input.MaxBackupRatio);
            RequirePositive(nameof(input.RecommendedFloodPercent), input.RecommendedFloodPercent);

            if (!(input.LiquidFlowM3s >= 0.0) || double.IsInfinity(input.LiquidFlowM3s))
                throw new ArgumentOutOfRangeException(
                    nameof(input.LiquidFlowM3s), input.LiquidFlowM3s, "Must be a non-negative finite number.");

            if (!(input.OpenAreaFraction > 0.0 && input.OpenAreaFraction < 1.0))
                throw new ArgumentOutOfRangeException(
                    nameof(input.OpenAreaFraction), input.OpenAreaFraction, "Must be strictly between 0 and 1.");

            var r = new PlateHydraulicsResult();

            // Areas
            double columnArea = Math.PI * input.ColumnDiameterM * input.ColumnDiameterM / 4.0;
            double fd = DowncomerAreaFraction(input.WeirLengthFraction);
            double downcomerArea = fd * columnArea;
            double netArea = columnArea - downcomerArea;
            double activeArea = columnArea - 2.0 * downcomerArea;
            double holeArea = input.OpenAreaFraction * activeArea;
            double weirLength = input.WeirLengthFraction * input.ColumnDiameterM;

            r.ColumnDiameterM = input.ColumnDiameterM;
            r.ColumnAreaM2 = columnArea;
            r.DowncomerAreaFraction = fd;
            r.DowncomerAreaM2 = downcomerArea;
            r.NetAreaM2 = netArea;
            r.ActiveAreaM2 = activeArea;
            r.HoleAreaM2 = holeArea;
            r.WeirLengthM = weirLength;

            // Velocities and flooding
            r.HoleVelocityMs = HoleVelocityMs(input.GasFlowM3s, holeArea);
            r.HoleFFactor = HoleFFactor(r.HoleVelocityMs, input.GasDensityKgM3);
            r.NetVelocityMs = input.GasFlowM3s / netArea;
            r.FloodingVelocityMs = FloodingVelocityMs(
                input.LiquidDensityKgM3, input.GasDensityKgM3, input.FloodCapacityFactorMs);
            r.PercentFlood = 100.0 * r.NetVelocityMs / r.FloodingVelocityMs;

            // Heads
            r.DryHeadM = DryHeadM(
                r.HoleVelocityMs, input.GasDensityKgM3, input.LiquidDensityKgM3, input.OrificeCoefficient);
            r.WeirCrestM = WeirCrestHeightM(input.LiquidFlowM3s, weirLength);
            r.HydraulicHeadM = HydraulicHeadM(input.WeirHeightM, r.WeirCrestM, input.AerationFactor);
            r.SurfaceTensionHeadM = SurfaceTensionHeadM(
                input.SurfaceTensionNm, input.HoleDiameterM, input.LiquidDensityKgM3);
            r.TotalHeadM = r.DryHeadM + r.HydraulicHeadM + r.SurfaceTensionHeadM;
            r.BackupRatio = r.TotalHeadM / input.TraySpacingM;
            r.WeirLoadingM2s = input.LiquidFlowM3s / weirLength;

            // Pressure drop
            double rhoLg = input.LiquidDensityKgM3 * GravityMs2;
            r.DryPressureDropPa = rhoLg * r.DryHeadM;
            r.LiquidHeadPressureDropPa = rhoLg * r.HydraulicHeadM;
            r.SurfaceTensionPressureDropPa = rhoLg * r.SurfaceTensionHeadM;
            r.PressureDropPerPlatePa =
                r.DryPressureDropPa + r.LiquidHeadPressureDropPa + r.SurfaceTensionPressureDropPa;

            // Limit flags
            r.IsWeeping = r.HoleFFactor < input.MinHoleFFactor;
            r.IsFlooding = r.PercentFlood > 100.0;
            r.ExceedsRecommendedFlood = r.PercentFlood > input.RecommendedFloodPercent;
            r.ExcessiveBackup = r.BackupRatio > input.MaxBackupRatio;

            return r;
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        }
    }
}