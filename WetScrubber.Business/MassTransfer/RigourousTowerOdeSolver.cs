using System;
using System.Collections.Generic;
using System.Linq;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Node in the continuous ODE profile.
    /// </summary>
    public sealed class TowerOdeState
    {
        public double Height { get; set; }                    // m (integration variable)
        public Dictionary<string, double> PollutantConcKgM3 { get; set; } = new();  // ppm equiv
        public Dictionary<string, double> LiquidMassFraction { get; set; } = new(); // kg/kg
        public double GasTemperatureK { get; set; }
        public double LiquidTemperatureK { get; set; }
    }

    /// <summary>
    /// RK45 (Runge-Kutta 4th/5th order) ODE solver for packed-tower mass transfer + energy.
    /// Replaces discrete 5-segment iterative solver with continuous profile.
    /// </summary>
    public sealed class RigourousTowerOdeSolver
    {
        private double stepHeightM = 0.05;      // dz (adaptive could reduce this)
        private const double Tolerance = 1e-8;
        private const int MaxSteps = 10000;

        public sealed class SolverInput
        {
            public List<string> PollutantCodes { get; set; } = new();
            public Dictionary<string, double> InletConcKgM3 { get; set; } = new();    // ppm-equiv
            public Dictionary<string, double> InitialLiquidFraction { get; set; } = new();
            public double GasTemperatureK { get; set; }
            public double LiquidInletTemperatureK { get; set; }
            public double TowerHeightM { get; set; }
            public double TowerAreaM2 { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidMassFlowKgS { get; set; }
            public double LiquidDensityKgM3 { get; set; }
            public double GasDensityKgM3 { get; set; }

            // Callables for Onda + Henry
            public Func<string, double, double, OndaResult> OndaLookup { get; set; }
            public Func<string, double, double> HenrysLawFn { get; set; }
            public Func<string, double> MolWeightFn { get; set; }
            public Func<string, double> HeatOfAbsorptionFn { get; set; }
        }

        public sealed class SolverOutput
        {
            public List<TowerOdeState> Profile { get; set; } = new();
            public Dictionary<string, double> OutletConcKgM3 { get; set; } = new();
            public Dictionary<string, double> OutletLiquidFraction { get; set; } = new();
            public double OutletGasTemperatureK { get; set; }
            public double OutletLiquidTemperatureK { get; set; }
            public Dictionary<string, double> RemovalEfficiency { get; set; } = new();
            public bool Converged { get; set; }
        }

        public SolverOutput Solve(SolverInput input)
        {
            var state = new TowerOdeState
            {
                Height = 0,
                GasTemperatureK = input.GasTemperatureK,
                LiquidTemperatureK = input.LiquidInletTemperatureK
            };

            foreach (var code in input.PollutantCodes)
            {
                state.PollutantConcKgM3[code] = input.InletConcKgM3[code];
                state.LiquidMassFraction[code] = input.InitialLiquidFraction[code];
            }

            var profile = new List<TowerOdeState> { state };
            int stepCount = 0;

            while (state.Height < input.TowerHeightM && stepCount < MaxSteps)
            {
                var dydt = ComputeDerivatives(state, input);
                state = StepRK45(state, dydt, input, stepHeightM);
                profile.Add(state);
                stepCount++;
            }

            var final = profile.Last();
            var output = new SolverOutput
            {
                Profile = profile,
                OutletGasTemperatureK = final.GasTemperatureK,
                OutletLiquidTemperatureK = final.LiquidTemperatureK,
                Converged = state.Height >= input.TowerHeightM
            };

            foreach (var code in input.PollutantCodes)
            {
                output.OutletConcKgM3[code] = final.PollutantConcKgM3[code];
                output.OutletLiquidFraction[code] = final.LiquidMassFraction[code];

                double inlet = input.InletConcKgM3[code];
                double outlet = final.PollutantConcKgM3[code];
                output.RemovalEfficiency[code] = inlet > 0 ? (inlet - outlet) / inlet * 100.0 : 0.0;
            }

            return output;
        }

        private Dictionary<string, double> ComputeDerivatives(TowerOdeState state, SolverInput input)
        {
            var derivs = new Dictionary<string, double>();

            double gasFlow = input.GasMassFlowKgS / (input.GasDensityKgM3 * input.TowerAreaM2);  // m/s
            double liqFlow = input.LiquidMassFlowKgS / (input.LiquidDensityKgM3 * input.TowerAreaM2);

            double avgT = (state.GasTemperatureK + state.LiquidTemperatureK) / 2.0;
            double segmentHeatKW = 0.0;

            foreach (var code in input.PollutantCodes)
            {
                double conc = state.PollutantConcKgM3[code];
                var onda = input.OndaLookup(code, state.GasTemperatureK, avgT);
                double hLocal = input.HenrysLawFn(code, avgT);

                // Equilibrium: C_eq = p_i / H = y_i * P / H
                double equilibriumConc = Math.Max(1e-12, conc / hLocal);

                // Two-film theory: combine gas-side (kG) and liquid-side (kL)
                // resistances into one overall coefficient — 1/KGa = 1/kGa + H/kLa,
                // same combination CalculateNtuHtu uses. Previously this used
                // kL alone and ignored kG entirely, i.e. assumed zero gas-film
                // resistance — overstated absorption for gas-film-controlled
                // (poorly soluble) species.
                double kGa = Math.Max(onda.GasFilmCoeffKmolM2SPa * onda.WettedAreaM2M3, 1e-12);
                double kLa = Math.Max(onda.LiquidFilmCoeffMS * onda.WettedAreaM2M3, 1e-12);
                double overallKa = 1.0 / (1.0 / kGa + hLocal / kLa);

                // Mass transfer: dC/dz = -(KGa * Aw/V) * (C - C_eq) / v_gas
                double dCdz = -(overallKa * (conc - equilibriumConc)) / gasFlow;
                derivs[code] = dCdz;

                // Liquid-side mass balance: whatever leaves the gas phase
                // enters the liquid phase. dW/dz = absorbed(kg/s per m) / liquidMassFlow(kg/s)
                double absorbedKgSPerM = -dCdz * gasFlow * input.TowerAreaM2;
                derivs["_dW_" + code] = absorbedKgSPerM / Math.Max(input.LiquidMassFlowKgS, 1e-9);

                // Heat: absorbed kmol/s * ΔH_abs
                double absorbedKmolS = Math.Abs(dCdz) * gasFlow * input.TowerAreaM2 /
                                       (input.MolWeightFn(code) / 1000.0);
                double heatKW = absorbedKmolS * Math.Abs(input.HeatOfAbsorptionFn(code)) / 1000.0;
                segmentHeatKW += heatKW;
            }

            // Liquid composition rise (simplified: mass accumulation)
            double totalAbsorbed = 0;
            foreach (var code in input.PollutantCodes)
                totalAbsorbed += Math.Abs(derivs[code]) * input.MolWeightFn(code) / 1000.0;

            // Temperature changes
            double dTgas_dz = -segmentHeatKW / (input.GasMassFlowKgS * 1.05);  // Cp_gas ~1.05 kJ/kg·K
            double dTliq_dz = segmentHeatKW / (input.LiquidMassFlowKgS * 4.18); // Cp_water = 4.18

            derivs["_dTgas_dz"] = dTgas_dz;
            derivs["_dTliq_dz"] = dTliq_dz;

            return derivs;
        }

        private TowerOdeState StepRK45(TowerOdeState y, Dictionary<string, double> dydt,
                                       SolverInput input, double dz)
        {
            // k1
            var k1 = dydt;

            // k2: interpolate at z + dz/2
            var y2 = Interpolate(y, k1, dz / 2.0, input);
            var k2 = ComputeDerivatives(y2, input);

            // k3: same point
            var y3 = Interpolate(y, k2, dz / 2.0, input);
            var k3 = ComputeDerivatives(y3, input);

            // k4: at z + dz
            var y4 = Interpolate(y, k3, dz, input);
            var k4 = ComputeDerivatives(y4, input);

            // Combine slopes
            var yNew = new TowerOdeState { Height = y.Height + dz };

            foreach (var code in input.PollutantCodes)
            {
                yNew.PollutantConcKgM3[code] = y.PollutantConcKgM3[code] +
                    (dz / 6.0) * (k1[code] + 2 * k2[code] + 2 * k3[code] + k4[code]);

                string wKey = "_dW_" + code;
                yNew.LiquidMassFraction[code] = Math.Max(0, y.LiquidMassFraction[code] +
                    (dz / 6.0) * (k1[wKey] + 2 * k2[wKey] + 2 * k3[wKey] + k4[wKey]));
            }

            yNew.GasTemperatureK = y.GasTemperatureK +
                (dz / 6.0) * (k1["_dTgas_dz"] + 2 * k2["_dTgas_dz"] + 2 * k3["_dTgas_dz"] + k4["_dTgas_dz"]);
            yNew.LiquidTemperatureK = y.LiquidTemperatureK +
                (dz / 6.0) * (k1["_dTliq_dz"] + 2 * k2["_dTliq_dz"] + 2 * k3["_dTliq_dz"] + k4["_dTliq_dz"]);

            return yNew;
        }

        private TowerOdeState Interpolate(TowerOdeState y, Dictionary<string, double> dydt, double step, SolverInput input)
        {
            var result = new TowerOdeState { Height = y.Height + step };

            foreach (var code in input.PollutantCodes)
            {
                result.PollutantConcKgM3[code] = Math.Max(0, y.PollutantConcKgM3[code] + step * dydt[code]);
                string wKey = "_dW_" + code;
                result.LiquidMassFraction[code] = Math.Max(0, y.LiquidMassFraction[code] + step * dydt[wKey]);
            }

            result.GasTemperatureK = y.GasTemperatureK + step * dydt["_dTgas_dz"];
            result.LiquidTemperatureK = y.LiquidTemperatureK + step * dydt["_dTliq_dz"];

            return result;
        }
    }
}