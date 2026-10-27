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

            /// <summary>|y_out(2N) − y_out(N)| / y_in; NaN when not evaluated.</summary>
            public double LayerRefinementRelativeDifference { get; set; } = double.NaN;

            /// <summary>
            /// Absorption from integrating KGa·P·(y − y*) over the height,
            /// kmol/(m2·hr). Independent of G·(y_in − y_out). NaN when not evaluated.
            /// </summary>
            public double ProfileIntegratedAbsorbedFluxKmolM2Hr { get; set; } = double.NaN;

            /// <summary>|integrated − G·(y_in − y_out)| / (G·y_in); NaN when not evaluated.</summary>
            public double ProfileClosureRelativeError { get; set; } = double.NaN;

            /// <summary>
            /// |y_out(layer solver) − y_out(analytic Colburn)| / y_in for the isothermal,
            /// linear-equilibrium case; NaN when the case is not comparable.
            /// </summary>
            public double AnalyticCrossCheckRelativeDifference { get; set; } = double.NaN;

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

        /// <param name="convergenceTolerance">
        /// RELATIVE to the inlet gas mole fraction (outlet y must move by less than
        /// convergenceTolerance × y_in between iterations).
        /// </param>
        /// <param name="pinchTolerance">
        /// RELATIVE to the local gas mole fraction: a layer is "pinched" when
        /// |y − y*| &lt; pinchTolerance × y at that layer, i.e. the gas is at equilibrium
        /// with the liquid there. Small absolute driving forces at a clean outlet
        /// (y* ≈ 0) are not flagged.
        /// </param>
        /// <param name="temperatureToleranceK">Liquid temperature profile tolerance, K.</param>
        /// <param name="refinementTolerance">
        /// Maximum accepted layer-refinement difference (relative to y_in) before a warning.
        /// </param>
        /// <param name="crossCheckTolerance">
        /// Maximum accepted difference (relative to y_in) from the analytic Colburn
        /// solution in the isothermal linear-equilibrium case before a warning.
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
            int maxIterations = 100,
            double convergenceTolerance = 1e-4,
            double pinchTolerance = 1e-3,
            double? gasSideResistanceFraction = null,
            double temperatureToleranceK = 1e-3,
            double refinementTolerance = 1e-3,
            double crossCheckTolerance = 0.02)
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
                convergenceTolerance,
                temperatureToleranceK,
                true);

            var result = new EnhancedTowerSolverResult
            {
                Converged = baseResult.Converged,
                IterationsUsed = baseResult.IterationsUsed,
                OutletGasMoleFraction = baseResult.OutletGasMoleFraction,
                OutletLiquidMoleFraction = baseResult.OutletLiquidMoleFraction,
                OutletLiquidTemperatureK = baseResult.OutletLiquidTemperatureK,
                Layers = baseResult.Layers,
                LayerRefinementRelativeDifference = baseResult.LayerRefinementRelativeDifference,
                ProfileIntegratedAbsorbedFluxKmolM2Hr = baseResult.ProfileIntegratedAbsorbedFluxKmolM2Hr,
                ProfileClosureRelativeError = baseResult.ProfileClosureRelativeError,
                Warnings = warnings
            };

            if (!double.IsNaN(result.LayerRefinementRelativeDifference)
                && result.LayerRefinementRelativeDifference > refinementTolerance)
            {
                warnings.Add(
                    $"LAYER REFINEMENT: doubling the layer count changes the outlet by " +
                    $"{result.LayerRefinementRelativeDifference:E2} of y_in " +
                    $"(limit {refinementTolerance:E1}); increase the layer count.");
            }

            // Isothermal, linear-equilibrium reference: analytic Colburn solution.
            bool isothermalCase = !heatOfSolutionKJmol.HasValue || heatOfSolutionKJmol.Value == 0.0;
            if (isothermalCase)
            {
                result.AnalyticCrossCheckRelativeDifference =
                    ComputeAnalyticCrossCheck(
                        packingHeightM,
                        gasMolarFluxKmolM2Hr,
                        liquidMolarFluxKmolM2Hr,
                        inletGasMoleFraction,
                        inletLiquidMoleFraction,
                        inletLiquidTemperatureK,
                        totalPressureKPa,
                        localGasFilmCoeff,
                        localHenrysConstant,
                        baseResult.OutletGasMoleFraction,
                        baseResult.OutletLiquidMoleFraction);

                if (!double.IsNaN(result.AnalyticCrossCheckRelativeDifference)
                    && result.AnalyticCrossCheckRelativeDifference > crossCheckTolerance)
                {
                    warnings.Add(
                        $"CROSS-CHECK: layer solver differs from the analytic Colburn solution by " +
                        $"{result.AnalyticCrossCheckRelativeDifference:E2} of y_in " +
                        $"(limit {crossCheckTolerance:E1}).");
                }
            }

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

                // Relative to the local gas fraction: pinch means y ≈ y*, not
                // merely a small absolute driving force.
                double absTol = Math.Max(pinchTolerance * Math.Abs(y), 1e-15);

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
        /// Analytic Colburn outlet for an isothermal tower with constant H and KGa,
        /// compared with the layer solver. Returns |y_out(solver) − y_out(analytic)| / y_in,
        /// or NaN when the case is not comparable (H varies with composition by more
        /// than 1 %, or any input is unusable).
        /// </summary>
        private static double ComputeAnalyticCrossCheck(
            double packingHeightM,
            double gasMolarFluxKmolM2Hr,
            double liquidMolarFluxKmolM2Hr,
            double inletGasMoleFraction,
            double inletLiquidMoleFraction,
            double inletLiquidTemperatureK,
            double totalPressureKPa,
            Func<double, double> localGasFilmCoeff,
            Func<double, double, double> localHenrysConstant,
            double solverOutletGasMoleFraction,
            double solverOutletLiquidMoleFraction)
        {
            try
            {
                double h1 = localHenrysConstant(inletLiquidTemperatureK, inletLiquidMoleFraction);
                double h2 = localHenrysConstant(
                    inletLiquidTemperatureK,
                    Math.Max(solverOutletLiquidMoleFraction, inletLiquidMoleFraction));
                double kGa = localGasFilmCoeff(inletLiquidTemperatureK);

                if (double.IsNaN(h1) || double.IsInfinity(h1) || h1 <= 0.0) return double.NaN;
                if (double.IsNaN(h2) || double.IsInfinity(h2)) return double.NaN;
                if (Math.Abs(h2 - h1) / h1 > 0.01) return double.NaN;
                if (double.IsNaN(kGa) || double.IsInfinity(kGa) || kGa <= 0.0) return double.NaN;
                if (gasMolarFluxKmolM2Hr <= 0.0 || liquidMolarFluxKmolM2Hr <= 0.0) return double.NaN;

                double m = h1;
                double ntu = kGa * totalPressureKPa * packingHeightM / gasMolarFluxKmolM2Hr;
                double absorptionFactor = liquidMolarFluxKmolM2Hr / (m * gasMolarFluxKmolM2Hr);
                double y1Effective = inletGasMoleFraction - m * inletLiquidMoleFraction;

                if (y1Effective <= 0.0) return double.NaN;

                double y2Effective;
                if (Math.Abs(absorptionFactor - 1.0) <= 1e-6)
                {
                    y2Effective = y1Effective / (1.0 + ntu);
                }
                else
                {
                    double invA = 1.0 / absorptionFactor;
                    double a = 1.0 - invA;
                    y2Effective = y1Effective * a / (Math.Exp(ntu * a) - invA);
                }

                double yOutAnalytic = y2Effective + m * inletLiquidMoleFraction;

                if (double.IsNaN(yOutAnalytic) || double.IsInfinity(yOutAnalytic)) return double.NaN;

                return Math.Abs(solverOutletGasMoleFraction - yOutAnalytic)
                       / Math.Max(Math.Abs(inletGasMoleFraction), 1e-300);
            }
            catch (Exception)
            {
                return double.NaN;
            }
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