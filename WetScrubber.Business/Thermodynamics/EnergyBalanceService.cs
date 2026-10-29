using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Enthalpy reference: liquid water at 0 C for every stream (gas, vapor, liquid).
    /// heatReleasedKW / reactionHeatKW = heat GENERATED inside the unit; it stays in the
    /// streams. heatLossKW = heat leaving to surroundings.
    ///   H_in + Q_generated = H_out + Q_loss
    /// gasOutletMassFlowKgS EXCLUDES evaporatedWaterKgS (added to the gas side here).
    /// </summary>
    public class EnergyBalanceService
    {
        private const double LiquidSpecificHeatKJKgK = 4.18;
        private const double LatentHeatAt0CKJKg = 2501.0;
        private const double SteamSpecificHeatKJKgK = 1.87;
        private const double ClosureFloorKW = 0.1;
        private const double ClosureRelativeTolerance = 0.01;
        private const double MassClosureRelativeTolerance = 1.0e-3;

        private static double GetGasSpecificHeatKJKgK(string gasMixture) =>
            (gasMixture ?? "flue").ToLowerInvariant() switch
            {
                "flue" => 1.05,
                "air" => 1.005,
                "steam" => 1.87,
                _ => 1.05
            };

        public double CalculateGasSpecificEnthalpyKJKg(double temperatureC, string gasMixture = "flue")
            => GetGasSpecificHeatKJKgK(gasMixture) * temperatureC;

        public double CalculateLiquidSpecificEnthalpyKJKg(double temperatureC)
            => LiquidSpecificHeatKJKgK * temperatureC;

        public double CalculateVaporEnthalpyKJKg(double temperatureC)
            => LatentHeatAt0CKJKg + SteamSpecificHeatKJKgK * temperatureC;

        private static void RequireFinite(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite.");
        }

        public EnergyBalanceResult ValidateEnergyBalance(
            double gasInletMassFlowKgS,
            double gasInletTemperatureC,
            double liquidInletMassFlowKgS,
            double liquidInletTemperatureC,
            double gasOutletMassFlowKgS,
            double gasOutletTemperatureC,
            double liquidOutletMassFlowKgS,
            double liquidOutletTemperatureC,
            double heatReleasedKW,
            string gasMixture = "flue",
            double evaporatedWaterKgS = 0.0,
            double reactionHeatKW = 0.0,
            double heatLossKW = 0.0)
        {
            RequireFinite(nameof(gasInletMassFlowKgS), gasInletMassFlowKgS);
            RequireFinite(nameof(gasInletTemperatureC), gasInletTemperatureC);
            RequireFinite(nameof(liquidInletMassFlowKgS), liquidInletMassFlowKgS);
            RequireFinite(nameof(liquidInletTemperatureC), liquidInletTemperatureC);
            RequireFinite(nameof(gasOutletMassFlowKgS), gasOutletMassFlowKgS);
            RequireFinite(nameof(gasOutletTemperatureC), gasOutletTemperatureC);
            RequireFinite(nameof(liquidOutletMassFlowKgS), liquidOutletMassFlowKgS);
            RequireFinite(nameof(liquidOutletTemperatureC), liquidOutletTemperatureC);
            RequireFinite(nameof(heatReleasedKW), heatReleasedKW);
            RequireFinite(nameof(evaporatedWaterKgS), evaporatedWaterKgS);
            RequireFinite(nameof(reactionHeatKW), reactionHeatKW);
            RequireFinite(nameof(heatLossKW), heatLossKW);

            double enthalpyInKW =
                gasInletMassFlowKgS * CalculateGasSpecificEnthalpyKJKg(gasInletTemperatureC, gasMixture)
                + liquidInletMassFlowKgS * CalculateLiquidSpecificEnthalpyKJKg(liquidInletTemperatureC);

            double enthalpyOutKW =
                gasOutletMassFlowKgS * CalculateGasSpecificEnthalpyKJKg(gasOutletTemperatureC, gasMixture)
                + liquidOutletMassFlowKgS * CalculateLiquidSpecificEnthalpyKJKg(liquidOutletTemperatureC)
                + evaporatedWaterKgS * CalculateVaporEnthalpyKJKg(gasOutletTemperatureC);

            double generatedKW = heatReleasedKW + reactionHeatKW;
            double enthalpyChangeKW = enthalpyOutKW - enthalpyInKW;
            double residualKW = enthalpyChangeKW + heatLossKW - generatedKW;

            double scaleKW = Math.Max(
                Math.Max(Math.Abs(generatedKW), Math.Abs(enthalpyChangeKW)), 1.0e-9);

            double massResidualKgS = gasInletMassFlowKgS + liquidInletMassFlowKgS
                - gasOutletMassFlowKgS - evaporatedWaterKgS - liquidOutletMassFlowKgS;
            double massScaleKgS = Math.Max(gasInletMassFlowKgS + liquidInletMassFlowKgS, 1.0e-12);

            return new EnergyBalanceResult
            {
                EnthalpyInletKW = enthalpyInKW,
                EnthalpyOutletKW = enthalpyOutKW,
                EnthalpyChangeKW = enthalpyChangeKW,
                HeatReleasedKW = generatedKW,
                ReactionHeatKW = reactionHeatKW,
                HeatLossKW = heatLossKW,
                ResidualKW = residualKW,
                ResidualPercent = 100.0 * Math.Abs(residualKW) / scaleKW,
                IsClosed = Math.Abs(residualKW) <= Math.Max(ClosureFloorKW, ClosureRelativeTolerance * scaleKW),
                MassResidualKgS = massResidualKgS,
                IsMassClosed = Math.Abs(massResidualKgS) <= MassClosureRelativeTolerance * massScaleKgS
            };
        }

        public EnergyBalanceResult ValidateFlowsheetPorts(
            Flowsheet.FlowsheetPorts inletPorts,
            Flowsheet.FlowsheetPorts outletPorts,
            double heatReleasedKW,
            double carrierGasMolecularWeightGMol,
            double absorbedMassKgS = 0.0,
            double evaporatedWaterKgS = 0.0,
            double reactionHeatKW = 0.0,
            double heatLossKW = 0.0)
        {
            if (inletPorts == null) throw new ArgumentNullException(nameof(inletPorts));
            if (outletPorts == null) throw new ArgumentNullException(nameof(outletPorts));
            if (inletPorts.Liquid == null) throw new ArgumentException("Inlet liquid port is required.", nameof(inletPorts));
            if (outletPorts.Liquid == null) throw new ArgumentException("Outlet liquid port is required.", nameof(outletPorts));

            if (double.IsNaN(carrierGasMolecularWeightGMol) ||
                double.IsInfinity(carrierGasMolecularWeightGMol) ||
                carrierGasMolecularWeightGMol <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(carrierGasMolecularWeightGMol),
                    carrierGasMolecularWeightGMol,
                    "Carrier gas molecular weight must be positive and finite.");

            double inletTempK = inletPorts.Gas.TemperatureC + 273.15;
            double inletPressurePa = inletPorts.Gas.PressurePa > 0 ? inletPorts.Gas.PressurePa : 101325.0;
            double gasDensityKgM3 = inletPressurePa * (carrierGasMolecularWeightGMol / 1000.0) / (8.314462 * inletTempK);
            double gasInKgS = inletPorts.Gas.ActualFlowM3Hr / 3600.0 * gasDensityKgM3;
            double gasOutKgS = gasInKgS - absorbedMassKgS;

            return ValidateEnergyBalance(
                gasInKgS, inletPorts.Gas.TemperatureC,
                inletPorts.Liquid.MassFlowKgS, inletPorts.Liquid.TemperatureC,
                gasOutKgS, outletPorts.Gas.TemperatureC,
                outletPorts.Liquid.MassFlowKgS, outletPorts.Liquid.TemperatureC,
                heatReleasedKW, "flue", evaporatedWaterKgS, reactionHeatKW, heatLossKW);
        }
    }

    public class EnergyBalanceResult
    {
        public double EnthalpyInletKW { get; set; }
        public double EnthalpyOutletKW { get; set; }
        public double EnthalpyChangeKW { get; set; }

        /// <summary>Total heat generated inside the unit (absorption + reaction), kW.</summary>
        public double HeatReleasedKW { get; set; }
        public double ReactionHeatKW { get; set; }
        public double HeatLossKW { get; set; }

        /// <summary>(H_out - H_in) + Q_loss - Q_generated; ~0 when closed.</summary>
        public double ResidualKW { get; set; }

        /// <summary>|residual| as % of max(|Q_generated|, |dH|).</summary>
        public double ResidualPercent { get; set; }

        /// <summary>|residual| &lt;= max(0.1 kW, 1 % of the larger of |Q_generated|, |dH|).</summary>
        public bool IsClosed { get; set; }

        public double MassResidualKgS { get; set; }
        public bool IsMassClosed { get; set; }
    }
}