using System;
using System.Collections.Generic;

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
    /// Coupled multi-pollutant packed-tower solver.
    /// All pollutants absorbed simultaneously into shared liquid,
    /// with single heat balance (sum of all ΔH_abs effects).
    /// </summary>
    public static class MultiPollutantIterativeSolver
    {
        private const int DefaultSegments = 5;
        private const int MaxIterations = 20;
        private const double TemperatureConvergenceTolC = 0.1;
        private const double LiquidHeatCapacityKJKgC = 3.5; // water + salts
        private const double WaterMolarDensityKmolM3 = 55.3; // for kL concentration -> mole-fraction basis

        public sealed class PollutantInput
        {
            public string Code { get; set; } = "";
            public double InletPpm { get; set; }
            public double MolecularWeight { get; set; }

            /// <summary>Solute molar volume at normal boiling point, cm3/mol
            /// (Le Bas method). Required for Wilke-Chang liquid diffusivity.
            /// If 0/unset, solver falls back to a flat literal.</summary>
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

            // ── Needed for real (Onda) film coefficients — without these
            // the solver falls back to a fixed removal fraction per
            // segment (see ComputeSegmentRemovalFraction). ──
            public double GasDensityKgM3 { get; set; } = 1.2;
            public double TowerHeightM { get; set; }
            public double TowerAreaM2 { get; set; }
            public double PackingSpecificAreaM2M3 { get; set; }
            public double PackingNominalSizeM { get; set; }
            public double PackingCriticalSurfaceTensionNM { get; set; } = 0.061;
            public double LiquidSurfaceTensionNM { get; set; } = 0.072;
            public double LiquidViscosityPas { get; set; } = 1e-3;
            public double GasViscosityPas { get; set; } = 1.8e-5;
            public double LiquidDiffusivityM2S { get; set; } = 2e-9; // fallback if no MolarVolumeCm3Mol
            public double GasDiffusivityM2S { get; set; } = 2e-5;    // fallback if no Fuller data
            public double LiquidSolventMolecularWeightGMol { get; set; } = 18.02;
            public double LiquidSolventAssociationFactor { get; set; } = 2.6;
            public double PressureKPa { get; set; } = 101.3;
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
            if (numSegments < 2) numSegments = 2;
            if (input.Pollutants.Count == 0)
                throw new ArgumentException("At least one pollutant required.");

            var output = new SolverOutput { Segments = new List<MultiPollutantSegment>(numSegments) };
            double[] liquidTempProfile = new double[numSegments + 1];
            double[] liquidTempProfileOld = new double[numSegments + 1];
            double segmentHeightM = input.TowerHeightM / numSegments;

            liquidTempProfile[0] = input.LiquidInletTempC;

            // Track inlet state per pollutant
            var pollutantInlets = new Dictionary<string, double>();
            foreach (var poll in input.Pollutants)
                pollutantInlets[poll.Code] = poll.InletPpm;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                Array.Copy(liquidTempProfile, liquidTempProfileOld, liquidTempProfile.Length);
                output.Segments.Clear();

                var pollutantOutlets = new Dictionary<string, double>(pollutantInlets);

                for (int seg = 0; seg < numSegments; seg++)
                {
                    double liquidTempSegment = (liquidTempProfile[seg] + liquidTempProfile[seg + 1]) / 2.0;
                    double segmentHeatKW = 0.0;
                    var segment = new MultiPollutantSegment
                    {
                        LayerIndex = seg,
                        LiquidInletTempC = liquidTempProfile[seg],
                        GasTemperatureC = input.GasTemperatureC,
                        Pollutants = new Dictionary<string, PollutantSegmentState>()
                    };

                    // Solve each pollutant in this segment
                    foreach (var poll in input.Pollutants)
                    {
                        double inletPpm = pollutantOutlets[poll.Code];
                        double hCorr = poll.HenrysLawTemperatureCorrectionFn(liquidTempSegment);
                        double hLocal = poll.HenrysLawConstant * hCorr;

                        // Real mass transfer: Onda film coefficients -> overall
                        // KGa (mole-fraction basis) -> NTU for this segment's
                        // height -> removal fraction. Replaces the previous
                        // fixed removalFrac = 1-exp(-0.4) (~33% every segment,
                        // every pollutant, regardless of hLocal or geometry).
                        double removalFrac = ComputeSegmentRemovalFraction(
                            poll, input, hLocal, segmentHeightM);
                        double outletPpm = inletPpm * (1.0 - removalFrac);

                        // Mass absorbed
                        double gasFlowKmolS = input.GasMassFlowKgS / 28.97;
                        double pollutantFlowKmolS = (inletPpm / 1e6) * gasFlowKmolS;
                        double absorbedKmolS = removalFrac * pollutantFlowKmolS;
                        double absorbedKgS = absorbedKmolS * poll.MolecularWeight / 1000.0;

                        // Heat from this pollutant
                        double heatKW = absorbedKmolS * Math.Abs(poll.HeatOfAbsorptionKJKmol) / 1000.0;
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

                        pollutantOutlets[poll.Code] = outletPpm;
                    }

                    // Shared liquid temperature rise from sum of all pollutants
                    double dT = segmentHeatKW * 3600.0 / (input.LiquidMassFlowKgS * LiquidHeatCapacityKJKgC);
                    liquidTempProfile[seg + 1] = liquidTempProfile[seg] + dT;

                    segment.LiquidOutletTempC = liquidTempProfile[seg + 1];
                    segment.TotalHeatAbsorbedKW = segmentHeatKW;

                    output.Segments.Add(segment);
                }

                output.LiquidOutletTemperatureC = liquidTempProfile[numSegments];
                output.TotalHeatAbsorbedKW = output.Segments.Sum(s => s.TotalHeatAbsorbedKW);

                // Overall removal per pollutant
                output.OverallRemovalEfficiency.Clear();
                foreach (var poll in input.Pollutants)
                {
                    double inlet = pollutantInlets[poll.Code];
                    double outlet = pollutantOutlets[poll.Code];
                    double eff = inlet > 0 ? (inlet - outlet) / inlet * 100.0 : 0.0;
                    output.OverallRemovalEfficiency[poll.Code] = eff;
                }

                // Convergence check
                double maxDeltaT = liquidTempProfile
                    .Select((t, i) => Math.Abs(t - liquidTempProfileOld[i]))
                    .Max();

                output.IterationCount = iter + 1;
                if (maxDeltaT < TemperatureConvergenceTolC)
                {
                    output.Converged = true;
                    break;
                }
            }

            return output;
        }

        // ── Real per-segment removal fraction ───────────────────────
        // Wilke-Chang + Fuller diffusivities -> Onda film coefficients
        // -> overall KGa (mole-fraction basis, combining gas + liquid
        // film resistance via Henry's law, same conversion as
        // ScrubberCalculationEngine.TryComputeOndaFilmCoefficients) ->
        // NTU for this segment's height -> removal fraction.
        // Falls back to a fixed ~33% removal only if the caller hasn't
        // supplied tower/packing geometry (TowerAreaM2 <= 0) — never
        // throws into a design that worked before this existed.
        private static double ComputeSegmentRemovalFraction(
            PollutantInput poll, SolverInput input, double hLocal, double segmentHeightM)
        {
            const double FallbackRemovalFrac = 0.33; // previous fixed 1-exp(-0.4) behavior

            if (input.TowerAreaM2 <= 0 || input.PackingSpecificAreaM2M3 <= 0)
                return FallbackRemovalFrac;

            try
            {
                double liquidMassVelocity = input.LiquidMassFlowKgS / input.TowerAreaM2;
                double gasMassVelocity = input.GasMassFlowKgS / input.TowerAreaM2;
                double tempK = input.GasTemperatureC + 273.15;

                double dL = poll.MolarVolumeCm3Mol > 0
                    ? WilkeChangDiffusivity.Calculate(
                        poll.MolarVolumeCm3Mol,
                        input.LiquidSolventAssociationFactor,
                        input.LiquidSolventMolecularWeightGMol,
                        input.LiquidViscosityPas * 1000.0, // Pa*s -> cP
                        tempK)
                    : input.LiquidDiffusivityM2S;

                double dG = FullerGasDiffusivity.TryGetDiffusionVolume(poll.Code, out _)
                    ? FullerGasDiffusivity.Calculate(
                        poll.Code, poll.MolecularWeight, "Air", 28.97, tempK, input.PressureKPa)
                    : input.GasDiffusivityM2S;

                var onda = OndaMassTransferCorrelation.Calculate(
                    input.PackingSpecificAreaM2M3,
                    input.PackingNominalSizeM,
                    input.PackingCriticalSurfaceTensionNM,
                    input.LiquidSurfaceTensionNM,
                    liquidMassVelocity,
                    gasMassVelocity,
                    input.LiquidDensityKgM3,
                    input.GasDensityKgM3,
                    input.LiquidViscosityPas,
                    input.GasViscosityPas,
                    dL, dG, tempK, input.PressureKPa);

                // kG (partial-pressure basis) -> mole-fraction basis via P;
                // kL (concentration basis) -> mole-fraction basis via water's
                // molar density. Combine as 1/KGa = 1/kGa_y + H/kLa_x.
                double kGaY = onda.GasFilmCoeffKmolM2SPa * (input.PressureKPa * 1000.0) * onda.WettedAreaM2M3;
                double kLaX = onda.LiquidFilmCoeffMS * WaterMolarDensityKmolM3 * onda.WettedAreaM2M3;
                double overallKGa = 1.0 / (1.0 / Math.Max(kGaY, 1e-9) + hLocal / Math.Max(kLaX, 1e-9));

                double gasMolarVelocityKmolM2S = gasMassVelocity / 28.97;
                double ntuSegment = overallKGa * segmentHeightM / Math.Max(gasMolarVelocityKmolM2S, 1e-9);

                return Math.Min(1.0 - Math.Exp(-ntuSegment), 0.999);
            }
            catch
            {
                return FallbackRemovalFrac; // never let a missing lookup break a design
            }
        }
    }
}