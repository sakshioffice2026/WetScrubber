using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Wrapper over RigourousTowerOdeSolver (counter-current, separate gas and liquid
    /// temperatures, local gas density, unified Henry convention, electrolyte-aware
    /// equilibrium). HenrysLawConstant on each pollutant is interpreted as the
    /// dimensionless mole-fraction ratio y*/x at system pressure (HenryConvention.YOverX).
    /// </summary>
    public static class MultiPollutantOdeSolver
    {
        private const double RKPaKmol = 8.314462;   // kPa*m3/(kmol*K)
        private const double SutherlandAirK = 110.4;

        public sealed class DissociationSpec
        {
            public DissociationKind Kind { get; set; } = DissociationKind.None;
            public double DissociationConstantMolPerL { get; set; }
            public double MeanIonicActivityCoefficient { get; set; } = 1.0;
        }

        public sealed class SolverInput
        {
            public List<MultiPollutantIterativeSolver.PollutantInput> Pollutants { get; set; } = new();
            public double GasTemperatureC { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidInletTempC { get; set; }
            public double LiquidMassFlowKgS { get; set; }
            public double LiquidDensityKgM3 { get; set; }

            /// <summary>Gas composition (code -> mole fraction). If provided, inlet gas density
            /// is computed via Peng-Robinson EOS.</summary>
            public IReadOnlyDictionary<string, double> GasCompositionMoleFraction { get; set; }
                = new Dictionary<string, double>();

            /// <summary>Inlet gas density, kg/m3 at inlet T and P. Used when no composition is given.</summary>
            public double GasDensityKgM3 { get; set; }

            /// <summary>Used only when neither composition nor GasDensityKgM3 is supplied.</summary>
            public double LegacyGasDensityKgM3 { get; set; } = 1.2;

            /// <summary>Liquid viscosity, Pa*s, at LiquidInletTempC (constant unless LiquidViscosityPasFn is set).</summary>
            public double LiquidViscosityPas { get; set; } = 1e-3;

            /// <summary>Gas viscosity, Pa*s, at GasTemperatureC; scaled with temperature by Sutherland's law.</summary>
            public double GasViscosityPas { get; set; } = 1.8e-5;

            /// <summary>Liquid diffusivity at LiquidInletTempC when no solute molar volume is given.</summary>
            public double LiquidDiffusivityM2S { get; set; } = 2e-9;

            public double LiquidSolventMolecularWeightGMol { get; set; } = 18.02;
            public double LiquidSolventAssociationFactor { get; set; } = 2.6;

            /// <summary>Gas diffusivity at inlet T and P when no Fuller data exists; scaled by T^1.75 / P.</summary>
            public double GasDiffusivityM2S { get; set; } = 2e-5;

            public double PackingCriticalSurfaceTensionNM { get; set; } = 0.061;

            /// <summary>Liquid surface tension, N/m, at LiquidInletTempC (constant unless LiquidSurfaceTensionNMFn is set).</summary>
            public double LiquidSurfaceTensionNM { get; set; } = 0.072;

            public double TowerHeightM { get; set; }
            public double TowerAreaM2 { get; set; }
            public double PackingSpecificAreaM2M3 { get; set; }
            public double PackingNominalSizeM { get; set; }

            /// <summary>Bottom (gas inlet) pressure, kPa.</summary>
            public double PressureKPa { get; set; } = 101.3;

            /// <summary>Total packed-bed pressure drop, kPa (linear profile).</summary>
            public double PressureDropKPa { get; set; } = 0.0;

            public IReadOnlyDictionary<string, double> InletLiquidLoadingKgKg { get; set; }
                = new Dictionary<string, double>();

            // ---- Liquid mixture ----
            /// <summary>If supplied, mixture molar mass = 1 / sum(w_i / MW_i).</summary>
            public IReadOnlyList<LiquidComponent> LiquidComposition { get; set; }

            /// <summary>Used when LiquidComposition is null. Default is pure water.</summary>
            public double LiquidMolarMassKgKmol { get; set; } = 18.015;

            // ---- Thermal data (explicit, overridable) ----
            public double LiquidHeatCapacityKJKgK { get; set; } = 4.18;
            public double GasHeatCapacityKJKgK { get; set; } = 1.05;
            public double GasPrandtl { get; set; } = 0.71;

            // ---- Optional liquid property correlations in Tl (K) ----
            public Func<double, double> LiquidDensityKgM3Fn { get; set; }
            public Func<double, double> LiquidViscosityPasFn { get; set; }
            public Func<double, double> LiquidSurfaceTensionNMFn { get; set; }
            public Func<double, double> LiquidHeatCapacityKJKgKFn { get; set; }

            // ---- Electrolyte behaviour (HCl, NH3, SO2 ...) ----
            public IReadOnlyDictionary<string, DissociationSpec> DissociationByCode { get; set; }
                = new Dictionary<string, DissociationSpec>();

            /// <summary>Scrubbing-liquor pH. Mandatory when DissociationByCode is non-empty.</summary>
            public double LiquidPH { get; set; } = double.NaN;
        }

        public sealed class SolverOutput
        {
            public List<MultiPollutantSegment> Segments { get; set; } = new();
            public double LiquidOutletTemperatureC { get; set; }
            public Dictionary<string, double> OverallRemovalEfficiency { get; set; } = new();
            public double TotalHeatAbsorbedKW { get; set; }
            public bool Converged { get; set; }
            public int NodeCount { get; set; }
            public double OutletGasTemperatureK { get; set; }
            public IReadOnlyDictionary<string, double> OutletConcKgM3 { get; set; }

            public IReadOnlyDictionary<string, double> OutletLiquidLoadingKgKg { get; set; }
                = new Dictionary<string, double>();
        }

        public static SolverOutput SolveOde(SolverInput input)
        {
            Validate(input);

            double tGasIn = input.GasTemperatureC + 273.15;
            double tLiqIn = input.LiquidInletTempC + 273.15;
            double pIn = input.PressureKPa;

            // ---- Inlet gas density -> effective gas molar mass ----
            double gasDensityInlet;
            if (input.GasCompositionMoleFraction != null && input.GasCompositionMoleFraction.Count > 0)
            {
                var eos = new PengRobinsonEos();
                var henrysLaw = new HenrysLawCalculator();
                var activityModel = new NrtlActivityModel();
                var thermoService = new ThermoCalculationService(eos, henrysLaw, activityModel);

                var gasComp = input.GasCompositionMoleFraction
                    .Select(kvp => (kvp.Key, kvp.Value))
                    .ToList();

                gasDensityInlet = thermoService.CalculateGasDensityKgM3(gasComp, input.GasTemperatureC, pIn);
            }
            else if (input.GasDensityKgM3 > 0.0)
            {
                gasDensityInlet = input.GasDensityKgM3;
            }
            else
            {
                gasDensityInlet = input.LegacyGasDensityKgM3;
            }

            if (!(gasDensityInlet > 0.0))
                throw new PropertyOutOfBoundsException("GasDensityKgM3", gasDensityInlet, 0.0, double.MaxValue);

            double gasMolarMass = gasDensityInlet * RKPaKmol * tGasIn / pIn;

            // ---- Liquid mixture ----
            double liquidMolarMass = input.LiquidComposition != null && input.LiquidComposition.Count > 0
                ? LiquidMixtureProperties.MixtureMolarMassKgKmol(input.LiquidComposition)
                : input.LiquidMolarMassKgKmol;
            if (!(liquidMolarMass > 0.0))
                throw new PropertyOutOfBoundsException("LiquidMolarMassKgKmol", liquidMolarMass, 0.0, double.MaxValue);

            Func<double, double> rhoL = input.LiquidDensityKgM3Fn ?? (_ => input.LiquidDensityKgM3);
            Func<double, double> muL = input.LiquidViscosityPasFn ?? (_ => input.LiquidViscosityPas);
            Func<double, double> sigmaL = input.LiquidSurfaceTensionNMFn ?? (_ => input.LiquidSurfaceTensionNM);
            Func<double, double> cpL = input.LiquidHeatCapacityKJKgKFn ?? (_ => input.LiquidHeatCapacityKJKgK);

            var byCode = input.Pollutants.ToDictionary(p => p.Code);

            // ---- Property and equilibrium callbacks (gas at Tg, liquid at Tl) ----
            Func<string, double, double> henryCgOverCl = (code, tl) =>
            {
                var p = byCode[code];
                double hYx = p.HenrysLawConstant * p.HenrysLawTemperatureCorrectionFn(tl - 273.15);
                double hcc = new HenryValue(hYx, HenryConvention.YOverX)
                    .ToCgOverCl(tl, pIn, rhoL(tl), liquidMolarMass);

                if (input.DissociationByCode != null
                    && input.DissociationByCode.TryGetValue(code, out var spec)
                    && spec.Kind != DissociationKind.None)
                {
                    hcc = DissociatingSpeciesEquilibrium.ApparentCgOverCl(
                        hcc, spec.Kind, spec.DissociationConstantMolPerL,
                        input.LiquidPH, spec.MeanIonicActivityCoefficient);
                }

                return hcc;
            };

            Func<string, double, double, double> gasDiffusivity = (code, tg, pKPa) =>
            {
                if (FullerGasDiffusivity.TryGetDiffusionVolume(code, out _))
                    return FullerGasDiffusivity.Calculate(code, byCode[code].MolecularWeight, "Air", 28.97, tg, pKPa);

                return input.GasDiffusivityM2S * Math.Pow(tg / tGasIn, 1.75) * (pIn / pKPa);
            };

            Func<string, double, double> liquidDiffusivity = (code, tl) =>
            {
                var p = byCode[code];
                if (p.MolarVolumeCm3Mol > 0.0)
                {
                    return WilkeChangDiffusivity.Calculate(
                        p.MolarVolumeCm3Mol,
                        input.LiquidSolventAssociationFactor,
                        input.LiquidSolventMolecularWeightGMol,
                        muL(tl) * 1000.0,   // Pa*s -> cP
                        tl);
                }

                // Stokes-Einstein scaling of the supplied reference diffusivity
                return input.LiquidDiffusivityM2S * (tl / tLiqIn) * (muL(tLiqIn) / muL(tl));
            };

            var odeInput = new RigourousTowerOdeSolver.SolverInput
            {
                PollutantCodes = input.Pollutants.Select(p => p.Code).ToList(),
                InletConcKgM3 = input.Pollutants.ToDictionary(
                    p => p.Code,
                    p => (p.InletPpm / 1e6) * (pIn / (RKPaKmol * tGasIn)) * p.MolecularWeight),
                InitialLiquidFraction = input.Pollutants.ToDictionary(
                    p => p.Code,
                    p => input.InletLiquidLoadingKgKg != null
                         && input.InletLiquidLoadingKgKg.TryGetValue(p.Code, out var loaded) ? loaded : 0.0),

                GasTemperatureK = tGasIn,
                LiquidInletTemperatureK = tLiqIn,
                TowerHeightM = input.TowerHeightM,
                TowerAreaM2 = input.TowerAreaM2,
                GasMassFlowKgS = input.GasMassFlowKgS,
                LiquidMassFlowKgS = input.LiquidMassFlowKgS,

                PressureBottomKPa = pIn,
                TotalPressureDropKPa = input.PressureDropKPa,
                GasMolarMassKgKmol = gasMolarMass,
                GasHeatCapacityKJKgK = input.GasHeatCapacityKJKgK,
                GasPrandtl = input.GasPrandtl,

                PackingSpecificAreaM2M3 = input.PackingSpecificAreaM2M3,
                PackingNominalSizeM = input.PackingNominalSizeM,
                PackingCriticalSurfaceTensionNM = input.PackingCriticalSurfaceTensionNM,

                GasViscosityPasFn = tg => SutherlandViscosity(input.GasViscosityPas, tGasIn, tg),
                GasDiffusivityM2SFn = gasDiffusivity,

                LiquidDensityKgM3Fn = rhoL,
                LiquidViscosityPasFn = muL,
                LiquidSurfaceTensionNMFn = sigmaL,
                LiquidHeatCapacityKJKgKFn = cpL,
                LiquidDiffusivityM2SFn = liquidDiffusivity,

                HenryCgOverClFn = henryCgOverCl,
                EquilibriumGasConcKmolM3Fn = (code, tl, cl, pKPa) => henryCgOverCl(code, tl) * cl,

                MolWeightFn = code => byCode[code].MolecularWeight,
                HeatOfAbsorptionFn = code => byCode[code].HeatOfAbsorptionKJKmol
            };

            var odeOutput = new RigourousTowerOdeSolver().Solve(odeInput);

            var output = new SolverOutput
            {
                Converged = odeOutput.Converged,
                NodeCount = odeOutput.Profile.Count,
                LiquidOutletTemperatureC = odeOutput.OutletLiquidTemperatureK - 273.15,
                OverallRemovalEfficiency = odeOutput.RemovalEfficiency,
                Segments = new List<MultiPollutantSegment>(),
                OutletGasTemperatureK = odeOutput.OutletGasTemperatureK,
                OutletConcKgM3 = odeOutput.OutletConcKgM3,
                OutletLiquidLoadingKgKg = odeOutput.OutletLiquidFraction,
                TotalHeatAbsorbedKW = odeOutput.TotalHeatAbsorbedKW
            };

            // ---- Synthetic segments from profile nodes ----
            var inletNode = odeOutput.Profile[0];
            var inletRatio = PollutantPerCarrier(inletNode, input.Pollutants);

            int stride = Math.Max(1, odeOutput.Profile.Count / 5);
            for (int i = 0; i < odeOutput.Profile.Count; i += stride)
            {
                var node = odeOutput.Profile[i];
                var seg = new MultiPollutantSegment
                {
                    LayerIndex = i / stride,
                    LiquidInletTempC = node.LiquidTemperatureK - 273.15,
                    GasTemperatureC = node.GasTemperatureK - 273.15,
                    Pollutants = new Dictionary<string, PollutantSegmentState>()
                };

                var nodeRatio = PollutantPerCarrier(node, input.Pollutants);

                foreach (var p in input.Pollutants)
                {
                    double y = MoleFraction(node, p);
                    double r0 = inletRatio[p.Code];
                    double removal = r0 > 1e-30 ? 1.0 - nodeRatio[p.Code] / r0 : 0.0;

                    seg.Pollutants[p.Code] = new PollutantSegmentState
                    {
                        PollutantCode = p.Code,
                        GasInletPpm = y * 1e6,
                        RemovalFraction = Math.Clamp(removal, 0.0, 1.0)
                    };
                }

                output.Segments.Add(seg);
            }

            return output;
        }

        private static double SutherlandViscosity(double muRef, double tRefK, double tK)
        {
            return muRef
                * Math.Pow(tK / tRefK, 1.5)
                * (tRefK + SutherlandAirK) / (tK + SutherlandAirK);
        }

        private static double MoleFraction(
            TowerOdeState node, MultiPollutantIterativeSolver.PollutantInput p)
        {
            double molarDensity = node.PressureKPa / (RKPaKmol * node.GasTemperatureK);
            return node.PollutantConcKgM3[p.Code] / p.MolecularWeight / molarDensity;
        }

        private static Dictionary<string, double> PollutantPerCarrier(
            TowerOdeState node, List<MultiPollutantIterativeSolver.PollutantInput> pollutants)
        {
            double sumY = 0.0;
            var ys = new Dictionary<string, double>();
            foreach (var p in pollutants)
            {
                double y = MoleFraction(node, p);
                ys[p.Code] = y;
                sumY += y;
            }

            if (!(sumY < 1.0))
                throw new PropertyOutOfBoundsException("TotalPollutantMoleFraction", sumY, 0.0, 1.0);

            var result = new Dictionary<string, double>();
            foreach (var kv in ys)
                result[kv.Key] = kv.Value / (1.0 - sumY);
            return result;
        }

        private static void Validate(SolverInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Pollutants == null || input.Pollutants.Count == 0)
                throw new ArgumentException("At least one pollutant is required.");

            foreach (var p in input.Pollutants)
            {
                if (string.IsNullOrWhiteSpace(p.Code))
                    throw new ArgumentException("Pollutant code is required.");
                if (!(p.MolecularWeight > 0.0))
                    throw new PropertyOutOfBoundsException("MolecularWeight:" + p.Code, p.MolecularWeight, 0.0, double.MaxValue);
                if (!(p.HenrysLawConstant > 0.0))
                    throw new PropertyOutOfBoundsException("HenrysLawConstant:" + p.Code, p.HenrysLawConstant, 0.0, double.MaxValue);
                if (p.InletPpm < 0.0)
                    throw new PropertyOutOfBoundsException("InletPpm:" + p.Code, p.InletPpm, 0.0, 1e6);
                if (p.HenrysLawTemperatureCorrectionFn == null)
                    throw new ArgumentException($"HenrysLawTemperatureCorrectionFn missing for '{p.Code}'.");
            }

            if (!(input.PressureKPa > 0.0))
                throw new PropertyOutOfBoundsException(nameof(input.PressureKPa), input.PressureKPa, 0.0, double.MaxValue);
            if (!(input.LiquidDensityKgM3 > 0.0) && input.LiquidDensityKgM3Fn == null)
                throw new PropertyOutOfBoundsException(nameof(input.LiquidDensityKgM3), input.LiquidDensityKgM3, 0.0, double.MaxValue);

            if (input.DissociationByCode != null && input.DissociationByCode.Count > 0
                && (double.IsNaN(input.LiquidPH) || input.LiquidPH < 0.0 || input.LiquidPH > 14.0))
                throw new PropertyOutOfBoundsException(nameof(input.LiquidPH), input.LiquidPH, 0.0, 14.0);
        }
    }
}