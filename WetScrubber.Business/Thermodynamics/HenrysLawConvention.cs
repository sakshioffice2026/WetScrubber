using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Henry's law can be expressed in two conventions; confusing them gives errors of 1000x or worse.
    ///
    /// LIQUID_REFERENCED:  y* = H * x   (H dimensionless, mole-fraction ratio; volatility form,
    ///                                   INCREASES with temperature for exothermic dissolution)
    /// GAS_REFERENCED:     x* = H * y   (H dimensionless; solubility form,
    ///                                   DECREASES with temperature for exothermic dissolution)
    ///
    /// LiquidReferenced H = 1 / GasReferenced H  (exact, same pressure and temperature).
    /// </summary>
    public enum HenrysLawConvention
    {
        Undefined = 0,
        /// <summary>y* = H * x</summary>
        LiquidReferenced = 1,
        /// <summary>x* = H * y</summary>
        GasReferenced = 2
    }

    /// <summary>
    /// Henry's constant with convention, salting-out and validation.
    /// </summary>
    public sealed class EnhancedHenrysLaw
    {
        private const double GasConstant = 8.314462; // J/(mol*K)
        private const double ReferenceTempK = 298.15;

        public class HenrysConstantResult
        {
            /// <summary>Temperature- and salt-corrected H, in the requested convention.</summary>
            public double Value { get; set; }

            public HenrysLawConvention Convention { get; set; }

            public bool SaltingOutApplied { get; set; }

            public double? IonicStrengthMolPerL { get; set; }

            /// <summary>Multiplier on the volatility-form H (y/x or Cg/Cl) from the Setchenow effect.</summary>
            public double SaltingOutFactor { get; set; } = 1.0;

            /// <summary>True if H was clamped to avoid unphysical values.</summary>
            public bool WasClamped { get; set; }
        }

        /// <summary>
        /// Temperature-corrected Henry's constant.
        ///   C = -deltaH_soln / R   (K; positive for exothermic dissolution)
        ///   LiquidReferenced: H(T) = H25 * exp( -C * (1/T - 1/Tref) )
        ///   GasReferenced:    H(T) = H25 * exp( +C * (1/T - 1/Tref) )
        /// fallbackTempCoeffK has the same meaning as C.
        /// </summary>
        public static HenrysConstantResult GetCorrectedConstant(
            double referenceHenrysConstantAt25C,
            double? heatOfSolutionKJmol,
            double temperatureC,
            double fallbackTempCoeffK,
            HenrysLawConvention convention,
            double? ionicStrengthMolPerL = null,
            string pollutantCode = "")
        {
            if (convention == HenrysLawConvention.Undefined)
                throw new ArgumentException(
                    "Henry's law convention MUST be explicitly declared (LiquidReferenced or GasReferenced).",
                    nameof(convention));

            if (referenceHenrysConstantAt25C <= 0)
                throw new ArgumentException(
                    $"Reference Henry's constant must be positive; got {referenceHenrysConstantAt25C}",
                    nameof(referenceHenrysConstantAt25C));

            double temperatureK = temperatureC + 273.15;
            if (temperatureK <= 0)
                throw new ArgumentOutOfRangeException(nameof(temperatureC), "Temperature must be above absolute zero.");

            var result = new HenrysConstantResult { Convention = convention };

            double tempCoeffK = heatOfSolutionKJmol.HasValue
                ? -(heatOfSolutionKJmol.Value * 1000.0) / GasConstant
                : fallbackTempCoeffK;

            double sign = convention == HenrysLawConvention.LiquidReferenced ? -1.0 : 1.0;
            double correctedH = referenceHenrysConstantAt25C
                * Math.Exp(sign * tempCoeffK * (1.0 / temperatureK - 1.0 / ReferenceTempK));

            // Salting-out (Setchenow): ln(H_volatility,salt / H_volatility) = k * I
            if (ionicStrengthMolPerL.HasValue && ionicStrengthMolPerL.Value > 0.001)
            {
                double k = GetSaltingOutCoefficient(pollutantCode);
                double factor = Math.Exp(k * ionicStrengthMolPerL.Value);

                correctedH *= convention == HenrysLawConvention.LiquidReferenced ? factor : 1.0 / factor;

                result.SaltingOutApplied = true;
                result.IonicStrengthMolPerL = ionicStrengthMolPerL.Value;
                result.SaltingOutFactor = factor;
            }

            if (double.IsNaN(correctedH) || double.IsInfinity(correctedH))
            {
                result.WasClamped = true;
                correctedH = HenrysConstantUnits.MinimumHcc;
            }
            else if (correctedH < HenrysConstantUnits.MinimumHcc)
            {
                result.WasClamped = true;
                correctedH = HenrysConstantUnits.MinimumHcc;
            }
            else if (correctedH > HenrysConstantUnits.MaximumHcc)
            {
                result.WasClamped = true;
                correctedH = HenrysConstantUnits.MaximumHcc;
            }

            result.Value = correctedH;
            return result;
        }

        /// <summary>
        /// Setchenow coefficient k (L/mol) for ln(H/H0) = k * I.
        /// Placeholder values, not yet verified against a primary source.
        /// </summary>
        private static double GetSaltingOutCoefficient(string pollutantCode)
        {
            return (pollutantCode?.ToUpperInvariant()) switch
            {
                "SO2" => 0.15,
                "CO2" => 0.13,
                "H2S" => 0.08,
                "HCL" => 0.20,
                "NH3" => -0.05,
                _ => 0.10
            };
        }

        /// <summary>
        /// Convert between conventions (dimensionless mole-fraction forms): H_G = 1 / H_L.
        /// Temperature and pressure arguments are kept for API compatibility.
        /// </summary>
        public static double ConvertConvention(
            double henryValue,
            HenrysLawConvention fromConvention,
            HenrysLawConvention toConvention,
            double temperatureC,
            double pressureKPa)
        {
            if (fromConvention == HenrysLawConvention.Undefined
                || toConvention == HenrysLawConvention.Undefined)
                throw new ArgumentException("Both conventions must be defined");

            if (fromConvention == toConvention)
                return henryValue;

            if (henryValue <= 0)
                throw new ArgumentException("Henry value must be positive", nameof(henryValue));

            return 1.0 / henryValue;
        }
    }
}