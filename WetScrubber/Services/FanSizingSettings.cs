using System;
using Microsoft.Extensions.Configuration;

namespace WetScrubber.Services
{
    /// <summary>
    /// Fan sizing design basis, read from the "FanSizing" configuration section.
    ///   AuxiliaryPressureDropPa : ducting, inlet/outlet, demister and distributor
    ///                             losses added to the packing pressure drop (Pa).
    ///   Efficiency              : overall fan + motor efficiency (fraction).
    /// Defaults apply when the section or a key is absent.
    /// </summary>
    public static class FanSizingSettings
    {
        public const string SectionName = "FanSizing";

        public const double DefaultAuxiliaryPressureDropPa = 500.0;
        public const double DefaultEfficiency = 0.65;

        public const double MinAuxiliaryPressureDropPa = 0.0;
        public const double MaxAuxiliaryPressureDropPa = 5000.0;
        public const double MinEfficiency = 0.30;
        public const double MaxEfficiency = 0.95;

        public static double AuxiliaryPressureDropPa { get; private set; } = DefaultAuxiliaryPressureDropPa;

        public static double Efficiency { get; private set; } = DefaultEfficiency;

        public static void Load(IConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var section = configuration.GetSection(SectionName);

            double aux = section.GetValue<double?>(nameof(AuxiliaryPressureDropPa))
                         ?? DefaultAuxiliaryPressureDropPa;

            double eff = section.GetValue<double?>(nameof(Efficiency))
                         ?? DefaultEfficiency;

            if (double.IsNaN(aux) || aux < MinAuxiliaryPressureDropPa || aux > MaxAuxiliaryPressureDropPa)
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(AuxiliaryPressureDropPa)} must be between "
                    + $"{MinAuxiliaryPressureDropPa} and {MaxAuxiliaryPressureDropPa} Pa; got {aux}.");

            if (double.IsNaN(eff) || eff < MinEfficiency || eff > MaxEfficiency)
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(Efficiency)} must be between "
                    + $"{MinEfficiency} and {MaxEfficiency}; got {eff}.");

            AuxiliaryPressureDropPa = aux;
            Efficiency = eff;
        }
    }
}
