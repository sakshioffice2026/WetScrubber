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
        public const int DefaultCycloneSizeBinCount = 20;

        public static double PackedPumpHeadOffsetM { get; private set; } = DefaultPackedPumpHeadOffsetM;
        public static double VenturiPumpHeadM { get; private set; } = DefaultVenturiPumpHeadM;
        public static double SprayTowerPumpHeadM { get; private set; } = DefaultSprayTowerPumpHeadM;
        public static double FreeboardFactor { get; private set; } = DefaultFreeboardFactor;
        public static double SumpAndTopAllowanceM { get; private set; } = DefaultSumpAndTopAllowanceM;
        public static double SprayTowerAuxiliaryPressureDropPa { get; private set; } = DefaultSprayTowerAuxiliaryPressureDropPa;
        public static double DefaultVoidFraction { get; private set; } = DefaultDefaultVoidFraction;
        public static double DefaultLiquidSurfaceTensionNM { get; private set; } = DefaultDefaultLiquidSurfaceTensionNM;
        public static double SprayTowerDesignVelocityMs { get; private set; } = DefaultSprayTowerDesignVelocityMs;
        public static double SprayTowerRemovalCoefficient { get; private set; } = DefaultSprayTowerRemovalCoefficient;
        public static double SprayTowerHeightPerM3S { get; private set; } = DefaultSprayTowerHeightPerM3S;
        public static double SprayTowerBaseHeightM { get; private set; } = DefaultSprayTowerBaseHeightM;
        public static double VenturiParticleGeometricStdDev { get; private set; } = DefaultVenturiParticleGeometricStdDev;
        public static double VenturiDiameterFactor { get; private set; } = DefaultVenturiDiameterFactor;
        public static double VenturiHeightFactor { get; private set; } = DefaultVenturiHeightFactor;
        public static double CyclonePumpEfficiency { get; private set; } = DefaultCyclonePumpEfficiency;
        public static double CycloneFanEfficiency { get; private set; } = DefaultCycloneFanEfficiency;
        public static double CycloneWetCorrectionFactor { get; private set; } = DefaultCycloneWetCorrectionFactor;
        public static double CycloneVelocityWarnMinMs { get; private set; } = DefaultCycloneVelocityWarnMinMs;
        public static double CycloneVelocityWarnMaxMs { get; private set; } = DefaultCycloneVelocityWarnMaxMs;
        public static double CycloneVelocityHardMinMs { get; private set; } = DefaultCycloneVelocityHardMinMs;
        public static double CycloneVelocityHardMaxMs { get; private set; } = DefaultCycloneVelocityHardMaxMs;
        public static double CyclonePressureDropCeilingPa { get; private set; } = DefaultCyclonePressureDropCeilingPa;
        public static int CycloneSizeBinCount { get; private set; } = DefaultCycloneSizeBinCount;

        public static void Load(IConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var section = configuration.GetSection(SectionName);

            PackedPumpHeadOffsetM = Read(section, nameof(PackedPumpHeadOffsetM), DefaultPackedPumpHeadOffsetM, 0.0, 50.0);
            VenturiPumpHeadM = Read(section, nameof(VenturiPumpHeadM), DefaultVenturiPumpHeadM, 0.0, 100.0);
            SprayTowerPumpHeadM = Read(section, nameof(SprayTowerPumpHeadM), DefaultSprayTowerPumpHeadM, 0.0, 100.0);
            FreeboardFactor = Read(section, nameof(FreeboardFactor), DefaultFreeboardFactor, 0.0, 1.0);
            SumpAndTopAllowanceM = Read(section, nameof(SumpAndTopAllowanceM), DefaultSumpAndTopAllowanceM, 0.0, 10.0);
            SprayTowerAuxiliaryPressureDropPa = Read(section, nameof(SprayTowerAuxiliaryPressureDropPa), DefaultSprayTowerAuxiliaryPressureDropPa, 0.0, 5000.0);
            DefaultVoidFraction = Read(section, nameof(DefaultVoidFraction), DefaultDefaultVoidFraction, 0.30, 0.99);
            DefaultLiquidSurfaceTensionNM = Read(section, nameof(DefaultLiquidSurfaceTensionNM), DefaultDefaultLiquidSurfaceTensionNM, 0.010, 0.100);
            SprayTowerDesignVelocityMs = Read(section, nameof(SprayTowerDesignVelocityMs), DefaultSprayTowerDesignVelocityMs, 0.2, 3.0);
            SprayTowerRemovalCoefficient = Read(section, nameof(SprayTowerRemovalCoefficient), DefaultSprayTowerRemovalCoefficient, 0.01, 10.0);
            SprayTowerHeightPerM3S = Read(section, nameof(SprayTowerHeightPerM3S), DefaultSprayTowerHeightPerM3S, 0.0, 50.0);
            SprayTowerBaseHeightM = Read(section, nameof(SprayTowerBaseHeightM), DefaultSprayTowerBaseHeightM, 0.0, 20.0);
            VenturiParticleGeometricStdDev = Read(section, nameof(VenturiParticleGeometricStdDev), DefaultVenturiParticleGeometricStdDev, 1.0, 10.0);
            VenturiDiameterFactor = Read(section, nameof(VenturiDiameterFactor), DefaultVenturiDiameterFactor, 1.0, 10.0);
            VenturiHeightFactor = Read(section, nameof(VenturiHeightFactor), DefaultVenturiHeightFactor, 1.0, 30.0);

            CyclonePumpEfficiency = Read(section, nameof(CyclonePumpEfficiency), DefaultCyclonePumpEfficiency, 0.10, 1.0);
            CycloneFanEfficiency = Read(section, nameof(CycloneFanEfficiency), DefaultCycloneFanEfficiency, 0.10, 1.0);
            CycloneWetCorrectionFactor = Read(section, nameof(CycloneWetCorrectionFactor), DefaultCycloneWetCorrectionFactor, 0.2, 5.0);
            CycloneVelocityHardMinMs = Read(section, nameof(CycloneVelocityHardMinMs), DefaultCycloneVelocityHardMinMs, 1.0, 50.0);
            CycloneVelocityWarnMinMs = Read(section, nameof(CycloneVelocityWarnMinMs), DefaultCycloneVelocityWarnMinMs, 1.0, 50.0);
            CycloneVelocityWarnMaxMs = Read(section, nameof(CycloneVelocityWarnMaxMs), DefaultCycloneVelocityWarnMaxMs, 1.0, 60.0);
            CycloneVelocityHardMaxMs = Read(section, nameof(CycloneVelocityHardMaxMs), DefaultCycloneVelocityHardMaxMs, 1.0, 60.0);
            CyclonePressureDropCeilingPa = Read(section, nameof(CyclonePressureDropCeilingPa), DefaultCyclonePressureDropCeilingPa, 100.0, 20000.0);

            int bins = section.GetValue<int?>(nameof(CycloneSizeBinCount)) ?? DefaultCycloneSizeBinCount;
            if (bins < 5 || bins > 200)
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(CycloneSizeBinCount)} must be between 5 and 200; got {bins}.");
            CycloneSizeBinCount = bins;

            if (!(CycloneVelocityHardMinMs <= CycloneVelocityWarnMinMs
                  && CycloneVelocityWarnMinMs <= CycloneVelocityWarnMaxMs
                  && CycloneVelocityWarnMaxMs <= CycloneVelocityHardMaxMs))
                throw new InvalidOperationException(
                    $"{SectionName}: cyclone velocity limits must satisfy HardMin <= WarnMin <= WarnMax <= HardMax.");
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