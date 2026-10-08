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
    /// Gas properties are evaluated at the gas temperature, liquid properties and
    /// Henry equilibrium at the local liquid temperature. Incomplete inputs, invalid
    /// properties and non-convergence throw; there are no fallback removal fractions
    /// and no swallowed exceptions. HenrysLawConstant is the dimensionless y*/x ratio.
    /// </summary>
    public static class MultiPollutantIterativeSolver
    {
        private const int DefaultSegments = 5;
        private const int MaxIterations = 50;
        private const double TemperatureConvergenceTolC = 0.1;
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
            public double PressureKPa { get; set; } = 101.3;

            /// <summary>Liquid mixture molar mass, kg/kmol. Default is pure water.</summary>
            public double LiquidMolarMassKgKmol { get; set; } = 18.015;

            public double LiquidHeatCapacityKJKgK { get; set; } = 3.5;

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

            var output = new SolverOutput { Segments = new List<MultiPollutantSegment>(numSegments) };
            double[] liquidTempProfile = new double[numSegments + 1];
            double[] liquidTempProfileOld = new double[numSegments + 1];
            double segmentHeightM = input.TowerHeightM / numSegments;

            liquidTempProfile[0] = input.LiquidInletTempC;

            var pollutantInlets = new Dictionary<string, double>();
            foreach (var poll in input.Pollutants)
                pollutantInlets[poll.Code] = poll.InletPpm;

            double maxDeltaT = double.MaxValue;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                Array.Copy(liquidTempProfile, liquidTempProfileOld, liquidTempProfile.Length);
                output.Segments.Clear();

                var pollutantOutlets = new Dictionary<string, double>(pollutantInlets);

                for (int seg = 0; seg < numSegments; seg++)
                {
                    double liquidTempSegmentC = (liquidTempProfile[seg] + liquidTempProfile[seg + 1]) / 2.0;
                    double segmentHeatKW = 0.0;
                    var segment = new MultiPollutantSegment
                    {
                        LayerIndex = seg,
                        LiquidInletTempC = liquidTempProfile[seg],
                        GasTemperatureC = input.GasTemperatureC,
                        Pollutants = new Dictionary<string, PollutantSegmentState>()
                    };

                    foreach (var poll in input.Pollutants)
                    {
                        double inletPpm = pollutantOutlets[poll.Code];
                        double hCorr = poll.HenrysLawTemperatureCorrectionFn(liquidTempSegmentC);
                        double hLocal = poll.HenrysLawConstant * hCorr;

                        double removalFrac = ComputeSegmentRemovalFraction(
                            poll, input, hLocal, liquidTempSegmentC, segmentHeightM, gasMolarMass);
                        double outletPpm = inletPpm * (1.0 - removalFrac);

                        double pollutantFlowKmolS = (inletPpm / 1e6) * gasFlowKmolS;
                        double absorbedKmolS = removalFrac * pollutantFlowKmolS;
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

                        pollutantOutlets[poll.Code] = outletPpm;
                    }

                    double dT = segmentHeatKW / (input.LiquidMassFlowKgS * input.LiquidHeatCapacityKJKgK);
                    liquidTempProfile[seg + 1] = liquidTempProfile[seg] + dT;

                    segment.LiquidOutletTempC = liquidTempProfile[seg + 1];
                    segment.TotalHeatAbsorbedKW = segmentHeatKW;

                    output.Segments.Add(segment);
                }

                output.LiquidOutletTemperatureC = liquidTempProfile[numSegments];
                output.TotalHeatAbsorbedKW = output.Segments.Sum(s => s.TotalHeatAbsorbedKW);

                output.OverallRemovalEfficiency.Clear();
                foreach (var poll in input.Pollutants)
                {
                    double inlet = pollutantInlets[poll.Code];
                    double outlet = pollutantOutlets[poll.Code];
                    output.OverallRemovalEfficiency[poll.Code] =
                        inlet > 0 ? (inlet - outlet) / inlet * 100.0 : 0.0;
                }

                maxDeltaT = liquidTempProfile
                    .Select((t, i) => Math.Abs(t - liquidTempProfileOld[i]))
                    .Max();

                output.IterationCount = iter + 1;
                if (maxDeltaT < TemperatureConvergenceTolC)
                {
                    output.Converged = true;
                    return output;
                }
            }

            throw new SolverNonConvergenceException(MaxIterations, maxDeltaT / TemperatureConvergenceTolC);
        }

        private static double ComputeSegmentRemovalFraction(
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
                    poll.Code, poll.MolecularWeight, "Air", 28.97, tGasK, input.PressureKPa)
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
            double ntuSegment = overallKGa * segmentHeightM / gasMolarVelocityKmolM2S;

            return 1.0 - Math.Exp(-ntuSegment);
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
                if (p.InletPpm < 0.0)
                    throw new PropertyOutOfBoundsException("InletPpm:" + p.Code, p.InletPpm, 0.0, 1e6);
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