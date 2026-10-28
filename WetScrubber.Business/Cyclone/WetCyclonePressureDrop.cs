using System;

namespace WetScrubber.Business.Cyclone
{
    public sealed class WetCyclonePressureDropResult
    {
        public double ConfigurationFactorK { get; set; }
        public double VelocityHeadPa { get; set; }
        public double DryPressureDropPa { get; set; }
        public double WetCorrectionFactor { get; set; }
        public double PressureDropPa { get; set; }
        public double FanPowerW { get; set; }
    }

    /// <summary>
    /// Wet cyclone gas-side pressure drop and fan power.
    /// Dry drop: Shepherd and Lapple (1939)  dP = K * rho_g * vi^2 / 2,  K = 16 * a * b / De^2
    ///   (K = 8 for Lapple ratios; a = inlet height, b = inlet width, De = gas outlet diameter).
    /// Wet correction: multiplicative factor on the dry drop. Default 1.0 (no correction).
    ///   No wet-wall or spray correlation is encoded here; set a factor only from a confirmed source.
    /// Fan power: P = Q * dP / eta_fan.
    /// Status: Shepherd-Lapple form is standard; wet correction is unverified. Confirm against
    ///   Cooper and Alley and the EPA Air Pollution Control Cost Manual before release.
    /// </summary>
    public static class WetCyclonePressureDrop
    {
        public const double ShepherdLappleCoefficient = 16.0;
        public const double DefaultWetCorrectionFactor = 1.0;
        public const double DefaultFanEfficiency = 0.65;

        public static double ConfigurationFactorK(WetCycloneGeometry geometry)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));

            double de = geometry.GasOutletDiameterM;
            return ShepherdLappleCoefficient * geometry.InletHeightM * geometry.InletWidthM / (de * de);
        }

        /// <summary>
        /// Gas-side pressure drop (Pa) and fan power (W) for the given geometry and total gas flow.
        /// </summary>
        public static WetCyclonePressureDropResult Calculate(
            WetCycloneGeometry geometry,
            double gasFlowM3s,
            double gasDensityKgM3,
            double wetCorrectionFactor = DefaultWetCorrectionFactor,
            double fanEfficiency = DefaultFanEfficiency)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            RequirePositive(nameof(gasFlowM3s), gasFlowM3s);
            RequirePositive(nameof(gasDensityKgM3), gasDensityKgM3);
            RequirePositive(nameof(wetCorrectionFactor), wetCorrectionFactor);
            if (!(fanEfficiency > 0 && fanEfficiency <= 1))
                throw new ArgumentOutOfRangeException(nameof(fanEfficiency), "Fan efficiency must be within (0, 1].");

            double vi = geometry.InletVelocityMs(gasFlowM3s);
            double k = ConfigurationFactorK(geometry);
            double velocityHead = 0.5 * gasDensityKgM3 * vi * vi;
            double dryDrop = k * velocityHead;
            double drop = dryDrop * wetCorrectionFactor;
            double fanPower = gasFlowM3s * drop / fanEfficiency;

            return new WetCyclonePressureDropResult
            {
                ConfigurationFactorK = k,
                VelocityHeadPa = velocityHead,
                DryPressureDropPa = dryDrop,
                WetCorrectionFactor = wetCorrectionFactor,
                PressureDropPa = drop,
                FanPowerW = fanPower
            };
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Value must be a positive finite number.");
        }
    }
}
