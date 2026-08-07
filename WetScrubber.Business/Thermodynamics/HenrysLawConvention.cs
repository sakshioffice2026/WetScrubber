using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// CRITICAL: Henry's law can be expressed in two conventions:
    /// 
    /// 1. LIQUID_REFERENCED (y = H * x)  — H in atm (or Pa)
    ///    "Liquid-side convention": equilibrium partial pressure = H * x
    ///    Common in gas absorption literature (Perry, McCabe)
    /// 
    /// 2. GAS_REFERENCED (x = H * y)     — H in mol/mol per mol/mol or Pa⁻¹
    ///    "Gas-side convention": equilibrium liquid mole fraction = H * y
    ///    Common in vapor-liquid equilibrium (thermodynamic) literature
    /// 
    /// Confusing these produces errors of 1000× or worse. This enum forces
    /// explicit declaration at every point.
    /// </summary>
    public enum HenrysLawConvention
    {
        Undefined = 0,
        /// <summary>y* = H * x  (equilibrium gas mole fraction from liquid)</summary>
        LiquidReferenced = 1,
        /// <summary>x* = H * y  (equilibrium liquid mole fraction from gas)</summary>
        GasReferenced = 2
    }

    /// <summary>
    /// Enhanced Henry's constant with convention, salting-out effects,
    /// and validation. Replaces the bare double-based HenrysLawCalculator.
    /// </summary>
    public sealed class EnhancedHenrysLaw
    {
        private const double GasConstant = 8.314; // J/(mol*K)
        private const double ReferenceTempK = 298.15;

        public class HenrysConstantResult
        {
            /// <summary>Temperature-corrected H value (in whatever units the reference had)</summary>
            public double Value { get; set; }

            /// <summary>Which convention was used</summary>
            public HenrysLawConvention Convention { get; set; }

            /// <summary>Was salting-out effect applied?</summary>
            public bool SaltingOutApplied { get; set; }

            /// <summary>Ionic strength used for salting-out, if any</summary>
            public double? IonicStrengthMolPerL { get; set; }

            /// <summary>Magnitude of salting-out correction (multiplier on H)</summary>
            public double SaltingOutFactor { get; set; } = 1.0;

            /// <summary>True if H was clamped to avoid unphysical values</summary>
            public bool WasClamped { get; set; }
        }

        /// <summary>
        /// Get temperature-corrected Henry's constant with full validation.
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

            var result = new HenrysConstantResult { Convention = convention };

            // Van't Hoff temperature correction
            double tempCoeff = heatOfSolutionKJmol.HasValue
                ? -(heatOfSolutionKJmol.Value * 1000.0) / GasConstant
                : fallbackTempCoeffK;

            double temperatureK = temperatureC + 273.15;
            double correctedH = referenceHenrysConstantAt25C
                * Math.Exp(tempCoeff * (1.0 / temperatureK - 1.0 / ReferenceTempK));

            // Salting-out effect (Setchenow): ln(H/H0) = kH * I
            // where kH is pollutant-specific, I is ionic strength
            double saltingOutFactor = 1.0;
            if (ionicStrengthMolPerL.HasValue && ionicStrengthMolPerL.Value > 0.001)
            {
                // kH varies by pollutant; this is a typical range for common gases
                // For SO2: ~0.15 mol/L, CO2: ~0.13, H2S: ~0.08 (literature values)
                double kH = GetSaltingOutCoefficient(pollutantCode);
                double lnFactor = kH * ionicStrengthMolPerL.Value;
                saltingOutFactor = Math.Exp(lnFactor);
                result.SaltingOutApplied = true;
                result.IonicStrengthMolPerL = ionicStrengthMolPerL.Value;
                result.SaltingOutFactor = saltingOutFactor;
            }

            correctedH *= saltingOutFactor;

            // Sanity checks
            if (correctedH < 0.001)
            {
                result.WasClamped = true;
                correctedH = 0.001;
            }
            if (correctedH > 1e10)
            {
                result.WasClamped = true;
                correctedH = 1e10;
            }

            result.Value = correctedH;
            return result;
        }

        /// <summary>
        /// Salting-out coefficient kH by pollutant (Setchenow equation).
        /// Returns kH for ln(H/H0) = kH * I
        /// </summary>
        private static double GetSaltingOutCoefficient(string pollutantCode)
        {
            return (pollutantCode?.ToUpperInvariant()) switch
            {
                "SO2" => 0.15,
                "CO2" => 0.13,
                "H2S" => 0.08,
                "HCL" => 0.20,  // acidic, salts out strongly
                "NH3" => -0.05, // salts IN (negative kH)
                _ => 0.10       // default moderate value
            };
        }

        /// <summary>
        /// Convert between Henry's law conventions.
        /// LiquidReferenced (y = H_L * x) to GasReferenced (x = H_G * y)
        /// where H_G = RT / H_L (at given T, P)
        /// </summary>
        public static double ConvertConvention(
            double henryValue,
            HenrysLawConvention fromConvention,
            HenrysLawConvention toConvention,
            double temperatureC,
            double pressureKPa)
        {
            if (fromConvention == toConvention)
                return henryValue;

            if (fromConvention == HenrysLawConvention.Undefined
                || toConvention == HenrysLawConvention.Undefined)
                throw new ArgumentException("Both conventions must be defined");

            double tempK = temperatureC + 273.15;
            double rt = GasConstant * tempK / 1000.0; // kPa·m³/mol = kPa·L/kmol ÷ 1000

            if (fromConvention == HenrysLawConvention.LiquidReferenced
                && toConvention == HenrysLawConvention.GasReferenced)
            {
                // H_L in kPa → H_G = RT / H_L (dimensionless)
                return (rt * pressureKPa) / henryValue;
            }
            else
            {
                // H_G (dimensionless) → H_L = RT / H_G (kPa)
                return (rt * pressureKPa) / henryValue;
            }
        }
    }
}