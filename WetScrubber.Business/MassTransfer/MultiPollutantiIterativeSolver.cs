using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// One pollutant in a tower segment.
    /// </summary>
    public sealed class PollutantSegmentState
    {
        public string PollutantCode { get; set; } = "";
        public double GasInletPpm { get; set; }
        public double GasOutletPpm { get; set; }
        public double RemovalFraction { get; set; } // 0-1
        public double MassAbsorbedKgS { get; set; }
        public double HeatReleasedKW { get; set; }
    }

    /// <summary>
    /// One layer in the tower, with state for all pollutants + liquid.
    /// Layer 0 is the gas inlet (bottom); liquid enters at the last layer (top).
    /// </summary>
    public sealed class MultiPollutantSegment
    {
        public int LayerIndex { get; set; }
        public Dictionary<string, PollutantSegmentState> Pollutants { get; set; } = new();
        public double LiquidInletTempC { get; set; }
        public double LiquidOutletTempC { get; set; }
        public double GasTemperatureC { get; set; }
        public double TotalHeatAbsorbedKW { get; set; }
    }

    /// <summary>
    /// Coupled multi-pollutant packed-tower solver (preliminary-design grade).
    /// Counter-current: gas enters at layer 0, fresh liquid enters at the top layer.
    /// Each layer is solved with an equilibrium-aware NTU relation that includes the
    /// absorption factor (S = H*G/L) and the liquid loading from the layer above, so
    /// removal can never exceed the equilibrium limit. The liquid solute profile and
    /// liquid temperature profile are iterated to convergence.
    /// Gas properties are evaluated at the gas temperature, liquid properties and
    /// Henry equilibrium at the local liquid temperature. Incomplete inputs, invalid
    /// properties and non-convergence throw; there are no fallback removal fractions
    /// and no swallowed exceptions. HenrysLawConstant is the dimensionless y*/x ratio
    /// evaluated at the liquid inlet temperature; HenrysLawTemperatureCorrectionFn
    /// must return the correction RELATIVE to the liquid inlet temperature.
    /// </summary>
    public static class MultiPollutantIterativeSolver
    {
        private const int DefaultSegments = 5;
        private const int MaxIterations = 500;
        private const double TemperatureConvergenceTolC = 0.1;
        private const double LoadingConvergenceTol = 1e-6;
        private const double RKPaKmol = 8.314462; // kPa*m3/(kmol*K)

        public sealed class PollutantInput
        {
            public string Code { get; set; } = "";
            public double InletPpm { get; set; }
            public double MolecularWeight { get; set; }

            /// <summary>Solute molar volume at normal boiling point, cm3/mol (Le Bas).
            /// If 0/unset, LiquidDiffusivityM2S is used as the supplied value.</summary>
            public double MolarVolumeCm3Mol { get; set; }
            public double HenrysLawConstant { get; set; }
            public double HeatOfAbsorptionKJKmol { get; set; }
            public Func<double, double> HenrysLawTemperatureCorrectionFn { get; set; } = _ => 1.0;
        }

        public sealed class SolverInput
        {
            public List<PollutantInput> Pollutants { get; set; } = new();
            public double GasTemperatureC { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidInletTempC { get; set; }
            public double LiquidMassFlowKgS { get; set; }
            public double LiquidDensityKgM3 { get; set; }

            /// <summary>Gas density at GasTemperatureC and PressureKPa, kg/m3.</summary>
            public double GasDensityKgM3 { get; set; } = 1.2;
            public double TowerHeightM { get; set; }
            public double TowerAreaM2 { get; set; }
            public double PackingSpecificAreaM2M3 { get; set; }
            public double PackingNominalSizeM { get; set; }
            public double PackingCriticalSurfaceTensionNM { get; set; } = 0.061;
            public double LiquidSurfaceTensionNM { get; set; } = 0.072;
            public double LiquidViscosityPas { get; set; } = 1e-3;
            public double GasViscosityPas { get; set; } = 1.8e-5;
            public double LiquidDiffusivityM2S { get; set; } = 2e-9;   // used when no MolarVolumeCm3Mol
            public double GasDiffusivityM2S { get; set; } = 2e-5;      // used when no Fuller data
            public double LiquidSolventMolecularWeightGMol { get; set; } = 18.02;
            public double LiquidSolventAssociationFactor { get; set; } = 2.6;

            /// <summary>Carrier gas code (must exist in the Fuller volume table).</summary>
            public string CarrierGasCode { get; set; } = "Air";

            /// <summary>Carrier gas molecular weight, g/mol.</summary>
            public double CarrierGasMolecularWeightGMol { get; set; } = 28.9647;

            public double PressureKPa { get; set; } = 101.3;

            /// <summary>Liquid mixture molar mass, kg/kmol. Default is pure water.</summary>
            public double LiquidMolarMassKgKmol { get; set; } = 18.015;

            /// <summary>Liquid heat capacity, kJ/(kg*K). Default is water.</summary>
            public double LiquidHeatCapacityKJKgK { get; set; } = 4.18;

            /// <summary>Reagent class in the scrubbing liquid; None = physical absorption (E = 1).</summary>
            public ReagentKind Reagent { get; set; } = ReagentKind.None;

            /// <summary>Reagent concentration, equivalents/L (OH- or H+).</summary>
            public double ReagentEqPerL { get; set; }
        }

        public sealed class SolverOutput
        {
            public List<MultiPollutantSegment> Segments { get; set; } = new();
            public double LiquidOutletTemperatureC { get; set; }
            public Dictionary<string, double> OverallRemovalEfficiency { get; set; } = new();
            public double TotalHeatAbsorbedKW { get; set; }
            public bool Converged { get; set; }
            public int IterationCount { get; set; }
        }

        public static SolverOutput SolveIterative(
            SolverInput input,
            int numSegments = DefaultSegments)
        {
            Validate(input);
            if (numSegments < 2) numSegments = 2;

            double tGasK = input.GasTemperatureC + 273.15;
            double gasMolarMass = input.GasDensityKgM3 * RKPaKmol * tGasK / input.PressureKPa;
            double gasFlowKmolS = input.GasMassFlowKgS / gasMolarMass;
            double liquidFlowKmolS = input.LiquidMassFlowKgS / input.LiquidMolarMassKgKmol;
            double gasToLiquidMolarRatio = gasFlowKmolS / liquidFlowKmolS;

            var output = new SolverOutput { Segments = new List<MultiPollutantSegment>(numSegments) };
            double segmentHeightM = input.TowerHeightM / numSegments;

            // Boundary index k = 0..N. Gas enters at k = 0 and leaves at k = N.
            // Liquid enters at k = N and leaves at k = 0.
            double[] liquidTempProfile = new double[numSegments + 1];
            double[] liquidTempProfileOld = new double[numSegments + 1];
            for (int k = 0; k <= numSegments; k++)
                liquidTempProfile[k] = input.LiquidInletTempC;

            // Liquid solute mole fraction at each boundary; fresh liquid at the top.
            var loading = new Dictionary<string, double[]>();
            var pollutantInlets = new Dictionary<string, double>();
            foreach (var poll in input.Pollutants)
            {
                pollutantInlets[poll.Code] = poll.InletPpm;
                loading[poll.Code] = new double[numSegments + 1];
            }

            double maxDeltaT = double.MaxValue;
            double maxDeltaLoading = double.MaxValue;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                Array.Copy(liquidTempProfile, liquidTempProfileOld, liquidTempProfile.Length);
                output.Segments.Clear();

                var gasPpm = new Dictionary<string, double>(pollutantInlets);

                // ── Gas sweep, bottom to top, using previous-iteration liquid state ──
                for (int seg = 0; seg < numSegments; seg++)
                {
                    double liquidTempSegmentC =
                        (liquidTempProfileOld[seg] + liquidTempProfileOld[seg + 1]) / 2.0;
                    double segmentHeatKW = 0.0;
                    var segment = new MultiPollutantSegment
                    {
                        LayerIndex = seg,
                        GasTemperatureC = input.GasTemperatureC,
                        Pollutants = new Dictionary<string, PollutantSegmentState>()
                    };

                    foreach (var poll in input.Pollutants)
                    {
                        double inletPpm = gasPpm[poll.Code];
                        double yIn = inletPpm / 1e6;
                        double xTop = loading[poll.Code][seg + 1];

                        double hCorr = poll.HenrysLawTemperatureCorrectionFn(liquidTempSegmentC);
                        double hLocal = poll.HenrysLawConstant * hCorr;
                        if (!(hLocal > 0.0) || double.IsInfinity(hLocal))
                            throw new PropertyOutOfBoundsException(
                                "HenrysLawLocal:" + poll.Code, hLocal, 0.0, double.MaxValue);

                        double ntuSegment = ComputeSegmentNtu(
                            poll, input, hLocal, liquidTempSegmentC, segmentHeightM, gasMolarMass);

                        double yOut = CountercurrentOutletY(
                            yIn, xTop, hLocal, gasToLiquidMolarRatio, ntuSegment);

                        double outletPpm = yOut * 1e6;
                        double removalFrac = yIn > 0.0 ? (yIn - yOut) / yIn : 0.0;

                        double absorbedKmolS = gasFlowKmolS * (yIn - yOut);
                        double absorbedKgS = absorbedKmolS * poll.MolecularWeight;

                        double heatKW = absorbedKmolS * Math.Abs(poll.HeatOfAbsorptionKJKmol);
                        segmentHeatKW += heatKW;

                        segment.Pollutants[poll.Code] = new PollutantSegmentState
                        {
                            PollutantCode = poll.Code,
                            GasInletPpm = inletPpm,
                            GasOutletPpm = outletPpm,
                            RemovalFraction = removalFrac,
                            MassAbsorbedKgS = absorbedKgS,
                            HeatReleasedKW = heatKW
                        };

                        gasPpm[poll.Code] = outletPpm;
                    }

                    segment.TotalHeatAbsorbedKW = segmentHeatKW;
                    output.Segments.Add(segment);
                }

                // ── Liquid sweep, top to bottom: temperature and solute loading ──
                liquidTempProfile[numSegments] = input.LiquidInletTempC;
                for (int seg = numSegments - 1; seg >= 0; seg--)
                {
                    double dT = output.Segments[seg].TotalHeatAbsorbedKW /
                                (input.LiquidMassFlowKgS * input.LiquidHeatCapacityKJKgK);
                    liquidTempProfile[seg] = liquidTempProfile[seg + 1] + dT;

                    output.Segments[seg].LiquidInletTempC = liquidTempProfile[seg + 1];
                    output.Segments[seg].LiquidOutletTempC = liquidTempProfile[seg];
                }

                maxDeltaLoading = 0.0;
                foreach (var poll in input.Pollutants)
                {
                    double[] x = loading[poll.Code];
                    double scale = Math.Max(1.0, pollutantInlets[poll.Code]);

                    double xAbove = 0.0; // fresh liquid at the top boundary
                    for (int seg = numSegments - 1; seg >= 0; seg--)
                    {
                        var state = output.Segments[seg].Pollutants[poll.Code];
                        double yIn = state.GasInletPpm / 1e6;
                        double yOut = state.GasOutletPpm / 1e6;

                        double xNew = xAbove + gasToLiquidMolarRatio * (yIn - yOut);

                        double delta = Math.Abs(xNew - x[seg]) * 1e6 / scale;
                        if (delta > maxDeltaLoading) maxDeltaLoading = delta;

                        x[seg] = xNew;
                        xAbove = xNew;
                    }
                    x[numSegments] = 0.0;
                }

                output.LiquidOutletTemperatureC = liquidTempProfile[0];
                output.TotalHeatAbsorbedKW = output.Segments.Sum(s => s.TotalHeatAbsorbedKW);

                output.OverallRemovalEfficiency.Clear();
                foreach (var poll in input.Pollutants)
                {
                    double inlet = pollutantInlets[poll.Code];
                    double outlet = gasPpm[poll.Code];
                    output.OverallRemovalEfficiency[poll.Code] =
                        inlet > 0 ? (inlet - outlet) / inlet * 100.0 : 0.0;
                }

                maxDeltaT = liquidTempProfile
                    .Select((t, i) => Math.Abs(t - liquidTempProfileOld[i]))
                    .Max();

                output.IterationCount = iter + 1;
                if (maxDeltaT < TemperatureConvergenceTolC &&
                    maxDeltaLoading < LoadingConvergenceTol)
                {
                    output.Converged = true;
                    return output;
                }
            }

            throw new SolverNonConvergenceException(
                MaxIterations,
                Math.Max(
                    maxDeltaT / TemperatureConvergenceTolC,
                    maxDeltaLoading / LoadingConvergenceTol));
        }

        /// <summary>
        /// Counter-current layer with constant H, G and L:
        /// (y_out - H*x_in) = (1 - S)(y_in - H*x_in) / (exp(NTU(1 - S)) - S), S = H*G/L.
        /// Valid for S &lt; 1, S = 1 (limit) and S &gt; 1; removal is bounded by equilibrium
        /// with the entering liquid (y_out -> H*x_in as NTU -> infinity).
        /// </summary>
        private static double CountercurrentOutletY(
            double yIn, double xIn, double hYx, double gasToLiquidMolarRatio, double ntu)
        {
            double s = hYx * gasToLiquidMolarRatio;
            double yEq = hYx * xIn;
            double driving = yIn - yEq;

            double factor;
            if (Math.Abs(1.0 - s) < 1e-6)
            {
                factor = 1.0 / (1.0 + ntu);
            }
            else
            {
                double e = Math.Exp(ntu * (1.0 - s));
                factor = (1.0 - s) / (e - s);
            }

            return yEq + factor * driving;
        }

        private static double ComputeSegmentNtu(
            PollutantInput poll, SolverInput input, double hYx,
            double liquidTempC, double segmentHeightM, double gasMolarMassKgKmol)
        {
            double tGasK = input.GasTemperatureC + 273.15;
            double tLiqK = liquidTempC + 273.15;

            var packing = new PackingMassTransferInput
            {
                SpecificAreaM2M3 = input.PackingSpecificAreaM2M3,
                NominalSizeM = input.PackingNominalSizeM,
                CriticalSurfaceTensionNM = input.PackingCriticalSurfaceTensionNM,
                LiquidSurfaceTensionNM = input.LiquidSurfaceTensionNM,
                TowerAreaM2 = input.TowerAreaM2,
                GasMassFlowKgS = input.GasMassFlowKgS,
                LiquidMassFlowKgS = input.LiquidMassFlowKgS
            };

            double dL = poll.MolarVolumeCm3Mol > 0
                ? WilkeChangDiffusivity.Calculate(
                    poll.MolarVolumeCm3Mol,
                    input.LiquidSolventAssociationFactor,
                    input.LiquidSolventMolecularWeightGMol,
                    input.LiquidViscosityPas * 1000.0, // Pa*s -> cP
                    tLiqK)
                : input.LiquidDiffusivityM2S;

            double dG = FullerGasDiffusivity.TryGetDiffusionVolume(poll.Code, out _)
                ? FullerGasDiffusivity.Calculate(
                    poll.Code, poll.MolecularWeight,
                    input.CarrierGasCode, input.CarrierGasMolecularWeightGMol,
                    tGasK, input.PressureKPa)
                : input.GasDiffusivityM2S;

            var onda = OndaMassTransferCorrelation.Calculate(
                packing,
                new GasPhaseProperties(tGasK, input.GasDensityKgM3, input.GasViscosityPas, dG),
                new LiquidPhaseProperties(tLiqK, input.LiquidDensityKgM3, input.LiquidViscosityPas,
                                          dL, input.LiquidSurfaceTensionNM));

            double liquidMolarDensityKmolM3 = input.LiquidDensityKgM3 / input.LiquidMolarMassKgKmol;

            // ky (per unit y) and kx (per unit x)
            double kGaY = onda.GasFilmCoeffKmolM2SPa * (input.PressureKPa * 1000.0) * onda.WettedAreaM2M3;
            double kLaX = onda.LiquidFilmCoeffMS * liquidMolarDensityKmolM3 * onda.WettedAreaM2M3;

            double hCgCl = new HenryValue(hYx, HenryConvention.YOverX)
                .ToCgOverCl(tLiqK, input.PressureKPa, input.LiquidDensityKgM3, input.LiquidMolarMassKgKmol);

            var enhancement = ReactiveEnhancementService.Compute(new ReactiveEnhancementInput
            {
                PollutantCode = poll.Code,
                Reagent = input.Reagent,
                ReagentConcentrationEqPerL = input.ReagentEqPerL,
                LiquidFilmCoeffMS = onda.LiquidFilmCoeffMS,
                PollutantLiquidDiffusivityM2S = dL,
                HenrysDimensionless = hCgCl,
                GasPartialPressureKPa = poll.InletPpm / 1e6 * input.PressureKPa,
                TemperatureK = tLiqK
            });
            kLaX *= enhancement.Factor;

            if (!(kGaY > 0) || !(kLaX > 0))
                throw new PropertyOutOfBoundsException("VolumetricCoefficient", Math.Min(kGaY, kLaX), 0.0, double.MaxValue);

            // 1/KyA = 1/kyA + H(y/x)/kxA
            double overallKGa = 1.0 / (1.0 / kGaY + hYx / kLaX);

            double gasMolarVelocityKmolM2S = input.GasMassFlowKgS / input.TowerAreaM2 / gasMolarMassKgKmol;
            return overallKGa * segmentHeightM / gasMolarVelocityKmolM2S;
        }

        private static void Validate(SolverInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Pollutants == null || input.Pollutants.Count == 0)
                throw new ArgumentException("At least one pollutant required.");

            RequirePositive(nameof(input.TowerHeightM), input.TowerHeightM);
            RequirePositive(nameof(input.GasMassFlowKgS), input.GasMassFlowKgS);
            RequirePositive(nameof(input.LiquidMassFlowKgS), input.LiquidMassFlowKgS);
            RequirePositive(nameof(input.LiquidDensityKgM3), input.LiquidDensityKgM3);
            RequirePositive(nameof(input.GasDensityKgM3), input.GasDensityKgM3);
            RequirePositive(nameof(input.PressureKPa), input.PressureKPa);
            RequirePositive(nameof(input.LiquidMolarMassKgKmol), input.LiquidMolarMassKgKmol);
            RequirePositive(nameof(input.LiquidHeatCapacityKJKgK), input.LiquidHeatCapacityKJKgK);

            if (double.IsNaN(input.GasTemperatureC) || double.IsInfinity(input.GasTemperatureC))
                throw new PropertyOutOfBoundsException(nameof(input.GasTemperatureC), input.GasTemperatureC, -273.15, double.MaxValue);
            if (double.IsNaN(input.LiquidInletTempC) || double.IsInfinity(input.LiquidInletTempC))
                throw new PropertyOutOfBoundsException(nameof(input.LiquidInletTempC), input.LiquidInletTempC, -273.15, double.MaxValue);

            OndaMassTransferCorrelation.RequireComplete(new PackingMassTransferInput
            {
                SpecificAreaM2M3 = input.PackingSpecificAreaM2M3,
                NominalSizeM = input.PackingNominalSizeM,
                CriticalSurfaceTensionNM = input.PackingCriticalSurfaceTensionNM,
                LiquidSurfaceTensionNM = input.LiquidSurfaceTensionNM,
                TowerAreaM2 = input.TowerAreaM2,
                GasMassFlowKgS = input.GasMassFlowKgS,
                LiquidMassFlowKgS = input.LiquidMassFlowKgS
            });

            foreach (var p in input.Pollutants)
            {
                if (string.IsNullOrWhiteSpace(p.Code))
                    throw new ArgumentException("Pollutant code is required.");
                RequirePositive("MolecularWeight:" + p.Code, p.MolecularWeight);
                RequirePositive("HenrysLawConstant:" + p.Code, p.HenrysLawConstant);
                if (!(p.InletPpm >= 0.0) || double.IsInfinity(p.InletPpm))
                    throw new PropertyOutOfBoundsException("InletPpm:" + p.Code, p.InletPpm, 0.0, 1e6);
                if (double.IsNaN(p.HeatOfAbsorptionKJKmol) || double.IsInfinity(p.HeatOfAbsorptionKJKmol))
                    throw new PropertyOutOfBoundsException("HeatOfAbsorptionKJKmol:" + p.Code, p.HeatOfAbsorptionKJKmol, 0.0, double.MaxValue);
                if (p.HenrysLawTemperatureCorrectionFn == null)
                    throw new ArgumentException($"HenrysLawTemperatureCorrectionFn missing for '{p.Code}'.");
            }
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new PropertyOutOfBoundsException(name, value, 0.0, double.MaxValue);
        }
    }
}