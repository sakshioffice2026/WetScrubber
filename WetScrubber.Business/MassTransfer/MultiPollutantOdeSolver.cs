using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Wrapper: RigourousTowerOdeSolver + MultiPollutantIterativeSolver interface.
    /// Accepts same input as MultiPollutantIterativeSolver, solves via RK45 ODE.
    /// </summary>
    public static class MultiPollutantOdeSolver
    {
        public sealed class SolverInput
        {
            public List<MultiPollutantIterativeSolver.PollutantInput> Pollutants { get; set; } = new();
            public double GasTemperatureC { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidInletTempC { get; set; }
            public double LiquidMassFlowKgS { get; set; }
            public double LiquidDensityKgM3 { get; set; }

            // ── Phase B: Replace hardcoded 1.2 kg/m³ with EOS calculation ────
            /// <summary>Gas composition (code → mole fraction). If provided,
            /// GasDensityKgM3 is computed via Peng-Robinson EOS. If null/empty,
            /// falls back to LegacyGasDensityKgM3 for backwards compat.</summary>
            public IReadOnlyDictionary<string, double> GasCompositionMoleFraction { get; set; }
                = new Dictionary<string, double>();

            /// <summary>Computed gas density from EOS (set by SolveOde if
            /// GasCompositionMoleFraction is provided). Otherwise use
            /// LegacyGasDensityKgM3.</summary>
            public double GasDensityKgM3 { get; set; }

            /// <summary>Fallback density (kg/m³) if no composition provided.
            /// Deprecated: prefer GasCompositionMoleFraction + EOS.</summary>
            public double LegacyGasDensityKgM3 { get; set; } = 1.2;

            /// <summary>Liquid dynamic viscosity, Pa·s. Water ≈1e-3 at 20°C,
            /// but slurries/organics differ a lot — must be supplied, not assumed.</summary>
            public double LiquidViscosityPas { get; set; } = 1e-3;

            /// <summary>Gas dynamic viscosity, Pa·s. Air ≈1.8e-5 at 20°C.</summary>
            public double GasViscosityPas { get; set; } = 1.8e-5;

            /// <summary>Liquid-phase molecular diffusivity of pollutant, m²/s.
            /// Should come from Wilke-Chang, not a flat literal.</summary>
            /// <summary>Fallback liquid diffusivity, used only when a pollutant's
            /// MolarVolumeCm3Mol is unset (Wilke-Chang can't be computed).</summary>
            public double LiquidDiffusivityM2S { get; set; } = 2e-9;

            /// <summary>Solvent molecular weight, g/mol, for Wilke-Chang. Water = 18.02.</summary>
            public double LiquidSolventMolecularWeightGMol { get; set; } = 18.02;

            /// <summary>Wilke-Chang solvent association factor phi. Water = 2.6,
            /// methanol = 1.9, benzene/unassociated = 1.0.</summary>
            public double LiquidSolventAssociationFactor { get; set; } = 2.6;

            /// <summary>Gas-phase molecular diffusivity of pollutant, m²/s.
            /// Should come from Fuller correlation, not a flat literal.</summary>
            public double GasDiffusivityM2S { get; set; } = 2e-5;

            /// <summary>Critical surface tension of packing material, N/m.
            /// Polyethylene ≈0.033, ceramic ≈0.061, steel ≈0.075 — NOT water's 0.072.</summary>
            public double PackingCriticalSurfaceTensionNM { get; set; } = 0.061;

            /// <summary>Liquid surface tension, N/m. Water ≈0.072 at 20°C but
            /// drops with surfactants/temperature — must be supplied per liquid.</summary>
            public double LiquidSurfaceTensionNM { get; set; } = 0.072;

            public double TowerHeightM { get; set; }
            public double TowerAreaM2 { get; set; }
            public double PackingSpecificAreaM2M3 { get; set; }
            public double PackingNominalSizeM { get; set; }

            /// <summary>Pressure (kPa) — used by EOS. Defaults to 101.3 (1 atm).</summary>
            public double PressureKPa { get; set; } = 101.3;

            /// <summary>Inlet liquid pollutant loading, kg pollutant/kg
            /// liquid, keyed by species code — non-zero when the liquid
            /// feed is recycled sump water rather than fresh makeup.
            /// Pollutants not present here fall back to the old
            /// near-zero (0.0001) fresh-liquid assumption.</summary>
            public IReadOnlyDictionary<string, double> InletLiquidLoadingKgKg { get; set; }
                = new Dictionary<string, double>();
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

            /// <summary>Outlet liquid pollutant loading, kg pollutant/kg
            /// liquid — feeds a downstream LiquidStream so it can be
            /// recirculated with its accumulated loading intact.</summary>
            public IReadOnlyDictionary<string, double> OutletLiquidLoadingKgKg { get; set; }
                = new Dictionary<string, double>();
        }

        public static SolverOutput SolveOde(SolverInput input)
        {
            // ── Phase B: Compute gas density from EOS if composition provided ────
            double gasDensityKgM3 = input.GasDensityKgM3;
            if (input.GasCompositionMoleFraction != null && input.GasCompositionMoleFraction.Count > 0)
            {
                var eos = new PengRobinsonEos();
                var henrysLaw = new HenrysLawCalculator();
                var activityModel = new NrtlActivityModel();
                var thermoService = new Thermodynamics.ThermoCalculationService(eos, henrysLaw, activityModel);

                var gasComp = input.GasCompositionMoleFraction
                    .Select(kvp => (kvp.Key, kvp.Value))
                    .ToList();

                gasDensityKgM3 = thermoService.CalculateGasDensityKgM3(
                    gasComp, input.GasTemperatureC, input.PressureKPa);
            }
            else
            {
                gasDensityKgM3 = input.LegacyGasDensityKgM3;
            }

            var odeInput = new RigourousTowerOdeSolver.SolverInput
            {
                PollutantCodes = input.Pollutants.Select(p => p.Code).ToList(),
                InletConcKgM3 = input.Pollutants.ToDictionary(p => p.Code, p => p.InletPpm),
                InitialLiquidFraction = input.Pollutants.ToDictionary(
                    p => p.Code,
                    p => input.InletLiquidLoadingKgKg.TryGetValue(p.Code, out var loaded) ? loaded : 0.0001),
                GasTemperatureK = input.GasTemperatureC + 273.15,
                LiquidInletTemperatureK = input.LiquidInletTempC + 273.15,
                TowerHeightM = input.TowerHeightM,
                TowerAreaM2 = input.TowerAreaM2,
                GasMassFlowKgS = input.GasMassFlowKgS,
                LiquidMassFlowKgS = input.LiquidMassFlowKgS,
                LiquidDensityKgM3 = input.LiquidDensityKgM3,
                GasDensityKgM3 = gasDensityKgM3,

                OndaLookup = (code, Tg, Tl) =>
                {
                    var poll = input.Pollutants.First(p => p.Code == code);
                    double tAvg = (Tg + Tl) / 2.0;

                    double dL = poll.MolarVolumeCm3Mol > 0
                        ? WilkeChangDiffusivity.Calculate(
                            poll.MolarVolumeCm3Mol,
                            input.LiquidSolventAssociationFactor,
                            input.LiquidSolventMolecularWeightGMol,
                            input.LiquidViscosityPas * 1000.0, // Pa*s -> cP
                            tAvg)
                        : input.LiquidDiffusivityM2S; // no molar volume supplied: fallback

                    double dG = FullerGasDiffusivity.TryGetDiffusionVolume(code, out _)
                        ? FullerGasDiffusivity.Calculate(
                            code, poll.MolecularWeight,
                            "Air", 28.97,
                            tAvg, input.PressureKPa)
                        : input.GasDiffusivityM2S; // no Fuller data for this species: fallback

                    return OndaMassTransferCorrelation.Calculate(
                        input.PackingSpecificAreaM2M3,
                        input.PackingNominalSizeM,
                        input.PackingCriticalSurfaceTensionNM,
                        input.LiquidSurfaceTensionNM,
                        input.LiquidMassFlowKgS / input.TowerAreaM2,
                        input.GasMassFlowKgS / input.TowerAreaM2,
                        input.LiquidDensityKgM3,
                        input.GasDensityKgM3,
                        input.LiquidViscosityPas,
                        input.GasViscosityPas,
                        dL,
                        dG,
                        tAvg, input.PressureKPa);
                },

                HenrysLawFn = (code, T) =>
                {
                    var poll = input.Pollutants.First(p => p.Code == code);
                    double corr = poll.HenrysLawTemperatureCorrectionFn(T - 273.15);
                    return poll.HenrysLawConstant * corr;
                },

                MolWeightFn = (code) => input.Pollutants.First(p => p.Code == code).MolecularWeight,
                HeatOfAbsorptionFn = (code) => input.Pollutants.First(p => p.Code == code).HeatOfAbsorptionKJKmol
            };

            var odeSolver = new RigourousTowerOdeSolver();
            var odeOutput = odeSolver.Solve(odeInput);

            // Convert ODE profile → multi-segment output (for compatibility)
            var output = new SolverOutput
            {
                Converged = odeOutput.Converged,
                NodeCount = odeOutput.Profile.Count,
                LiquidOutletTemperatureC = odeOutput.OutletLiquidTemperatureK - 273.15,
                OverallRemovalEfficiency = odeOutput.RemovalEfficiency,
                Segments = new List<MultiPollutantSegment>(),

                OutletGasTemperatureK = odeOutput.Profile.Last().GasTemperatureK,

                OutletConcKgM3 = odeOutput.Profile.Last().PollutantConcKgM3,

                OutletLiquidLoadingKgKg = odeOutput.OutletLiquidFraction
            };

            // Create synthetic segments from ODE nodes (every 5th node)
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

                var inletNode = odeOutput.Profile[0];
                foreach (var code in input.Pollutants.Select(p => p.Code))
                {
                    double c0 = inletNode.PollutantConcKgM3[code];
                    double cNow = node.PollutantConcKgM3[code];
                    double removal = c0 > 1e-12 ? (c0 - cNow) / c0 : 0.0;

                    seg.Pollutants[code] = new PollutantSegmentState
                    {
                        PollutantCode = code,
                        GasInletPpm = cNow,
                        RemovalFraction = Math.Clamp(removal, 0.0, 1.0)
                    };
                }

                output.Segments.Add(seg);
            }

            output.TotalHeatAbsorbedKW = Math.Abs(
                input.LiquidMassFlowKgS * 4.18 *
                (odeOutput.OutletLiquidTemperatureK - (input.LiquidInletTempC + 273.15)));
            return output;
        }
    }
}