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

        /// <summary>
        /// Local absorption rate per unit height, KGa·P·(y − y*), kmol/(m2·hr·m)
        /// (single-component solver only; NaN otherwise).
        /// </summary>
        public double AbsorptionRateKmolM2HrPerM { get; set; } = double.NaN;

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

        /// <summary>
        /// |y_out(2N layers) − y_out(N layers)| / y_in. NaN when the refinement
        /// check was not run.
        /// </summary>
        public double LayerRefinementRelativeDifference { get; set; } = double.NaN;

        /// <summary>
        /// Absorption obtained by integrating KGa·P·(y − y*) over the packed height
        /// (trapezoid on the final profile), kmol/(m2·hr). Independent of the
        /// operating-line balance G·(y_in − y_out). NaN when not computed.
        /// </summary>
        public double ProfileIntegratedAbsorbedFluxKmolM2Hr { get; set; } = double.NaN;

        /// <summary>
        /// |integrated absorption − G·(y_in − y_out)| / (G·y_in). NaN when not computed.
        /// </summary>
        public double ProfileClosureRelativeError { get; set; } = double.NaN;

        /// <summary>Gas leaving the top, every absorbing species (multi-component solver only).</summary>
        public IReadOnlyDictionary<string, double> OutletGasMoleFractions { get; set; }
            = new Dictionary<string, double>();

        /// <summary>Liquid leaving the bottom, every absorbing species (multi-component solver only).</summary>
        public IReadOnlyDictionary<string, double> OutletLiquidMoleFractions { get; set; }
            = new Dictionary<string, double>();

        /// <summary>RK4 stage values clamped to zero (0 = none).</summary>
        public int NegativeClampCount { get; set; }

        /// <summary>True if heat of solution was missing and absorption heat was ignored.</summary>
        public bool ThermalCouplingSkipped { get; set; }
    }

    /// <summary>
    /// Phase 3 discretized solver. Slices the tower into layers (z=0 at the gas
    /// inlet/bottom, z=H at the top) and integrates the gas-phase mass balance
    /// up the tower with a classical fourth-order Runge-Kutta step per layer,
    /// coupled to a liquid energy balance (HeatOfAbsorptionModel) that shifts the
    /// local temperature-dependent Henry's constant layer by layer.
    ///
    /// Henry's constant and the film coefficient are re-evaluated at the local
    /// liquid temperature and local liquid composition at every Runge-Kutta stage.
    ///
    /// Convergence is RELATIVE: the outlet gas fraction must stop moving by less
    /// than convergenceTolerance × y_in, and the liquid temperature profile by
    /// less than temperatureToleranceK kelvin.
    ///
    /// After solving, the tower is re-solved with twice as many layers
    /// (layer-refinement check) and the outlet difference is reported.
    ///
    /// This is a RATING calculation, not a sizing one.
    ///
    /// Pure math — no DB/IO. Callers inject local kinetics via delegates.
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
            int maxIterations = 100,
            double convergenceTolerance = 1e-4,
            double temperatureToleranceK = 1e-3,
            bool performRefinementCheck = true)
        {
            if (layerCount < 2) throw new ArgumentException("Need at least 2 layers.", nameof(layerCount));
            if (packingHeightM <= 0) throw new ArgumentException("Packing height must be positive.", nameof(packingHeightM));
            if (localGasFilmCoeff == null) throw new ArgumentNullException(nameof(localGasFilmCoeff));
            if (localHenrysConstant == null) throw new ArgumentNullException(nameof(localHenrysConstant));
            if (!(convergenceTolerance > 0)) throw new ArgumentException("Convergence tolerance must be positive.", nameof(convergenceTolerance));
            if (!(temperatureToleranceK > 0)) throw new ArgumentException("Temperature tolerance must be positive.", nameof(temperatureToleranceK));
            if (!(gasMolarFluxKmolM2Hr > 0)) throw new ArgumentException("Gas molar flux must be positive.", nameof(gasMolarFluxKmolM2Hr));
            if (!(liquidMolarFluxKmolM2Hr > 0)) throw new ArgumentException("Liquid molar flux must be positive.", nameof(liquidMolarFluxKmolM2Hr));
            if (!(totalPressureKPa > 0)) throw new ArgumentException("Total pressure must be positive.", nameof(totalPressureKPa));

            var result = SolveCore(
                packingHeightM, layerCount,
                gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr,
                liquidMassFluxKgM2Hr, liquidSpecificHeatKJKgK,
                inletGasMoleFraction, inletLiquidMoleFraction,
                outletGasMoleFractionTarget, inletLiquidTemperatureK,
                heatOfSolutionKJmol, totalPressureKPa,
                localGasFilmCoeff, localHenrysConstant,
                maxIterations, convergenceTolerance, temperatureToleranceK);

            if (performRefinementCheck && layerCount <= 5000)
            {
                var fine = SolveCore(
                    packingHeightM, layerCount * 2,
                    gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr,
                    liquidMassFluxKgM2Hr, liquidSpecificHeatKJKgK,
                    inletGasMoleFraction, inletLiquidMoleFraction,
                    outletGasMoleFractionTarget, inletLiquidTemperatureK,
                    heatOfSolutionKJmol, totalPressureKPa,
                    localGasFilmCoeff, localHenrysConstant,
                    maxIterations, convergenceTolerance, temperatureToleranceK);

                double yScale = Math.Max(Math.Abs(inletGasMoleFraction), 1e-300);
                result.LayerRefinementRelativeDifference =
                    Math.Abs(fine.OutletGasMoleFraction - result.OutletGasMoleFraction) / yScale;
            }

            return result;
        }

        private static TowerSolverResult SolveCore(
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
            int maxIterations,
            double convergenceTolerance,
            double temperatureToleranceK)
        {
            double dz = packingHeightM / layerCount;
            double gSafe = gasMolarFluxKmolM2Hr;
            double yScale = Math.Max(Math.Abs(inletGasMoleFraction), 1e-300);
            double yTolerance = convergenceTolerance * yScale;

            double yOutEstimate = outletGasMoleFractionTarget;

            // Isothermal on the first pass — no absorbed-heat history yet.
            var liquidTempByLayer = new double[layerCount + 1];
            for (int i = 0; i <= layerCount; i++) liquidTempByLayer[i] = inletLiquidTemperatureK;

            var layers = new List<LayerProfile>(layerCount + 1);
            bool converged = false;
            int iterationsUsed = 0;
            int clampCount = 0;
            bool thermalSkipped = false;
            double NonNeg(double v)
            {
                if (v < 0.0) { clampCount++; return 0.0; }
                return v;
            }

            for (int iter = 1; iter <= maxIterations; iter++)
            {
                iterationsUsed = iter;
                layers.Clear();

                double yOutForLine = yOutEstimate;

                // dy/dz = −KGa·P·(y − y*) / G, with x from the operating line and
                // H, KGa evaluated at the supplied local liquid temperature.
                double Rate(double yv, double tK)
                {
                    double xv = OperatingLineX(yv, yOutForLine, inletLiquidMoleFraction,
                        gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr);
                    double h = localHenrysConstant(tK, xv);
                    double kGa = localGasFilmCoeff(tK);
                    return -(kGa * totalPressureKPa * (yv - h * xv)) / gSafe;
                }

                double y = inletGasMoleFraction;
                layers.Add(new LayerProfile
                {
                    HeightM = 0,
                    GasMoleFraction = y,
                    LiquidMoleFraction = OperatingLineX(y, yOutForLine, inletLiquidMoleFraction,
                        gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr),
                    LiquidTemperatureK = liquidTempByLayer[0]
                });

                // ── March the gas phase from bottom (z=0) to top (z=H), RK4 per layer ──
                for (int i = 1; i <= layerCount; i++)
                {
                    double z = i * dz;
                    double tLayer = 0.5 * (liquidTempByLayer[i - 1] + liquidTempByLayer[i]);

                    double k1 = Rate(y, tLayer);
                    double k2 = Rate(NonNeg(y + 0.5 * dz * k1), tLayer);
                    double k3 = Rate(NonNeg(y + 0.5 * dz * k2), tLayer);
                    double k4 = Rate(NonNeg(y + dz * k3), tLayer);

                    y = NonNeg(y + dz / 6.0 * (k1 + 2.0 * k2 + 2.0 * k3 + k4));

                    layers.Add(new LayerProfile
                    {
                        HeightM = z,
                        GasMoleFraction = y,
                        LiquidMoleFraction = OperatingLineX(y, yOutForLine, inletLiquidMoleFraction,
                            gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr),
                        LiquidTemperatureK = liquidTempByLayer[i]
                    });
                }

                double yOutComputed = y;

                // ── Rebuild liquid temperature profile layer by layer.
                //    Liquid enters at the top (index layerCount) at the inlet
                //    temperature and flows down; each layer's heat pickup is
                //    G·(y(z_i) − y(z_i+1)) and is added to the temperature
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
                    if (thermal == null && layerAbsorbedKmolPerHr > 0.0) thermalSkipped = true;
                    newLiquidTemp[i] = thermal?.OutletLiquidTemperatureK ?? newLiquidTemp[i + 1];
                }

                double maxTempShift = 0.0;
                for (int i = 0; i <= layerCount; i++)
                    maxTempShift = Math.Max(maxTempShift, Math.Abs(newLiquidTemp[i] - liquidTempByLayer[i]));

                double yOutShift = Math.Abs(yOutComputed - yOutEstimate);

                liquidTempByLayer = newLiquidTemp;
                yOutEstimate += 0.7 * (yOutComputed - yOutEstimate);

                if (maxTempShift < temperatureToleranceK && yOutShift < yTolerance)
                {
                    converged = true;
                    break;
                }
            }

            // ── Local absorption rate at every layer boundary, then an
            //    independent integral of the rate over the packed height. ──
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                double xL = layer.LiquidMoleFraction;
                double h = localHenrysConstant(layer.LiquidTemperatureK, xL);
                double kGa = localGasFilmCoeff(layer.LiquidTemperatureK);
                layer.AbsorptionRateKmolM2HrPerM =
                    kGa * totalPressureKPa * (layer.GasMoleFraction - h * xL);
            }

            double integrated = 0.0;
            for (int i = 1; i < layers.Count; i++)
                integrated += 0.5 * (layers[i - 1].AbsorptionRateKmolM2HrPerM
                                     + layers[i].AbsorptionRateKmolM2HrPerM) * dz;

            double yOutFinal = layers[layers.Count - 1].GasMoleFraction;
            double gasBalanceAbsorbed = gasMolarFluxKmolM2Hr * (inletGasMoleFraction - yOutFinal);
            double closure = Math.Abs(integrated - gasBalanceAbsorbed)
                             / Math.Max(gSafe * yScale, 1e-300);

            return new TowerSolverResult
            {
                Converged = converged,
                IterationsUsed = iterationsUsed,
                OutletGasMoleFraction = layers[layers.Count - 1].GasMoleFraction,
                OutletLiquidMoleFraction = layers[0].LiquidMoleFraction, // bottom = richest liquid
                OutletLiquidTemperatureK = liquidTempByLayer[0],
                ProfileIntegratedAbsorbedFluxKmolM2Hr = integrated,
                ProfileClosureRelativeError = closure,
                NegativeClampCount = clampCount,
                ThermalCouplingSkipped = thermalSkipped,
                Layers = layers
            };
        }

        /// <summary>
        /// Coupled multi-species solver. z = 0 is the gas inlet (bottom), z = H
        /// the top. The liquid enters at the top at <paramref name="inletLiquidTemperatureK"/>
        /// and flows down. Each absorbing species has its own liquid operating line
        /// x_i(z) = x_i,in + (G/L)·(y_i(z) − y_i,out); gas carriers
        /// ("BULK_CARRIER", "Air") are not transferred. All species are reported
        /// per layer and at the outlet. Each species is integrated with RK4 per
        /// layer using the local liquid temperature.
        /// Convergence is relative to each species' inlet mole fraction.
        /// </summary>
        private static TowerSolverResult SolveMultiComponentCore(
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
            int maxIterations = 100,
            double convergenceTolerance = 1e-4,
            double temperatureToleranceK = 1e-3,
            Func<string, double?>? heatOfSolutionBySpecies = null,
            Func<string, double>? reactionHeatKJPerMolBySpecies = null,
            Func<double, double, double>? gasSideHeatToLiquidKJHrM2 = null)
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
            if (!(convergenceTolerance > 0)) throw new ArgumentException("Convergence tolerance must be positive.", nameof(convergenceTolerance));
            if (!(temperatureToleranceK > 0)) throw new ArgumentException("Temperature tolerance must be positive.", nameof(temperatureToleranceK));

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
            int clampCount = 0;
            bool thermalSkipped = false;
            double NonNeg(double v)
            {
                if (v < 0.0) { clampCount++; return 0.0; }
                return v;
            }

            for (int iter = 1; iter <= maxIterations; iter++)
            {
                iterationsUsed = iter;
                layers.Clear();

                var yOutForLine = new Dictionary<string, double>(yOutEstimate);

                double RateFor(string code, double yv, double tK)
                {
                    double xv = xIn[code] + gOverL * (yv - yOutForLine[code]);
                    double h = localHenrysConstantBySpecies(code, tK, xv);
                    double kGa = localGasFilmCoeffBySpecies(code, tK);
                    return -(kGa * totalPressureKPa * (yv - h * xv)) / gasMolarFluxKmolM2Hr;
                }

                var y = new Dictionary<string, double>(yIn);
                layers.Add(BuildLayer(0.0, y, xIn, yOutForLine, gOverL, liquidTempByLayer[0], species, primary));

                // ── March the gas phase from bottom (z = 0) to top (z = H), RK4 per layer ──
                for (int i = 1; i <= layerCount; i++)
                {
                    double tLayer = 0.5 * (liquidTempByLayer[i - 1] + liquidTempByLayer[i]);
                    var yNext = new Dictionary<string, double>(y);

                    foreach (var code in species)
                    {
                        double y0 = y[code];
                        double k1 = RateFor(code, y0, tLayer);
                        double k2 = RateFor(code, NonNeg(y0 + 0.5 * dz * k1), tLayer);
                        double k3 = RateFor(code, NonNeg(y0 + 0.5 * dz * k2), tLayer);
                        double k4 = RateFor(code, NonNeg(y0 + dz * k3), tLayer);

                        yNext[code] = NonNeg(y0 + dz / 6.0 * (k1 + 2.0 * k2 + 2.0 * k3 + k4));
                    }

                    y = yNext;
                    layers.Add(BuildLayer(i * dz, y, xIn, yOutForLine, gOverL, liquidTempByLayer[i], species, primary));
                }

                yTop = new Dictionary<string, double>(y);

                // ── Rebuild the liquid temperature profile from the TOP down. ──
                var newLiquidTemp = new double[layerCount + 1];
                newLiquidTemp[layerCount] = inletLiquidTemperatureK;
                for (int i = layerCount - 1; i >= 0; i--)
                {
                    double tOut = newLiquidTemp[i + 1];
                    double zMid = (i + 0.5) * dz;
                    foreach (var code in species)
                    {
                        double yBelow = layers[i].GasMoleFractions[code];
                        double yAbove = layers[i + 1].GasMoleFractions[code];
                        double absorbedKmolPerHr = gasMolarFluxKmolM2Hr * Math.Max(yBelow - yAbove, 0.0);
                        if (absorbedKmolPerHr <= 0.0) continue;

                        double? dH = heatOfSolutionBySpecies?.Invoke(code) ?? heatOfSolutionKJmol;
                        var thermal = HeatOfAbsorptionModel.TryCalculate(
                            absorbedKmolPerHr / 3600.0, dH,
                            liquidMassFluxKgM2Hr / 3600.0, liquidSpecificHeatKJKgK, tOut);
                        if (thermal == null) thermalSkipped = true;
                        else tOut = thermal.OutletLiquidTemperatureK;

                        double rxnKJPerMol = reactionHeatKJPerMolBySpecies?.Invoke(code) ?? 0.0;
                        if (rxnKJPerMol != 0.0)
                        {
                            var rxn = HeatOfAbsorptionModel.TryCalculate(
                                absorbedKmolPerHr / 3600.0, rxnKJPerMol,
                                liquidMassFluxKgM2Hr / 3600.0, liquidSpecificHeatKJKgK, tOut);
                            if (rxn != null) tOut = rxn.OutletLiquidTemperatureK;
                        }
                    }

                    if (gasSideHeatToLiquidKJHrM2 != null && liquidMassFluxKgM2Hr > 0.0 && liquidSpecificHeatKJKgK > 0.0)
                        tOut += gasSideHeatToLiquidKJHrM2(zMid, tOut) * dz
                                / (liquidMassFluxKgM2Hr * liquidSpecificHeatKJKgK);

                    newLiquidTemp[i] = tOut;
                }

                double maxTempShift = 0.0;
                for (int i = 0; i <= layerCount; i++)
                    maxTempShift = Math.Max(maxTempShift, Math.Abs(newLiquidTemp[i] - liquidTempByLayer[i]));

                bool speciesConverged = true;
                foreach (var code in species)
                {
                    double scale = Math.Max(Math.Abs(yIn[code]), 1e-300);
                    if (Math.Abs(yTop[code] - yOutEstimate[code]) >= convergenceTolerance * scale)
                    {
                        speciesConverged = false;
                        break;
                    }
                }

                var relaxed = new Dictionary<string, double>(yTop.Count);
                foreach (var code in species)
                    relaxed[code] = yOutEstimate[code] + 0.7 * (yTop[code] - yOutEstimate[code]);

                liquidTempByLayer = newLiquidTemp;
                yOutEstimate = relaxed;

                if (maxTempShift < temperatureToleranceK && speciesConverged)
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
                OutletGasMoleFraction = yTop[primary],
                OutletLiquidMoleFraction = bottom.LiquidMoleFractions[primary],
                OutletLiquidTemperatureK = liquidTempByLayer[0],
                OutletGasMoleFractions = new Dictionary<string, double>(yTop),
                OutletLiquidMoleFractions = new Dictionary<string, double>(
                    bottom.LiquidMoleFractions.ToDictionary(kv => kv.Key, kv => kv.Value)),
                NegativeClampCount = clampCount,
                ThermalCouplingSkipped = thermalSkipped,
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
            int maxIterations = 100,
            double convergenceTolerance = 1e-4,
            double temperatureToleranceK = 1e-3,
            Func<string, double?>? heatOfSolutionBySpecies = null,
            Func<string, double>? reactionHeatKJPerMolBySpecies = null,
            Func<double, double, double>? gasSideHeatToLiquidKJHrM2 = null,
            bool performRefinementCheck = true)
        {
            var result = SolveMultiComponentCore(
                packingHeightM, layerCount, gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr,
                liquidMassFluxKgM2Hr, liquidSpecificHeatKJKgK, inletGasComposition,
                inletLiquidComposition, inletLiquidTemperatureK, heatOfSolutionKJmol,
                totalPressureKPa, localGasFilmCoeffBySpecies, localHenrysConstantBySpecies,
                maxIterations, convergenceTolerance, temperatureToleranceK,
                heatOfSolutionBySpecies, reactionHeatKJPerMolBySpecies, gasSideHeatToLiquidKJHrM2);

            if (performRefinementCheck && layerCount <= 5000)
            {
                var fine = SolveMultiComponentCore(
                    packingHeightM, layerCount * 2, gasMolarFluxKmolM2Hr, liquidMolarFluxKmolM2Hr,
                    liquidMassFluxKgM2Hr, liquidSpecificHeatKJKgK, inletGasComposition,
                    inletLiquidComposition, inletLiquidTemperatureK, heatOfSolutionKJmol,
                    totalPressureKPa, localGasFilmCoeffBySpecies, localHenrysConstantBySpecies,
                    maxIterations, convergenceTolerance, temperatureToleranceK,
                    heatOfSolutionBySpecies, reactionHeatKJPerMolBySpecies, gasSideHeatToLiquidKJHrM2);

                double worst = 0.0;
                foreach (var kv in result.OutletGasMoleFractions)
                {
                    double scale = Math.Max(Math.Abs(inletGasComposition.MoleFractionOf(kv.Key)), 1e-300);
                    worst = Math.Max(worst, Math.Abs(fine.OutletGasMoleFractions[kv.Key] - kv.Value) / scale);
                }
                result.LayerRefinementRelativeDifference = worst;
            }

            return result;
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
            => xIn + (gasMolarFlux / liquidMolarFlux) * (y - yOut);
    }
}