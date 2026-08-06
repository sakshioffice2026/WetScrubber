using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Unified thermodynamics service for the flowsheet (Phase B).
    /// Replaces hardcoded density (1.2 kg/m³), crude Henry's law temp correction,
    /// and disconnected NRTL — now integrated into mass transfer solver.
    /// </summary>
    public class ThermoCalculationService
    {
        private readonly IEquationOfState _eos;
        private readonly IHenrysLawCalculator _henrysLaw;
        private readonly IActivityCoefficientModel _activityModel;

        public ThermoCalculationService(
            IEquationOfState eos,
            IHenrysLawCalculator henrysLaw,
            IActivityCoefficientModel activityModel)
        {
            _eos = eos ?? throw new ArgumentNullException(nameof(eos));
            _henrysLaw = henrysLaw ?? throw new ArgumentNullException(nameof(henrysLaw));
            _activityModel = activityModel ?? throw new ArgumentNullException(nameof(activityModel));
        }

        /// <summary>
        /// Calculate gas density using Peng-Robinson EOS.
        /// Replaces hardcoded 1.2 kg/m³.
        /// </summary>
        public double CalculateGasDensityKgM3(
            IReadOnlyList<(string code, double moleFraction)> gasComposition,
            double temperatureC,
            double pressureKPa)
        {
            if (gasComposition == null || gasComposition.Count == 0)
                throw new ArgumentException("Gas composition required.", nameof(gasComposition));

            double temperatureK = temperatureC + 273.15;

            // Map gas codes to EOS component inputs (N2, O2, SO2, etc.)
            var eosInputs = BuildEosComponentInputs(gasComposition, temperatureK, pressureKPa);

            var eosResult = _eos.Evaluate(eosInputs, temperatureK, pressureKPa);
            return eosResult.DensityKgM3;
        }

        /// <summary>
        /// Get temperature-corrected Henry's constant using stored heat of solution or fallback.
        /// Replaces crude `1 + 0.01*(T-25)` factor.
        /// </summary>
        public double GetCorrectedHenrysConstant(
            string pollutantCode,
            double referenceH25C,
            double? heatOfSolutionKJmol,
            double temperatureC)
        {
            // Fallback temp coeff if heat of solution not available (e.g., SO2: -1700 K)
            var fallbackCoeff = GetFallbackTempCoefficient(pollutantCode);
            return _henrysLaw.GetTemperatureCorrectedHenrysConstant(
                referenceH25C, heatOfSolutionKJmol, temperatureC, fallbackCoeff);
        }

        /// <summary>
        /// Calculate liquid-phase activity coefficients for pollutant-water binary.
        /// Enables non-ideal liquid treatment in equilibrium calcs.
        /// </summary>
        public (double gammaPollutant, double gammaWater) GetActivityCoefficients(
            string pollutantCode,
            double liquidMoleFractionPollutant,
            double temperatureK)
        {
            // Lookup NRTL params for (pollutant, water) — would come from DB in production
            var nrtlParams = GetNrtlBinaryParameters(pollutantCode, temperatureK);

            var input = _activityModel.Evaluate(
                new NrtlComponentInput { Code = pollutantCode, MoleFraction = liquidMoleFractionPollutant },
                new NrtlComponentInput { Code = "H2O", MoleFraction = 1.0 - liquidMoleFractionPollutant },
                nrtlParams);

            return (input.GammaA, input.GammaB);
        }

        /// <summary>
        /// Build EOS component inputs from gas composition codes.
        /// Maps SO2 → real properties, N2 → real properties, etc.
        /// </summary>
        private List<EosComponentInput> BuildEosComponentInputs(
            IReadOnlyList<(string code, double moleFraction)> gasComposition,
            double temperatureK,
            double pressureKPa)
        {
            var result = new List<EosComponentInput>();

            foreach (var (code, moleFrac) in gasComposition)
            {
                var props = GetCriticalPropertiesAndMW(code);
                result.Add(new EosComponentInput
                {
                    Code = code,
                    MoleFraction = moleFrac,
                    CriticalTemperatureK = props.Tc,
                    CriticalPressureKPa = props.Pc,
                    AcentricFactor = props.Omega,
                    MolecularWeight = props.MW
                });
            }

            return result;
        }

        /// <summary>
        /// Critical properties and molecular weight for common scrubber gases.
        /// </summary>
        private static (double Tc, double Pc, double Omega, double MW) GetCriticalPropertiesAndMW(string code)
        {
            return code.ToUpperInvariant() switch
            {
                "N2" => (126.2, 3394, 0.040, 28.01),       // Nitrogen
                "O2" => (154.6, 5043, 0.022, 32.00),       // Oxygen
                "SO2" => (430.8, 7884, 0.252, 64.07),      // Sulfur dioxide
                "NO" => (180.0, 6486, 0.583, 30.01),       // Nitric oxide
                "NO2" => (369.8, 10100, 0.289, 46.01),     // Nitrogen dioxide
                "H2O" => (647.1, 22064, 0.345, 18.02),     // Water (vapor)
                "CO2" => (304.1, 7377, 0.239, 44.01),      // Carbon dioxide
                "HCl" => (324.6, 8291, 0.132, 36.46),      // Hydrogen chloride
                "NH3" => (369.8, 11333, 0.253, 17.03),     // Ammonia
                _ => throw new ArgumentException($"Unknown gas code: {code}")
            };
        }

        /// <summary>
        /// Fallback van't Hoff temperature coefficient (J/mol) for common pollutants.
        /// Used if heat of solution not in DB.
        /// </summary>
        private static double GetFallbackTempCoefficient(string code)
        {
            return code.ToUpperInvariant() switch
            {
                "SO2" => -1700 * 8.314,         // SO2: exothermic dissolution
                "NO2" => -2200 * 8.314,         // NO2
                "HCl" => -1600 * 8.314,         // HCl
                "NH3" => -1200 * 8.314,         // NH3
                _ => -1500 * 8.314               // Default fallback
            };
        }

        /// <summary>
        /// NRTL binary interaction parameters for (pollutant, water).
        /// In production, would be loaded from NrtlBinaryParameter table.
        /// </summary>
        private static NrtlBinaryInput GetNrtlBinaryParameters(string pollutantCode, double temperatureK)
        {
            // Placeholder — real implementation queries DB or config
            // tauAB, tauBA, alpha vary by pollutant; shown here are order-of-magnitude guesses
            return pollutantCode.ToUpperInvariant() switch
            {
                "SO2" => new NrtlBinaryInput { Tau_AB = 0.95, Tau_BA = -1.05, Alpha = 0.20 },
                "NO2" => new NrtlBinaryInput { Tau_AB = 1.10, Tau_BA = -1.15, Alpha = 0.20 },
                "HCl" => new NrtlBinaryInput { Tau_AB = 0.85, Tau_BA = -0.95, Alpha = 0.20 },
                "NH3" => new NrtlBinaryInput { Tau_AB = 1.25, Tau_BA = -0.75, Alpha = 0.20 },
                _ => new NrtlBinaryInput { Tau_AB = 1.0, Tau_BA = -1.0, Alpha = 0.20 }
            };
        }
    }

}