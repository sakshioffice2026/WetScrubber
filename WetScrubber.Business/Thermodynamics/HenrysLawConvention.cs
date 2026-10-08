using System;
using System.Collections.Generic;
using WetScrubber.Business.Exceptions;

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
    /// No silent clamping: out-of-range or non-finite values throw.
    /// Salting-out is applied only when a caller-supplied, verified Setchenow
    /// coefficient is provided.
    /// </summary>
    public sealed class EnhancedHenrysLaw
    {
        private const double GasConstant = 8.314462; // J/(mol*K)
        private const double ReferenceTempK = 298.15;
        private const double MinimumIonicStrengthMolPerL = 0.001;

        public class HenrysConstantResult
        {
            /// <summary>Temperature- and salt-corrected H, in the requested convention.</summary>
            public double Value { get; set; }

            public HenrysLawConvention Convention { get; set; }

            public bool SaltingOutApplied { get; set; }

            /// <summary>
            /// True if an ionic strength above the threshold was supplied but no
            /// verified Setchenow coefficient was available, so no salting-out
            /// correction was applied.
            /// </summary>
            public bool SaltingOutRequestedButUnavailable { get; set; }

            public double? IonicStrengthMolPerL { get; set; }

            /// <summary>Setchenow coefficient (L/mol, natural-log basis) actually used, if any.</summary>
            public double? SaltingOutCoefficientLPerMol { get; set; }

            /// <summary>Multiplier on the volatility-form H (y/x or Cg/Cl) from the Setchenow effect.</summary>
            public double SaltingOutFactor { get; set; } = 1.0;

            /// <summary>Retained for API compatibility; values are never clamped, so this is always false.</summary>
            public bool WasClamped { get; set; }

            public List<string> Warnings { get; } = new List<string>();
        }

        /// <summary>
        /// Temperature-corrected Henry's constant.
        ///   C = -deltaH_soln / R   (K; positive for exothermic dissolution)
        ///   LiquidReferenced: H(T) = H25 * exp( -C * (1/T - 1/Tref) )
        ///   GasReferenced:    H(T) = H25 * exp( +C * (1/T - 1/Tref) )
        /// fallbackTempCoeffK has the same meaning as C.
        /// Salting-out (Setchenow, ln(H_volatility,salt / H_volatility) = k * I) is applied
        /// only when saltingOutCoefficientLPerMol is supplied by the caller.
        /// </summary>
        public static HenrysConstantResult GetCorrectedConstant(
            double referenceHenrysConstantAt25C,
            double? heatOfSolutionKJmol,
            double temperatureC,
            double fallbackTempCoeffK,
            HenrysLawConvention convention,
            double? ionicStrengthMolPerL = null,
            string pollutantCode = "",
            double? saltingOutCoefficientLPerMol = null)
        {
            if (convention == HenrysLawConvention.Undefined)
                throw new ArgumentException(
                    "Henry's law convention MUST be explicitly declared (LiquidReferenced or GasReferenced).",
                    nameof(convention));

            if (double.IsNaN(referenceHenrysConstantAt25C)
                || double.IsInfinity(referenceHenrysConstantAt25C)
                || referenceHenrysConstantAt25C <= 0)
                throw new ArgumentException(
                    $"Reference Henry's constant must be positive and finite; got {referenceHenrysConstantAt25C}",
                    nameof(referenceHenrysConstantAt25C));

            if (double.IsNaN(temperatureC) || double.IsInfinity(temperatureC))
                throw new ArgumentOutOfRangeException(nameof(temperatureC), "Temperature must be finite.");

            double temperatureK = temperatureC + 273.15;
            if (temperatureK <= 0)
                throw new ArgumentOutOfRangeException(nameof(temperatureC), "Temperature must be above absolute zero.");

            if (heatOfSolutionKJmol.HasValue
                && (double.IsNaN(heatOfSolutionKJmol.Value) || double.IsInfinity(heatOfSolutionKJmol.Value)))
                throw new ArgumentException("Heat of solution must be finite.", nameof(heatOfSolutionKJmol));

            if (!heatOfSolutionKJmol.HasValue
                && (double.IsNaN(fallbackTempCoeffK) || double.IsInfinity(fallbackTempCoeffK)))
                throw new ArgumentException("Fallback temperature coefficient must be finite.", nameof(fallbackTempCoeffK));

            var result = new HenrysConstantResult { Convention = convention };

            double tempCoeffK = heatOfSolutionKJmol.HasValue
                ? -(heatOfSolutionKJmol.Value * 1000.0) / GasConstant
                : fallbackTempCoeffK;

            double sign = convention == HenrysLawConvention.LiquidReferenced ? -1.0 : 1.0;
            double correctedH = referenceHenrysConstantAt25C
                * Math.Exp(sign * tempCoeffK * (1.0 / temperatureK - 1.0 / ReferenceTempK));

            if (ionicStrengthMolPerL.HasValue)
            {
                double ionic = ionicStrengthMolPerL.Value;
                if (double.IsNaN(ionic) || double.IsInfinity(ionic) || ionic < 0)
                    throw new ArgumentOutOfRangeException(
                        nameof(ionicStrengthMolPerL), "Ionic strength must be finite and non-negative.");

                if (ionic > MinimumIonicStrengthMolPerL)
                {
                    if (saltingOutCoefficientLPerMol.HasValue)
                    {
                        double k = saltingOutCoefficientLPerMol.Value;
                        if (double.IsNaN(k) || double.IsInfinity(k))
                            throw new ArgumentException(
                                "Setchenow coefficient must be finite.", nameof(saltingOutCoefficientLPerMol));

                        double factor = Math.Exp(k * ionic);
                        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0)
                            throw new PropertyOutOfBoundsException(
                                "SaltingOutFactor", factor, double.Epsilon, double.MaxValue);

                        correctedH *= convention == HenrysLawConvention.LiquidReferenced ? factor : 1.0 / factor;

                        result.SaltingOutApplied = true;
                        result.IonicStrengthMolPerL = ionic;
                        result.SaltingOutCoefficientLPerMol = k;
                        result.SaltingOutFactor = factor;
                    }
                    else
                    {
                        result.SaltingOutRequestedButUnavailable = true;
                        result.IonicStrengthMolPerL = ionic;
                        result.Warnings.Add(
                            "Salting-out NOT applied"
                            + (string.IsNullOrWhiteSpace(pollutantCode) ? "" : $" for {pollutantCode}")
                            + ": no verified Setchenow coefficient supplied.");
                    }
                }
            }

            if (double.IsNaN(correctedH) || double.IsInfinity(correctedH))
                throw new PropertyOutOfBoundsException(
                    "CorrectedHenrysConstant", correctedH,
                    HenrysConstantUnits.MinimumHcc, HenrysConstantUnits.MaximumHcc);

            if (correctedH < HenrysConstantUnits.MinimumHcc || correctedH > HenrysConstantUnits.MaximumHcc)
                throw new PropertyOutOfBoundsException(
                    "CorrectedHenrysConstant", correctedH,
                    HenrysConstantUnits.MinimumHcc, HenrysConstantUnits.MaximumHcc);

            result.Value = correctedH;
            return result;
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

            if (double.IsNaN(henryValue) || double.IsInfinity(henryValue) || henryValue <= 0)
                throw new ArgumentException("Henry value must be positive and finite", nameof(henryValue));

            return 1.0 / henryValue;
        }
    }
}