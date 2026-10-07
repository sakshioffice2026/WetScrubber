using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Conservation
{
    /// <summary>
    /// Enhanced tower solver with pinch-point detection, driving-force checks
    /// and diagnostics on top of PackedTowerLayerSolver.
    /// Equilibrium convention: y* = H * x  (H in mole-fraction ratio form).
    /// </summary>
    public static class EnhancedPackedTowerSolver
    {
        public sealed class EnhancedTowerSolverResult
        {
            public bool Converged { get; set; }
            public int IterationsUsed { get; set; }
            public double OutletGasMoleFraction { get; set; }
            public double OutletLiquidMoleFraction { get; set; }
            public double OutletLiquidTemperatureK { get; set; }
            public IReadOnlyList<LayerProfile> Layers { get; set; }

            public bool PinchPointDetected { get; set; }
            public double? PinchHeightM { get; set; }
            public string PinchDiagnosis { get; set; }

            public bool NegativeDrivingForceDetected { get; set; }
            public double? NegativeDrivingForceHeightM { get; set; }

            public double MinimumDrivingForce { get; set; } = double.PositiveInfinity;
            public double AverageDrivingForce { get; set; }
            public double FractionInPinchZone { get; set; }

            /// <summary>NaN when no resistance data was supplied.</summary>
            public double GasSideResistanceFraction { get; set; } = double.NaN;
            public double LiquidSideResistanceFraction { get; set; } = double.NaN;
            public string ControllingResistance { get; set; }

            public string ConvergenceMessage { get; set; }
            public bool IsPhysicallyFeasible { get; set; }
            public IReadOnlyList<string> Warnings { get; set; }
        }

        /// <param name="pinchTolerance">
        /// RELATIVE tolerance: a layer is "pinched" when |y - y*| &lt; pinchTolerance * y_inlet.
        /// </param>
        /// <param name="gasSideResistanceFraction">
        /// Optional gas-side share of total transfer resistance (0..1), from the film-coefficient provider.
        /// </param>
        public static EnhancedTowerSolverResult SolveWithDiagnostics(
            double packingHeightM,
            int layerCount,
            double gasMolarFluxKmolM2Hr,
            double liquidMolarFluxKmolM2Hr,
            double liquidMassFluxKgM2Hr,
            double liquidSpecificHeatKJKgK,
            double inletGasMoleFraction,
            double inletLiquidMoleFraction,
            double outletGasMoleFractionTarget,
            double inletLiquidTemperatureK,
            double? heatOfSolutionKJmol,
            double totalPressureKPa,
            Func<double, double> localGasFilmCoeff,
            Func<double, double, double> localHenrysConstant,
            int maxIterations = 25,
            double convergenceTolerance = 1e-4,
            double pinchTolerance = 1e-4,
            double? gasSideResistanceFraction = null)
        {
            var warnings = new List<string>();

            var baseResult = PackedTowerLayerSolver.Solve(
                packingHeightM,
                layerCount,
                gasMolarFluxKmolM2Hr,
                liquidMolarFluxKmolM2Hr,
                liquidMassFluxKgM2Hr,
                liquidSpecificHeatKJKgK,
                inletGasMoleFraction,
                inletLiquidMoleFraction,
                outletGasMoleFractionTarget,
                inletLiquidTemperatureK,
                heatOfSolutionKJmol,
                totalPressureKPa,
                localGasFilmCoeff,
                localHenrysConstant,
                maxIterations,
                convergenceTolerance);

            var result = new EnhancedTowerSolverResult
            {
                Converged = baseResult.Converged,
                IterationsUsed = baseResult.IterationsUsed,
                OutletGasMoleFraction = baseResult.OutletGasMoleFraction,
                OutletLiquidMoleFraction = baseResult.OutletLiquidMoleFraction,
                OutletLiquidTemperatureK = baseResult.OutletLiquidTemperatureK,
                Layers = baseResult.Layers,
                Warnings = warnings
            };

            double absTol = Math.Max(pinchTolerance * Math.Abs(inletGasMoleFraction), 1e-15);

            double totalDrivingForce = 0.0;
            int pinchCount = 0;
            double minDrivingForce = double.PositiveInfinity;

            foreach (var layer in baseResult.Layers)
            {
                double x = layer.LiquidMoleFraction;
                double y = layer.GasMoleFraction;
                double tLocal = layer.LiquidTemperatureK;

                double yStar = localHenrysConstant(tLocal, x) * x;
                double drivingForce = y - yStar;

                if (drivingForce < minDrivingForce)
                    minDrivingForce = drivingForce;

                totalDrivingForce += Math.Max(drivingForce, 0.0);

                if (Math.Abs(drivingForce) < absTol)
                {
                    pinchCount++;
                    if (!result.PinchPointDetected)
                    {
                        result.PinchPointDetected = true;
                        result.PinchHeightM = layer.HeightM;
                    }
                }

                if (drivingForce < -absTol)
                {
                    result.NegativeDrivingForceDetected = true;
                    if (result.NegativeDrivingForceHeightM == null)
                        result.NegativeDrivingForceHeightM = layer.HeightM;
                }
            }

            int layerTotal = Math.Max(baseResult.Layers.Count, 1);
            result.MinimumDrivingForce = minDrivingForce;
            result.AverageDrivingForce = totalDrivingForce / layerTotal;
            result.FractionInPinchZone = (double)pinchCount / layerTotal;

            if (result.PinchPointDetected)
            {
                result.PinchDiagnosis = $"Pinch point detected at height {result.PinchHeightM:F2} m. " +
                    "The gas approaches equilibrium with the liquid. " +
                    "Consider: (1) more packing height, (2) lower target removal, " +
                    "(3) higher L/G, or (4) a different or reactive solvent.";
                warnings.Add($"PINCH CONDITION: {result.PinchDiagnosis}");
            }

            if (result.NegativeDrivingForceDetected)
            {
                warnings.Add($"NEGATIVE DRIVING FORCE at height {result.NegativeDrivingForceHeightM:F2} m; " +
                    "liquid is supersaturated and absorption reverses. Check liquid flow and inlet composition.");
            }

            if (result.FractionInPinchZone > 0.3)
            {
                warnings.Add($"WARNING: {result.FractionInPinchZone * 100:F1}% of the tower is in the pinch zone (y ~ y*).");
            }

            if (gasSideResistanceFraction.HasValue)
            {
                double g = Math.Min(Math.Max(gasSideResistanceFraction.Value, 0.0), 1.0);
                result.GasSideResistanceFraction = g;
                result.LiquidSideResistanceFraction = 1.0 - g;
                result.ControllingResistance = g > 0.6 ? "Gas-side" : g < 0.4 ? "Liquid-side" : "Balanced";
            }
            else
            {
                result.ControllingResistance = "Not evaluated";
            }

            if (result.Converged)
            {
                result.ConvergenceMessage = $"Converged in {result.IterationsUsed} iterations";
            }
            else
            {
                result.ConvergenceMessage = $"Did not converge after {maxIterations} iterations; results may be inaccurate.";
                warnings.Add("CONVERGENCE FAILURE: solver did not reach tolerance. Results uncertain.");
            }

            result.IsPhysicallyFeasible =
                !result.NegativeDrivingForceDetected
                && result.Converged
                && !result.PinchPointDetected;

            if (!result.IsPhysicallyFeasible)
                warnings.Add("DESIGN NOT FEASIBLE: see specific issues above.");

            return result;
        }

        /// <summary>
        /// Minimum L/G for a linear equilibrium y* = H*x with a straight operating line:
        ///   (L/G)min = (y_in - y_out) / (x*_out - x_in),   x*_out = y_in / H.
        /// Only the ratio L/G matters, so fluxes or flows may be supplied.
        /// </summary>
        public static (bool Feasible, string Message) QuickPinchCheck(
            double gasMolarFluxKmolM2Hr,
            double liquidMolarFluxKmolM2Hr,
            double inletGasMoleFraction,
            double outletGasMoleFractionTarget,
            double inletLiquidMoleFraction,
            double henrysConstantAtOperatingConditions,
            double totalPressureKPa)
        {
            if (gasMolarFluxKmolM2Hr <= 0 || liquidMolarFluxKmolM2Hr <= 0)
                return (false, "Invalid gas or liquid flux.");

            if (henrysConstantAtOperatingConditions <= 0)
                return (false, "Henry's constant must be positive.");

            if (outletGasMoleFractionTarget >= inletGasMoleFraction)
                return (true, "Target outlet is not below inlet; no absorption required.");

            double loverG = liquidMolarFluxKmolM2Hr / gasMolarFluxKmolM2Hr;

            double xStarAtInletGas = inletGasMoleFraction / henrysConstantAtOperatingConditions;
            double capacity = xStarAtInletGas - inletLiquidMoleFraction;

            if (capacity <= 1e-15)
                return (false, "Inlet liquid is already at or above equilibrium with the inlet gas; no absorption possible.");

            double minLoverG = (inletGasMoleFraction - outletGasMoleFractionTarget) / capacity;

            if (loverG < minLoverG * 1.02)
            {
                return (false,
                    $"L/G ({loverG:F3}) is at or below the theoretical minimum ({minLoverG:F3}) with 2% margin. " +
                    "Pinch point will occur; target removal unachievable.");
            }

            return (true, $"L/G ({loverG:F3}) exceeds the minimum ({minLoverG:F3}) with margin.");
        }
    }
}