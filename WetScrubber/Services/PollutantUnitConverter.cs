using System;
using WetScrubber.Models;

namespace WetScrubber.Services
{
    public static class PollutantUnitConverter
    {
        // Molar volume of an ideal gas at normal conditions (0 degC, 101.325 kPa), L/mol.
        private const double NormalMolarVolumeLPerMol = 22.414;

        public const double ActualFlowTolerance = 0.05;

        public static double MgPerNm3ToPpmv(double mgPerNm3, double molecularWeightGMol)
        {
            if (molecularWeightGMol <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(molecularWeightGMol),
                    "Molecular weight must be positive.");

            return mgPerNm3 * NormalMolarVolumeLPerMol / molecularWeightGMol;
        }

        /// <summary>
        /// Converts pollutant inlet and target-outlet concentrations in place from
        /// mg/Nm3 (as entered on the form) to ppmv, the basis the calculation engine
        /// expects. Call once on a view model built for an engine run; do not call on
        /// a view model that is later shown on a form or saved.
        /// </summary>
        public static void ApplyEngineBasis(CreateDesignViewModel vm)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));

            foreach (var p in vm.Pollutants)
            {
                p.InletConcentration = MgPerNm3ToPpmv(p.InletConcentration, p.MolecularWeight);

                if (p.TargetOutletConcentration > 0)
                    p.TargetOutletConcentration =
                        MgPerNm3ToPpmv(p.TargetOutletConcentration, p.MolecularWeight);
            }
        }

        public static double ExpectedActualFlowM3Hr(
            double normalFlowNm3Hr,
            double inletTemperatureC,
            double inletPressurePa)
        {
            double pressurePa = inletPressurePa > 0 ? inletPressurePa : 101325.0;
            return normalFlowNm3Hr
                   * (inletTemperatureC + 273.15) / 273.15
                   * 101325.0 / pressurePa;
        }

        /// <summary>
        /// Returns an error message when the entered actual flow deviates from the
        /// value implied by normal flow, temperature and pressure by more than the
        /// tolerance; otherwise null.
        /// </summary>
        public static string? CheckActualFlow(CreateDesignViewModel model)
        {
            if (model.NormalFlowRate <= 0 || model.ActualFlowRate <= 0)
                return null;

            double expected = ExpectedActualFlowM3Hr(
                model.NormalFlowRate,
                model.InletTemperature,
                model.InletPressure);

            double deviation = Math.Abs(model.ActualFlowRate - expected) / expected;

            if (deviation <= ActualFlowTolerance)
                return null;

            return $"Actual flow {model.ActualFlowRate:N0} m³/hr is inconsistent with "
                 + $"{model.NormalFlowRate:N0} Nm³/hr at {model.InletTemperature:0.#} °C "
                 + $"and {model.InletPressure:N0} Pa (expected about {expected:N0} m³/hr).";
        }
    }
}
