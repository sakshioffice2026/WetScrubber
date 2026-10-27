using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.Conservation
{
    public sealed class LayerProfile
    {
        public double HeightM { get; set; }
        public double GasMoleFraction { get; set; }
        public double LiquidMoleFraction { get; set; }
        public double LiquidTemperatureK { get; set; }

        /// <summary>Per-species gas mole fractions (multi-component solver only).</summary>
        public IReadOnlyDictionary<string, double> GasMoleFractions { get; set; }
            = new Dictionary<string, double>();

        /// <summary>Per-species liquid mole fractions (multi-component solver only).</summary>
        public IReadOnlyDictionary<string, double> LiquidMoleFractions { get; set; }
            = new Dictionary<string, double>();
    }

    public sealed class TowerSolverResult
    {
        public bool Converged { get; set; }
        public int IterationsUsed { get; set; }
        public double OutletGasMoleFraction { get; set; }   // actual, may differ from target
        public double OutletLiquidMoleFraction { get; set; }
        public double OutletLiquidTemperatureK { get; set; } // liquid leaving the BOTTOM (hottest)
        public IReadOnlyList<LayerProfile> Layers { get; set; } = Array.Empty<LayerProfile>();

        /// <summary>Gas leaving the top, every absorbing species (multi-component solver only).</summary>
        public IReadOnlyDictionary<string, double> OutletGasMoleFractions { get; set; }
            = new Dictionary<string, double>();

        /// <summary>Liquid leaving the bottom, every absorbing species (multi-component solver only).</summary>
        public IReadOnlyDictionary<string, double> OutletLiquidMoleFractions { get; set; }
            = new Dictionary<string, double>();
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

                // ── Rebuild liquid temperature profile layer by layer.
                //    Liquid enters at the top (index layerCount) at the inlet
                //    temperature and flows down; each layer's heat pickup is
                //    G·(y(z_i) - y(z_i+1)) and is added to the temperature
                //    leaving the layer above it, not to the fixed inlet T. ──
                var newLiquidTemp = new double[layerCount + 1];
                newLiquidTemp[layerCount] = inletLiquidTemperatureK;
                for (int i = layerCount - 1; i >= 0; i--)
                {
                    double yBelow = layers[i].GasMoleFraction;
                    double yAbove = layers[i + 1].GasMoleFraction;
                    double layerAbsorbedKmolPerHr = gasMolarFluxKmolM2Hr * Math.Max(yBelow - yAbove, 0.0);
                    var thermal = HeatOfAbsorptionModel.TryCalculate(
                        layerAbsorbedKmolPerHr / 3600.0, heatOfSolutionKJmol,
                        liquidMassFluxKgM2Hr / 3600.0, liquidSpecificHeatKJKgK, newLiquidTemp[i + 1]);
                    newLiquidTemp[i] = thermal?.OutletLiquidTemperatureK ?? newLiquidTemp[i + 1];
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

        /// <summary>
        /// Coupled multi-species solver. z = 0 is the gas inlet (bottom), z = H
        /// the top. The liquid enters at the top at <paramref name="inletLiquidTemperatureK"/>
        /// and flows down. Each absorbing species has its own liquid operating line
        /// x_i(z) = x_i,in + (G/L)·(y_i(z) − y_i,out); gas carriers
        /// ("BULK_CARRIER", "Air") are not transferred. All species are reported
        /// per layer and at the outlet.
        /// </summary>
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
            if (inletGasComposition == null) throw new ArgumentNullException(nameof(inletGasComposition));
            if (inletLiquidComposition == null) throw new ArgumentNullException(nameof(inletLiquidComposition));
            if (localGasFilmCoeffBySpecies == null) throw new ArgumentNullException(nameof(localGasFilmCoeffBySpecies));
            if (localHenrysConstantBySpecies == null) throw new ArgumentNullException(nameof(localHenrysConstantBySpecies));
            if (!(gasMolarFluxKmolM2Hr > 0)) throw new ArgumentException("Gas molar flux must be positive.", nameof(gasMolarFluxKmolM2Hr));
            if (!(liquidMolarFluxKmolM2Hr > 0)) throw new ArgumentException("Liquid molar flux must be positive.", nameof(liquidMolarFluxKmolM2Hr));
            if (!(totalPressureKPa > 0)) throw new ArgumentException("Total pressure must be positive.", nameof(totalPressureKPa));

            var species = inletGasComposition.MoleFractions.Keys
                .Where(c => !IsCarrier(c))
                .ToList();

            if (species.Count == 0)
                throw new ArgumentException("Inlet gas composition has no absorbing species.", nameof(inletGasComposition));

            string primary = species.Contains("POLLUTANT") ? "POLLUTANT" : species[0];

            double dz = packingHeightM / layerCount;
            double gOverL = gasMolarFluxKmolM2Hr / liquidMolarFluxKmolM2Hr;

            var yIn = species.ToDictionary(c => c, c => inletGasComposition.MoleFractionOf(c));
            var xIn = species.ToDictionary(c => c, c => inletLiquidComposition.MoleFractionOf(c));

            var yOutEstimate = new Dictionary<string, double>(yIn);

            // Isothermal first pass; the liquid enters at the top (index layerCount).
            var liquidTempByLayer = new double[layerCount + 1];
            for (int i = 0; i <= layerCount; i++) liquidTempByLayer[i] = inletLiquidTemperatureK;

            var layers = new List<LayerProfile>(layerCount + 1);
            var yTop = new Dictionary<string, double>(yIn);
            bool converged = false;
            int iterationsUsed = 0;

            for (int iter = 1; iter <= maxIterations; iter++)
            {
                iterationsUsed = iter;
                layers.Clear();

                var y = new Dictionary<string, double>(yIn);
                layers.Add(BuildLayer(0.0, y, xIn, yOutEstimate, gOverL, liquidTempByLayer[0], species, primary));

                // ── March the gas phase from bottom (z = 0) to top (z = H) ──
                for (int i = 1; i <= layerCount; i++)
                {
                    double tLocal = liquidTempByLayer[i - 1];
                    var yNext = new Dictionary<string, double>(y);

                    foreach (var code in species)
                    {
                        double x = xIn[code] + gOverL * (y[code] - yOutEstimate[code]);
                        double h = localHenrysConstantBySpecies(code, tLocal, x);
                        double kGa = localGasFilmCoeffBySpecies(code, tLocal);
                        double yStar = h * x;

                        // G·dy/dz = -KGa·P·(y - y*)  (explicit Euler step)
                        double dy = -(kGa * totalPressureKPa * (y[code] - yStar) / gasMolarFluxKmolM2Hr) * dz;
                        yNext[code] = Math.Max(y[code] + dy, 0.0);
                    }

                    y = yNext;
                    layers.Add(BuildLayer(i * dz, y, xIn, yOutEstimate, gOverL, tLocal, species, primary));
                }

                yTop = new Dictionary<string, double>(y);

                // ── Rebuild the liquid temperature profile from the TOP down. ──
                var newLiquidTemp = new double[layerCount + 1];
                newLiquidTemp[layerCount] = inletLiquidTemperatureK;
                for (int i = layerCount - 1; i >= 0; i--)
                {
                    double absorbedKmolPerHr = 0.0;
                    foreach (var code in species)
                    {
                        double yBelow = layers[i].GasMoleFractions[code];
                        double yAbove = layers[i + 1].GasMoleFractions[code];
                        absorbedKmolPerHr += gasMolarFluxKmolM2Hr * Math.Max(yBelow - yAbove, 0.0);
                    }

                    var thermal = HeatOfAbsorptionModel.TryCalculate(
                        absorbedKmolPerHr / 3600.0, heatOfSolutionKJmol,
                        liquidMassFluxKgM2Hr / 3600.0, liquidSpecificHeatKJKgK, newLiquidTemp[i + 1]);
                    newLiquidTemp[i] = thermal?.OutletLiquidTemperatureK ?? newLiquidTemp[i + 1];
                }

                double maxTempShift = 0.0;
                for (int i = 0; i <= layerCount; i++)
                    maxTempShift = Math.Max(maxTempShift, Math.Abs(newLiquidTemp[i] - liquidTempByLayer[i]));

                double maxYOutShift = 0.0;
                foreach (var code in species)
                    maxYOutShift = Math.Max(maxYOutShift, Math.Abs(yTop[code] - yOutEstimate[code]));

                liquidTempByLayer = newLiquidTemp;
                yOutEstimate = new Dictionary<string, double>(yTop);

                if (maxTempShift < convergenceTolerance && maxYOutShift < convergenceTolerance)
                {
                    converged = true;
                    break;
                }
            }

            var bottom = layers[0];

            return new TowerSolverResult
            {
                Converged = converged,
                IterationsUsed = iterationsUsed,
                OutletGasMoleFraction = yOutEstimate[primary],
                OutletLiquidMoleFraction = bottom.LiquidMoleFractions[primary],
                OutletLiquidTemperatureK = liquidTempByLayer[0],
                OutletGasMoleFractions = new Dictionary<string, double>(yOutEstimate),
                OutletLiquidMoleFractions = new Dictionary<string, double>(
                    bottom.LiquidMoleFractions.ToDictionary(kv => kv.Key, kv => kv.Value)),
                Layers = layers
            };
        }

        private static bool IsCarrier(string code) =>
            string.Equals(code, "BULK_CARRIER", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "Air", StringComparison.OrdinalIgnoreCase);

        private static LayerProfile BuildLayer(
            double heightM,
            IReadOnlyDictionary<string, double> y,
            IReadOnlyDictionary<string, double> xIn,
            IReadOnlyDictionary<string, double> yOut,
            double gOverL,
            double temperatureK,
            IReadOnlyList<string> species,
            string primary)
        {
            var gas = new Dictionary<string, double>(species.Count);
            var liquid = new Dictionary<string, double>(species.Count);

            foreach (var code in species)
            {
                gas[code] = y[code];
                liquid[code] = xIn[code] + gOverL * (y[code] - yOut[code]);
            }

            return new LayerProfile
            {
                HeightM = heightM,
                GasMoleFraction = gas[primary],
                LiquidMoleFraction = liquid[primary],
                LiquidTemperatureK = temperatureK,
                GasMoleFractions = gas,
                LiquidMoleFractions = liquid
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