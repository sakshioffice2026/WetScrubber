using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Default implementation of IHenrysLawCalculator. Same math as
    /// ScrubberCalculationEngine.GetHenrysLawConstant /
    /// GetVanTHoffTempCoeff, extracted here so it has exactly one
    /// implementation instead of four copies.
    /// </summary>
    public sealed class HenrysLawCalculator : IHenrysLawCalculator
    {
        private const double GasConstant = 8.314; // J/(mol*K)
        private const double ReferenceTempK = 298.15;

        public double GetTemperatureCorrectedHenrysConstant(
            double referenceHenrysConstantAt25C,
            double? heatOfSolutionKJmol,
            double temperatureC,
            double fallbackTempCoeffK)
        {
            double h25 = referenceHenrysConstantAt25C <= 0 ? 0.83 : referenceHenrysConstantAt25C;
            double tempCoeff = heatOfSolutionKJmol.HasValue
                ? -(heatOfSolutionKJmol.Value * 1000.0) / GasConstant
                : fallbackTempCoeffK;

            double temperatureK = temperatureC + 273.15;
            double correctedH = h25 * Math.Exp(tempCoeff * (1.0 / temperatureK - 1.0 / ReferenceTempK));

            return Math.Max(correctedH, 0.001);
        }
    }
}