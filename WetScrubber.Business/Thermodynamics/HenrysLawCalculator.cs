using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Van 't Hoff temperature correction for Henry's constants expressed in
    /// VOLATILITY form (Cg/Cl or y/x), i.e. constants that INCREASE with
    /// temperature for exothermic dissolution (deltaH_soln &lt; 0).
    ///
    ///   C = -deltaH_soln / R                      [K, positive for exothermic]
    ///   H(T) = H_ref * exp( -C * (1/T - 1/T_ref) )
    ///
    /// fallbackTempCoeffK uses the same meaning as C above (positive = exothermic).
    /// </summary>
    public sealed class HenrysLawCalculator : IHenrysLawCalculator
    {
        private const double GasConstant = 8.314462;   // J/(mol*K)
        private const double ReferenceTempK = 298.15;
        private const double AbsoluteMinimum = 1.0e-12;
        private const double AbsoluteMaximum = 1.0e12;

        public double GetTemperatureCorrectedHenrysConstant(
            double referenceHenrysConstantAt25C,
            double? heatOfSolutionKJmol,
            double temperatureC,
            double fallbackTempCoeffK)
        {
            if (double.IsNaN(referenceHenrysConstantAt25C) || double.IsInfinity(referenceHenrysConstantAt25C) || referenceHenrysConstantAt25C <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(referenceHenrysConstantAt25C),
                    referenceHenrysConstantAt25C,
                    "Reference Henry's constant at 25 C must be positive and finite.");

            double h25 = referenceHenrysConstantAt25C;

            double tempCoeffK = heatOfSolutionKJmol.HasValue
                ? -(heatOfSolutionKJmol.Value * 1000.0) / GasConstant
                : fallbackTempCoeffK;

            double temperatureK = temperatureC + 273.15;
            if (temperatureK <= 0.0 || double.IsNaN(temperatureK) || double.IsInfinity(temperatureK))
                throw new ArgumentOutOfRangeException(nameof(temperatureC), "Temperature must be above absolute zero.");

            double correctedH = h25 * Math.Exp(-tempCoeffK * (1.0 / temperatureK - 1.0 / ReferenceTempK));

            if (double.IsNaN(correctedH) || double.IsInfinity(correctedH)
    || correctedH < AbsoluteMinimum || correctedH > AbsoluteMaximum)
                throw new WetScrubber.Business.Exceptions.PropertyOutOfBoundsException(
                    "CorrectedHenrysConstant", correctedH, AbsoluteMinimum, AbsoluteMaximum);

            return correctedH;
        }
    }
}