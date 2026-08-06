using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Conservation
{
    public sealed class LayerProfile
    {
        public double HeightM { get; set; }
        public double GasMoleFraction { get; set; }
        public double LiquidMoleFraction { get; set; }
        public double LiquidTemperatureK { get; set; }
    }

    public sealed class TowerSolverResult
    {
        public bool Converged { get; set; }
        public int IterationsUsed { get; set; }
        public double OutletGasMoleFraction { get; set; }   // actual, may differ from target
        public double OutletLiquidMoleFraction { get; set; }
        public double OutletLiquidTemperatureK { get; set; } // liquid leaving the BOTTOM (hottest)
        public IReadOnlyList<LayerProfile> Layers { get; set; } = Array.Empty<LayerProfile>();
    }

    /// <summary>
    /// Phase 3 discretized solver — the architectural jump from "one-shot
    /// formula" to "simulator" the roadmap calls out. Slices the tower
    /// into layers (z=0 at the gas inlet/bottom, z=H at the top) and
    /// marches an explicit-Euler mass balance up the gas phase, coupled
    /// to a liquid energy balance (HeatOfAbsorptionModel) that shifts the
    /// local temperature-dependent Henry's constant layer by layer —
    /// something the analytical Colburn NTU/HTU calc (constant H,
    /// isothermal) cannot capture.
    ///
    /// This is a RATING calculation, not a sizing one: packingHeightM is
    /// a candidate height (e.g. from the existing NTU*HTU estimate), and
    /// the solver reports what the tower actually achieves — useful as a
    /// check on whether the isothermal shortcut over- or under-promises.
    ///
    /// Two things are unknown before marching: the actual outlet gas
    /// mole fraction (kinetics-dependent) and the liquid temperature
    /// profile (depends on how much absorption happened, which depends
    /// on kinetics, which depends on temperature). Both are resolved by
    /// successive substitution: march with the previous iteration's
    /// temperature profile, rebuild the operating line and the new
    /// temperature profile from the result, repeat until the outlet gas
    /// fraction stops moving.
    ///
    /// Pure math — no DB/IO. Callers (ScrubberCalculationEngine) inject
    /// local kinetics via delegates so this class doesn't own EF lookups.
    /// </summary>
    public static class PackedTowerLayerSolver
    {
        public static TowerSolverResult Solve(
            double packingHeightM,
            int layerCount,
            double gasMolarFluxKmolM2Hr,     // G, per unit cross-section
            double liquidMolarFluxKmolM2Hr,  // L, per unit cross-section
            double liquidMassFluxKgM2Hr,
            double liquidSpecificHeatKJKgK,
            double inletGasMoleFraction,      // y_in, bottom
            double inletLiquidMoleFraction,   // x_in, top
            double outletGasMoleFractionTarget, // starting guess for the operating line
            double inletLiquidTemperatureK,   // liquid entering at the TOP
            double? heatOfSolutionKJmol,
            double totalPressureKPa,
            Func<double, double> localGasFilmCoeff, // temperatureK -> KGa, kmol/(m3·hr·kPa)
            Func<double, double, double> localHenrysConstant, // (temperatureK, liquidMoleFraction) -> H  (y* = H·x)
            int maxIterations = 25,
            double convergenceTolerance = 1e-4)
        {
            if (layerCount < 2) throw new ArgumentException("Need at least 2 layers.", nameof(layerCount));
            if (packingHeightM <= 0) throw new ArgumentException("Packing height must be positive.", nameof(packingHeightM));

            double dz = packingHeightM / layerCount;
            double yOutEstimate = outletGasMoleFractionTarget;

            // Isothermal on the first pass — no absorbed-heat history yet.
            var liquidTempByLayer = new double[layerCount + 1];
            for (int i = 0; i <= layerCount; i++) liquidTempByLayer[i] = inletLiquidTemperatureK;

            var layers = new List<LayerProfile>(layerCount + 1);
            bool converged = false;
            int iterationsUsed = 0;

            for (int iter = 1; iter <= maxIterations; iter++)
            {
                iterationsUsed = iter;
                layers.Clear();

                double y = inletGasMoleFraction;
                layers.Add(new LayerProfile
                {
                    HeightM = 0,
                    GasMoleFraction = y,
                    LiquidMoleFraction = OperatingLineX(y, yOutEstimate, inletLiquidMoleFraction, gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr),
                    LiquidTemperatureK = liquidTempByLayer[0]
                });

                // ── March the gas phase from bottom (z=0) to top (z=H) ──
                for (int i = 1; i <= layerCount; i++)
                {
                    double z = i * dz;
                    double tLocal = liquidTempByLayer[i - 1]; // previous iteration's profile
                    double x = OperatingLineX(y, yOutEstimate, inletLiquidMoleFraction,
                        gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr);

                    double hLocal = localHenrysConstant(tLocal, x); // real per-layer x, not a ppm proxy
                    double kGaLocal = localGasFilmCoeff(tLocal);
                    double yStar = hLocal * x;

                    // G·dy/dz = -KGa·P·(y - y*)  (explicit Euler step)
                    double dy = -(kGaLocal * totalPressureKPa * (y - yStar) / Math.Max(gasMolarFluxKmolM2Hr, 1e-9)) * dz;
                    y = Math.Max(y + dy, 0.0);

                    layers.Add(new LayerProfile
                    {
                        HeightM = z,
                        GasMoleFraction = y,
                        LiquidMoleFraction = OperatingLineX(y, yOutEstimate, inletLiquidMoleFraction, gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr),
                        LiquidTemperatureK = tLocal
                    });
                }

                double yOutComputed = y;

                // ── Rebuild liquid temperature profile from this pass's
                //    absorption profile (liquid enters at the top, so its
                //    cumulative pickup at height z is G·(y(z) - y_out)). ──
                var newLiquidTemp = new double[layerCount + 1];
                for (int i = 0; i <= layerCount; i++)
                {
                    double yAtLayer = layers[i].GasMoleFraction;
                    double molesAbsorbedKmolPerHr = gasMolarFluxKmolM2Hr * Math.Max(yAtLayer - yOutComputed, 0.0);
                    var thermal = HeatOfAbsorptionModel.TryCalculate(
                        molesAbsorbedKmolPerHr / 3600.0, heatOfSolutionKJmol,
                        liquidMassFluxKgM2Hr / 3600.0, liquidSpecificHeatKJKgK, inletLiquidTemperatureK);
                    newLiquidTemp[i] = thermal?.OutletLiquidTemperatureK ?? inletLiquidTemperatureK;
                }

                double maxTempShift = 0.0;
                for (int i = 0; i <= layerCount; i++)
                    maxTempShift = Math.Max(maxTempShift, Math.Abs(newLiquidTemp[i] - liquidTempByLayer[i]));

                double yOutShift = Math.Abs(yOutComputed - yOutEstimate);

                liquidTempByLayer = newLiquidTemp;
                yOutEstimate = yOutComputed;

                if (maxTempShift < convergenceTolerance && yOutShift < convergenceTolerance)
                {
                    converged = true;
                    break;
                }
            }

            return new TowerSolverResult
            {
                Converged = converged,
                IterationsUsed = iterationsUsed,
                OutletGasMoleFraction = yOutEstimate,
                OutletLiquidMoleFraction = layers[0].LiquidMoleFraction, // bottom = richest liquid
                OutletLiquidTemperatureK = liquidTempByLayer[0],
                Layers = layers
            };
        }
        public static TowerSolverResult SolveMultiComponent(
    double packingHeightM,
    int layerCount,
    double gasMolarFluxKmolM2Hr,
    double liquidMolarFluxKmolM2Hr,
    double liquidMassFluxKgM2Hr,
    double liquidSpecificHeatKJKgK,
    Composition inletGasComposition,
    Composition inletLiquidComposition,
    double inletLiquidTemperatureK,
    double? heatOfSolutionKJmol,
    double totalPressureKPa,
    Func<string, double, double> localGasFilmCoeffBySpecies,
    Func<string, double, double, double> localHenrysConstantBySpecies,
    int maxIterations = 25,
    double convergenceTolerance = 1e-4)
        {
            if (layerCount < 2) throw new ArgumentException("Need at least 2 layers.", nameof(layerCount));
            if (packingHeightM <= 0) throw new ArgumentException("Packing height must be positive.", nameof(packingHeightM));

            double dz = packingHeightM / layerCount;
            var yOutEstimate = new Dictionary<string, double>(inletGasComposition.MoleFractions);
            var liquidTempByLayer = new double[layerCount + 1];
            for (int i = 0; i <= layerCount; i++) liquidTempByLayer[i] = inletLiquidTemperatureK;

            var allLayers = new List<LayerProfile>();
            var yVecCurrent = new Dictionary<string, double>(inletGasComposition.MoleFractions);

            for (int iter = 0; iter < maxIterations; iter++)
            {
                var layers = new List<LayerProfile>();
                yVecCurrent = new Dictionary<string, double>(inletGasComposition.MoleFractions);
                var xVecCurrent = new Dictionary<string, double>(inletLiquidComposition.MoleFractions);

                for (int layerIdx = 0; layerIdx < layerCount; layerIdx++)
                {
                    double z = layerIdx * dz;
                    double tLocal = liquidTempByLayer[layerIdx];

                    var dyVec = new Dictionary<string, double>();
                    double totalAbsorbedMoles = 0;

                    foreach (var (code, y_i) in yVecCurrent)
                    {
                        if (code == "BULK_CARRIER" || code == "Air") continue;

                        double kGa_i = localGasFilmCoeffBySpecies(code, tLocal);
                        double H_i = localHenrysConstantBySpecies(code, tLocal, xVecCurrent.GetValueOrDefault(code, 0));
                        double x_i = xVecCurrent.GetValueOrDefault(code, 0);
                        double yStar_i = H_i * x_i;

                        double dy = -(kGa_i * totalPressureKPa * (y_i - yStar_i) / Math.Max(gasMolarFluxKmolM2Hr, 1e-9)) * dz;
                        dyVec[code] = dy;
                        totalAbsorbedMoles += Math.Max(-dy * gasMolarFluxKmolM2Hr, 0);
                    }

                    foreach (var code in yVecCurrent.Keys.ToList())
                    {
                        if (dyVec.ContainsKey(code))
                            yVecCurrent[code] = Math.Max(yVecCurrent[code] + dyVec[code], 0);
                    }

                    double normSum = yVecCurrent.Values.Sum();
                    if (normSum > 0)
                        foreach (var code in yVecCurrent.Keys.ToList())
                            yVecCurrent[code] /= normSum;

                    double dT = 0;
                    if (heatOfSolutionKJmol.HasValue && totalAbsorbedMoles > 0)
                        dT = (totalAbsorbedMoles * heatOfSolutionKJmol.Value) / Math.Max(liquidMassFluxKgM2Hr, 1e-9) / Math.Max(liquidSpecificHeatKJKgK, 1e-9);

                    tLocal = Math.Min(tLocal + Math.Abs(dT), 373.15);
                    liquidTempByLayer[layerIdx + 1] = tLocal;

                    layers.Add(new LayerProfile
                    {
                        HeightM = z,
                        GasMoleFraction = yVecCurrent.GetValueOrDefault("POLLUTANT", yVecCurrent.Values.FirstOrDefault()),
                        LiquidTemperatureK = tLocal
                    });
                }

                double maxYShift = 0;
                foreach (var (code, y_new) in yVecCurrent)
                {
                    yOutEstimate.TryGetValue(code, out var y_old);
                    maxYShift = Math.Max(maxYShift, Math.Abs(y_new - y_old));
                }

                yOutEstimate = new Dictionary<string, double>(yVecCurrent);
                allLayers = layers;

                if (maxYShift < convergenceTolerance) break;
            }

            return new TowerSolverResult
            {
                Converged = true,
                IterationsUsed = maxIterations,
                OutletGasMoleFraction = yVecCurrent.GetValueOrDefault("POLLUTANT", 0),
                OutletLiquidTemperatureK = liquidTempByLayer[layerCount],
                Layers = allLayers
            };
        }
        /// <summary>Linear operating line from an overall mass balance
        /// (constant G, L — the same dilute-system assumption the rest of
        /// this codebase already makes): x(z) = x_in + (G/L)*(y(z) - y_out).</summary>
        private static double OperatingLineX(
            double y, double yOut, double xIn, double gasMolarFlux, double liquidMolarFlux)
            => xIn + (gasMolarFlux / Math.Max(liquidMolarFlux, 1e-9)) * (y - yOut);
    }
}