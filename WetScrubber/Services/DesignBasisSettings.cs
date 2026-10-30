using System;
using Microsoft.Extensions.Configuration;

namespace WetScrubber.Services
{
    /// <summary>
    /// Design-basis constants, read from the "DesignBasis" configuration section.
    /// Defaults reproduce the previously hard-coded values and are UNVERIFIED
    /// until reviewed by a process engineer against a design standard or vendor data.
    ///   PackedPumpHeadOffsetM           : static/piping/distributor head added to tower height (m).
    ///   VenturiPumpHeadM                : venturi liquid pump head (m).
    ///   SprayTowerPumpHeadM             : spray tower liquid pump head (m).
    ///   FreeboardFactor                 : fraction of packing height added as freeboard.
    ///   SumpAndTopAllowanceM            : sump plus top disengagement allowance (m).
    ///   SprayTowerAuxiliaryPressureDropPa : ducting/demister allowance added to spray tower drop (Pa).
    ///   DefaultVoidFraction             : packing voidage when the packing record has none.
    ///   DefaultLiquidSurfaceTensionNM   : liquid surface tension used by Onda (N/m).
    ///   SprayTower*/Venturi*            : empirical sizing rules (UNVERIFIED placeholders, see engine).
    ///   Cyclone*                        : wet cyclone limits and efficiencies (UNVERIFIED placeholders):
    ///                                     pump/fan efficiency, wet correction factor on dry dP,
    ///                                     inlet velocity warn/hard limits (m/s), dP ceiling (Pa),
    ///                                     particle size bin count.
    ///
    /// Values are held in one immutable snapshot. Load() builds and validates a complete new
    /// snapshot and publishes it with a single reference swap, so readers never observe a
    /// half-updated or invalid configuration, and a failed Load leaves the previous snapshot intact.
    /// </summary>
    public static class DesignBasisSettings
    {
        public const string SectionName = "DesignBasis";

        public const double DefaultPackedPumpHeadOffsetM = 5.0;
        public const double DefaultVenturiPumpHeadM = 10.0;
        public const double DefaultSprayTowerPumpHeadM = 8.0;
        public const double DefaultFreeboardFactor = 0.30;
        public const double DefaultSumpAndTopAllowanceM = 2.0;
        public const double DefaultSprayTowerAuxiliaryPressureDropPa = 300.0;
        public const double DefaultDefaultVoidFraction = 0.951;
        public const double DefaultDefaultLiquidSurfaceTensionNM = 0.0728;
        public const double DefaultSprayTowerDesignVelocityMs = 0.8;
        public const double DefaultSprayTowerRemovalCoefficient = 0.5;
        public const double DefaultSprayTowerHeightPerM3S = 5.0;
        public const double DefaultSprayTowerBaseHeightM = 2.0;
        public const double DefaultSprayTowerDropletDiameterM = 0.001;
        public const double DefaultSprayTowerRelativeVelocityMs = 2.0;
        public const double DefaultSprayTowerHeightCapM = 15.0;
        public const double DefaultSprayTowerLossCoefficient = 1.5;
        public const double DefaultVenturiDiameterFactor = 2.5;
        public const double DefaultVenturiHeightFactor = 8.0;
        public const double DefaultVenturiParticleGeometricStdDev = 1.0;
        public const double DefaultCyclonePumpEfficiency = 0.60;
        public const double DefaultCycloneFanEfficiency = 0.65;
        public const double DefaultCycloneWetCorrectionFactor = 1.0;
        public const double DefaultCycloneVelocityWarnMinMs = 15.0;
        public const double DefaultCycloneVelocityWarnMaxMs = 27.0;
        public const double DefaultCycloneVelocityHardMinMs = 8.0;
        public const double DefaultCycloneVelocityHardMaxMs = 35.0;
        public const double DefaultCyclonePressureDropCeilingPa = 2500.0;
        // Impingement plate settings (provisional; require engineering validation).
        public const double DefaultImpingementPlateRecommendedFloodPercent = WetScrubber.Business.Plate.ImpingementPlateHydraulics.RecommendedFloodPercent;
        public const double DefaultImpingementPlatePressureDropCeilingPa = WetScrubber.Business.Plate.ImpingementPlateHydraulics.DefaultPressureDropCeilingPa;
        public const double DefaultImpingementPlateHoleAreaFraction = WetScrubber.Business.Plate.ImpingementPlateHydraulics.DefaultOpenAreaFraction;
        public const double DefaultImpingementPlateDischargeCoefficient = WetScrubber.Business.Plate.ImpingementPlateHydraulics.DefaultOrificeCoefficient;
        public const double DefaultImpingementPlateCapacityFactorMPerS = WetScrubber.Business.Plate.ImpingementPlateHydraulics.DefaultFloodCapacityFactorMs;
        public const double DefaultImpingementPlateMurphreeEfficiency = 0.50;
        public const double DefaultImpingementPlateReactiveRemovalCeilingPercent = 99.9;
        public const double DefaultImpingementPlateParticleDensityKgM3 = 1500.0;
        public const double DefaultImpingementPlateParticleGeometricStdDev = 2.0;
        public const double DefaultImpingementPlateStokesNumberAt50Percent = 0.5;
        public const int DefaultCycloneSizeBinCount = 20;

        private sealed class Snapshot
        {
            public double PackedPumpHeadOffsetM { get; init; } = DefaultPackedPumpHeadOffsetM;
            public double VenturiPumpHeadM { get; init; } = DefaultVenturiPumpHeadM;
            public double SprayTowerPumpHeadM { get; init; } = DefaultSprayTowerPumpHeadM;
            public double FreeboardFactor { get; init; } = DefaultFreeboardFactor;
            public double SumpAndTopAllowanceM { get; init; } = DefaultSumpAndTopAllowanceM;
            public double SprayTowerAuxiliaryPressureDropPa { get; init; } = DefaultSprayTowerAuxiliaryPressureDropPa;
            public double DefaultVoidFraction { get; init; } = DefaultDefaultVoidFraction;
            public double DefaultLiquidSurfaceTensionNM { get; init; } = DefaultDefaultLiquidSurfaceTensionNM;
            public double SprayTowerDropletDiameterM { get; init; } = DefaultSprayTowerDropletDiameterM;
            public double SprayTowerRelativeVelocityMs { get; init; } = DefaultSprayTowerRelativeVelocityMs;
            public double SprayTowerHeightCapM { get; init; } = DefaultSprayTowerHeightCapM;
            public double SprayTowerLossCoefficient { get; init; } = DefaultSprayTowerLossCoefficient;
            public double SprayTowerDesignVelocityMs { get; init; } = DefaultSprayTowerDesignVelocityMs;
            public double SprayTowerRemovalCoefficient { get; init; } = DefaultSprayTowerRemovalCoefficient;
            public double SprayTowerHeightPerM3S { get; init; } = DefaultSprayTowerHeightPerM3S;
            public double SprayTowerBaseHeightM { get; init; } = DefaultSprayTowerBaseHeightM;
            public double VenturiParticleGeometricStdDev { get; init; } = DefaultVenturiParticleGeometricStdDev;
            public double VenturiDiameterFactor { get; init; } = DefaultVenturiDiameterFactor;
            public double VenturiHeightFactor { get; init; } = DefaultVenturiHeightFactor;
            public double CyclonePumpEfficiency { get; init; } = DefaultCyclonePumpEfficiency;
            public double CycloneFanEfficiency { get; init; } = DefaultCycloneFanEfficiency;
            public double CycloneWetCorrectionFactor { get; init; } = DefaultCycloneWetCorrectionFactor;
            public double ImpingementPlateRecommendedFloodPercent { get; init; } = DefaultImpingementPlateRecommendedFloodPercent;
            public double ImpingementPlatePressureDropCeilingPa { get; init; } = DefaultImpingementPlatePressureDropCeilingPa;
            public double ImpingementPlateHoleAreaFraction { get; init; } = DefaultImpingementPlateHoleAreaFraction;
            public double ImpingementPlateDischargeCoefficient { get; init; } = DefaultImpingementPlateDischargeCoefficient;
            public double ImpingementPlateCapacityFactorMPerS { get; init; } = DefaultImpingementPlateCapacityFactorMPerS;
            public double ImpingementPlateMurphreeEfficiency { get; init; } = DefaultImpingementPlateMurphreeEfficiency;
            public double ImpingementPlateReactiveRemovalCeilingPercent { get; init; } = DefaultImpingementPlateReactiveRemovalCeilingPercent;
            public double ImpingementPlateParticleDensityKgM3 { get; init; } = DefaultImpingementPlateParticleDensityKgM3;
            public double ImpingementPlateParticleGeometricStdDev { get; init; } = DefaultImpingementPlateParticleGeometricStdDev;
            public double ImpingementPlateStokesNumberAt50Percent { get; init; } = DefaultImpingementPlateStokesNumberAt50Percent;
            public double CycloneVelocityWarnMinMs { get; init; } = DefaultCycloneVelocityWarnMinMs;
            public double CycloneVelocityWarnMaxMs { get; init; } = DefaultCycloneVelocityWarnMaxMs;
            public double CycloneVelocityHardMinMs { get; init; } = DefaultCycloneVelocityHardMinMs;
            public double CycloneVelocityHardMaxMs { get; init; } = DefaultCycloneVelocityHardMaxMs;
            public double CyclonePressureDropCeilingPa { get; init; } = DefaultCyclonePressureDropCeilingPa;
            public int CycloneSizeBinCount { get; init; } = DefaultCycloneSizeBinCount;
        }

        private static volatile Snapshot _current = new Snapshot();

        public static double PackedPumpHeadOffsetM => _current.PackedPumpHeadOffsetM;
        public static double VenturiPumpHeadM => _current.VenturiPumpHeadM;
        public static double SprayTowerPumpHeadM => _current.SprayTowerPumpHeadM;
        public static double FreeboardFactor => _current.FreeboardFactor;
        public static double SumpAndTopAllowanceM => _current.SumpAndTopAllowanceM;
        public static double SprayTowerAuxiliaryPressureDropPa => _current.SprayTowerAuxiliaryPressureDropPa;
        public static double DefaultVoidFraction => _current.DefaultVoidFraction;
        public static double DefaultLiquidSurfaceTensionNM => _current.DefaultLiquidSurfaceTensionNM;
        public static double SprayTowerDropletDiameterM => _current.SprayTowerDropletDiameterM;
        public static double SprayTowerRelativeVelocityMs => _current.SprayTowerRelativeVelocityMs;
        public static double SprayTowerHeightCapM => _current.SprayTowerHeightCapM;
        public static double SprayTowerLossCoefficient => _current.SprayTowerLossCoefficient;
        public static double SprayTowerDesignVelocityMs => _current.SprayTowerDesignVelocityMs;
        public static double SprayTowerRemovalCoefficient => _current.SprayTowerRemovalCoefficient;
        public static double SprayTowerHeightPerM3S => _current.SprayTowerHeightPerM3S;
        public static double SprayTowerBaseHeightM => _current.SprayTowerBaseHeightM;
        public static double VenturiParticleGeometricStdDev => _current.VenturiParticleGeometricStdDev;
        public static double VenturiDiameterFactor => _current.VenturiDiameterFactor;
        public static double VenturiHeightFactor => _current.VenturiHeightFactor;
        public static double CyclonePumpEfficiency => _current.CyclonePumpEfficiency;
        public static double CycloneFanEfficiency => _current.CycloneFanEfficiency;
        public static double CycloneWetCorrectionFactor => _current.CycloneWetCorrectionFactor;
        public static double ImpingementPlateRecommendedFloodPercent => _current.ImpingementPlateRecommendedFloodPercent;
        public static double ImpingementPlatePressureDropCeilingPa => _current.ImpingementPlatePressureDropCeilingPa;
        public static double ImpingementPlateHoleAreaFraction => _current.ImpingementPlateHoleAreaFraction;
        public static double ImpingementPlateDischargeCoefficient => _current.ImpingementPlateDischargeCoefficient;
        public static double ImpingementPlateCapacityFactorMPerS => _current.ImpingementPlateCapacityFactorMPerS;
        public static double ImpingementPlateMurphreeEfficiency => _current.ImpingementPlateMurphreeEfficiency;
        public static double ImpingementPlateReactiveRemovalCeilingPercent => _current.ImpingementPlateReactiveRemovalCeilingPercent;
        public static double ImpingementPlateParticleDensityKgM3 => _current.ImpingementPlateParticleDensityKgM3;
        public static double ImpingementPlateParticleGeometricStdDev => _current.ImpingementPlateParticleGeometricStdDev;
        public static double ImpingementPlateStokesNumberAt50Percent => _current.ImpingementPlateStokesNumberAt50Percent;
        public static double CycloneVelocityWarnMinMs => _current.CycloneVelocityWarnMinMs;
        public static double CycloneVelocityWarnMaxMs => _current.CycloneVelocityWarnMaxMs;
        public static double CycloneVelocityHardMinMs => _current.CycloneVelocityHardMinMs;
        public static double CycloneVelocityHardMaxMs => _current.CycloneVelocityHardMaxMs;
        public static double CyclonePressureDropCeilingPa => _current.CyclonePressureDropCeilingPa;
        public static int CycloneSizeBinCount => _current.CycloneSizeBinCount;

        public static void Load(IConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var section = configuration.GetSection(SectionName);

            int bins = section.GetValue<int?>(nameof(CycloneSizeBinCount)) ?? DefaultCycloneSizeBinCount;
            if (bins < 5 || bins > 200)
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(CycloneSizeBinCount)} must be between 5 and 200; got {bins}.");

            var next = new Snapshot
            {
                PackedPumpHeadOffsetM = Read(section, nameof(PackedPumpHeadOffsetM), DefaultPackedPumpHeadOffsetM, 0.0, 50.0),
                VenturiPumpHeadM = Read(section, nameof(VenturiPumpHeadM), DefaultVenturiPumpHeadM, 0.0, 100.0),
                SprayTowerPumpHeadM = Read(section, nameof(SprayTowerPumpHeadM), DefaultSprayTowerPumpHeadM, 0.0, 100.0),
                FreeboardFactor = Read(section, nameof(FreeboardFactor), DefaultFreeboardFactor, 0.0, 1.0),
                SumpAndTopAllowanceM = Read(section, nameof(SumpAndTopAllowanceM), DefaultSumpAndTopAllowanceM, 0.0, 10.0),
                SprayTowerAuxiliaryPressureDropPa = Read(section, nameof(SprayTowerAuxiliaryPressureDropPa), DefaultSprayTowerAuxiliaryPressureDropPa, 0.0, 5000.0),
                DefaultVoidFraction = Read(section, nameof(DefaultVoidFraction), DefaultDefaultVoidFraction, 0.30, 0.99),
                DefaultLiquidSurfaceTensionNM = Read(section, nameof(DefaultLiquidSurfaceTensionNM), DefaultDefaultLiquidSurfaceTensionNM, 0.010, 0.100),

                SprayTowerDropletDiameterM = Read(section, nameof(SprayTowerDropletDiameterM), DefaultSprayTowerDropletDiameterM, 0.0002, 0.005),
                SprayTowerRelativeVelocityMs = Read(section, nameof(SprayTowerRelativeVelocityMs), DefaultSprayTowerRelativeVelocityMs, 0.1, 10.0),
                SprayTowerHeightCapM = Read(section, nameof(SprayTowerHeightCapM), DefaultSprayTowerHeightCapM, 1.0, 50.0),
                SprayTowerLossCoefficient = Read(section, nameof(SprayTowerLossCoefficient), DefaultSprayTowerLossCoefficient, 0.1, 10.0),
                SprayTowerDesignVelocityMs = Read(section, nameof(SprayTowerDesignVelocityMs), DefaultSprayTowerDesignVelocityMs, 0.2, 3.0),
                SprayTowerRemovalCoefficient = Read(section, nameof(SprayTowerRemovalCoefficient), DefaultSprayTowerRemovalCoefficient, 0.01, 10.0),
                SprayTowerHeightPerM3S = Read(section, nameof(SprayTowerHeightPerM3S), DefaultSprayTowerHeightPerM3S, 0.0, 50.0),
                SprayTowerBaseHeightM = Read(section, nameof(SprayTowerBaseHeightM), DefaultSprayTowerBaseHeightM, 0.0, 20.0),

                VenturiParticleGeometricStdDev = Read(section, nameof(VenturiParticleGeometricStdDev), DefaultVenturiParticleGeometricStdDev, 1.0, 10.0),
                VenturiDiameterFactor = Read(section, nameof(VenturiDiameterFactor), DefaultVenturiDiameterFactor, 1.0, 10.0),
                VenturiHeightFactor = Read(section, nameof(VenturiHeightFactor), DefaultVenturiHeightFactor, 1.0, 30.0),

                CyclonePumpEfficiency = Read(section, nameof(CyclonePumpEfficiency), DefaultCyclonePumpEfficiency, 0.10, 1.0),
                CycloneFanEfficiency = Read(section, nameof(CycloneFanEfficiency), DefaultCycloneFanEfficiency, 0.10, 1.0),
                CycloneWetCorrectionFactor = Read(section, nameof(CycloneWetCorrectionFactor), DefaultCycloneWetCorrectionFactor, 0.2, 5.0),
                CycloneVelocityHardMinMs = Read(section, nameof(CycloneVelocityHardMinMs), DefaultCycloneVelocityHardMinMs, 1.0, 50.0),
                CycloneVelocityWarnMinMs = Read(section, nameof(CycloneVelocityWarnMinMs), DefaultCycloneVelocityWarnMinMs, 1.0, 50.0),
                CycloneVelocityWarnMaxMs = Read(section, nameof(CycloneVelocityWarnMaxMs), DefaultCycloneVelocityWarnMaxMs, 1.0, 60.0),
                CycloneVelocityHardMaxMs = Read(section, nameof(CycloneVelocityHardMaxMs), DefaultCycloneVelocityHardMaxMs, 1.0, 60.0),
                CyclonePressureDropCeilingPa = Read(section, nameof(CyclonePressureDropCeilingPa), DefaultCyclonePressureDropCeilingPa, 100.0, 20000.0),
                CycloneSizeBinCount = bins,

                ImpingementPlateRecommendedFloodPercent = Read(section, nameof(ImpingementPlateRecommendedFloodPercent), DefaultImpingementPlateRecommendedFloodPercent, 10.0, 100.0),
                ImpingementPlatePressureDropCeilingPa = Read(section, nameof(ImpingementPlatePressureDropCeilingPa), DefaultImpingementPlatePressureDropCeilingPa, 100.0, 20000.0),
                ImpingementPlateHoleAreaFraction = Read(section, nameof(ImpingementPlateHoleAreaFraction), DefaultImpingementPlateHoleAreaFraction, 0.01, 0.50),
                ImpingementPlateDischargeCoefficient = Read(section, nameof(ImpingementPlateDischargeCoefficient), DefaultImpingementPlateDischargeCoefficient, 0.10, 1.0),
                ImpingementPlateCapacityFactorMPerS = Read(section, nameof(ImpingementPlateCapacityFactorMPerS), DefaultImpingementPlateCapacityFactorMPerS, 0.01, 1.0),
                ImpingementPlateMurphreeEfficiency = Read(section, nameof(ImpingementPlateMurphreeEfficiency), DefaultImpingementPlateMurphreeEfficiency, 0.05, 1.0),
                ImpingementPlateReactiveRemovalCeilingPercent = Read(section, nameof(ImpingementPlateReactiveRemovalCeilingPercent), DefaultImpingementPlateReactiveRemovalCeilingPercent, 50.0, 100.0),
                ImpingementPlateParticleDensityKgM3 = Read(section, nameof(ImpingementPlateParticleDensityKgM3), DefaultImpingementPlateParticleDensityKgM3, 100.0, 20000.0),
                ImpingementPlateParticleGeometricStdDev = Read(section, nameof(ImpingementPlateParticleGeometricStdDev), DefaultImpingementPlateParticleGeometricStdDev, 1.0, 10.0),
                ImpingementPlateStokesNumberAt50Percent = Read(section, nameof(ImpingementPlateStokesNumberAt50Percent), DefaultImpingementPlateStokesNumberAt50Percent, 0.05, 5.0)
            };

            if (!(next.CycloneVelocityHardMinMs <= next.CycloneVelocityWarnMinMs
                  && next.CycloneVelocityWarnMinMs <= next.CycloneVelocityWarnMaxMs
                  && next.CycloneVelocityWarnMaxMs <= next.CycloneVelocityHardMaxMs))
                throw new InvalidOperationException(
                    $"{SectionName}: cyclone velocity limits must satisfy HardMin <= WarnMin <= WarnMax <= HardMax.");

            _current = next;
        }

        private static double Read(
            IConfigurationSection section,
            string key,
            double defaultValue,
            double min,
            double max)
        {
            double value = section.GetValue<double?>(key) ?? defaultValue;

            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw new InvalidOperationException(
                    $"{SectionName}:{key} must be between {min} and {max}; got {value}.");

            return value;
        }
    }
}
