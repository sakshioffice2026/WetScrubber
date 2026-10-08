using System;
using System.Collections.Generic;
using System.Linq;
using WetScrubber.Business.Exceptions;
using WetScrubber.Business.Thermodynamics;

namespace WetScrubber.Business.MassTransfer
{
    public sealed class TowerOdeState
    {
        public double Height { get; set; }                                          // m, z = 0 at gas inlet (bottom)
        public Dictionary<string, double> PollutantConcKgM3 { get; set; } = new();  // gas phase, local T and P
        public Dictionary<string, double> LiquidMassFraction { get; set; } = new(); // kg pollutant / kg liquid
        public double GasTemperatureK { get; set; }
        public double LiquidTemperatureK { get; set; }
        public double GasDensityKgM3 { get; set; }
        public double PressureKPa { get; set; }
        public double CumulativeHeatKW { get; set; }                                // absorbed between gas inlet and this node
    }

    /// <summary>
    /// Counter-current packed-tower solver. Gas enters at z = 0 and flows up,
    /// liquid enters at z = H and flows down. Gas pass (RK4, upward) and liquid
    /// pass (RK4, downward) are iterated until the profiles are self-consistent.
    /// Gas and liquid temperatures, properties and equilibrium are kept separate;
    /// gas density follows the local EOS at every step. Non-convergence throws.
    /// </summary>
    public sealed class RigourousTowerOdeSolver
    {
        private const double MaxStepM = 0.05;
        private const int MaxStepCount = 20000;
        private const int MaxPicardIterations = 300;
        private const double Relaxation = 0.7;
        private const double TolTemperatureK = 1e-4;
        private const double TolMolarRelative = 1e-6;
        private const double RKPaKmol = 8.314462;     // kPa*m3/(kmol*K)
        private const double RPaKmol = 8314.462;      // Pa*m3/(kmol*K)

        public sealed class SolverInput
        {
            public List<string> PollutantCodes { get; set; } = new();
            public Dictionary<string, double> InletConcKgM3 { get; set; } = new();          // gas, at inlet T and bottom P
            public Dictionary<string, double> InitialLiquidFraction { get; set; } = new();  // kg pollutant / kg liquid at z = H

            public double GasTemperatureK { get; set; }
            public double LiquidInletTemperatureK { get; set; }
            public double TowerHeightM { get; set; }
            public double TowerAreaM2 { get; set; }
            public double GasMassFlowKgS { get; set; }
            public double LiquidMassFlowKgS { get; set; }

            public double PressureBottomKPa { get; set; }
            public double TotalPressureDropKPa { get; set; }
            public double GasMolarMassKgKmol { get; set; }
            public double GasHeatCapacityKJKgK { get; set; }
            public double GasPrandtl { get; set; }

            public double PackingSpecificAreaM2M3 { get; set; }
            public double PackingNominalSizeM { get; set; }
            public double PackingCriticalSurfaceTensionNM { get; set; }

            // Gas-phase properties: evaluated at gas temperature
            public Func<double, double> GasViscosityPasFn { get; set; }                  // (Tg K)
            public Func<string, double, double, double> GasDiffusivityM2SFn { get; set; } // (code, Tg K, P kPa)

            // Liquid-phase properties: evaluated at liquid temperature
            public Func<double, double> LiquidDensityKgM3Fn { get; set; }                // (Tl K)
            public Func<double, double> LiquidViscosityPasFn { get; set; }               // (Tl K)
            public Func<double, double> LiquidSurfaceTensionNMFn { get; set; }           // (Tl K)
            public Func<double, double> LiquidHeatCapacityKJKgKFn { get; set; }          // (Tl K)
            public Func<string, double, double> LiquidDiffusivityM2SFn { get; set; }     // (code, Tl K)

            // Equilibrium: evaluated at liquid temperature
            public Func<string, double, double> HenryCgOverClFn { get; set; }            // (code, Tl K) -> dimensionless slope
            public Func<string, double, double, double, double> EquilibriumGasConcKmolM3Fn { get; set; } // (code, Tl K, Cl kmol/m3, P kPa)

            public Func<string, double> MolWeightFn { get; set; }                        // kg/kmol
            public Func<string, double> HeatOfAbsorptionFn { get; set; }                 // kJ/kmol (magnitude used)
        }

        public sealed class SolverOutput
        {
            public List<TowerOdeState> Profile { get; set; } = new();
            public Dictionary<string, double> OutletConcKgM3 { get; set; } = new();
            public Dictionary<string, double> OutletLiquidFraction { get; set; } = new();
            public double OutletGasTemperatureK { get; set; }
            public double OutletLiquidTemperatureK { get; set; }
            public Dictionary<string, double> RemovalEfficiency { get; set; } = new();     // percent, molar basis
            public bool Converged { get; set; }
            public int Iterations { get; set; }
            public double TotalHeatAbsorbedKW { get; set; }
        }

        private sealed class Context
        {
            public int M;
            public double CarrierKmolS;
            public PackingMassTransferInput Packing;
            public double[] Mw;
            public double[] Heat;
            public double[] NgIn;
            public double[] NlIn;
        }

        private sealed class RateResult
        {
            public double[] DNg;
            public double DTg;
            public double DTl;
            public double RhoG;
            public double PressureKPa;
        }

        public SolverOutput Solve(SolverInput inp)
        {
            Validate(inp);
            var ctx = BuildContext(inp);
            int m = ctx.M;

            int n = Math.Max(2, (int)Math.Ceiling(inp.TowerHeightM / MaxStepM));
            if (n > MaxStepCount)
                throw new PropertyOutOfBoundsException("TowerHeightM", inp.TowerHeightM, 0.0, MaxStepCount * MaxStepM);
            double dz = inp.TowerHeightM / n;

            var ng = NewMatrix(m, n + 1);
            var nl = NewMatrix(m, n + 1);
            var tg = new double[n + 1];
            var tl = new double[n + 1];
            for (int k = 0; k <= n; k++)
            {
                tg[k] = inp.GasTemperatureK;
                tl[k] = inp.LiquidInletTemperatureK;
                for (int i = 0; i < m; i++)
                {
                    ng[i][k] = ctx.NgIn[i];
                    nl[i][k] = ctx.NlIn[i];
                }
            }

            bool converged = false;
            int iter;
            double resT = 0.0, resN = 0.0;

            for (iter = 1; iter <= MaxPicardIterations; iter++)
            {
                // ---- Gas pass: upward from z = 0 ----
                var ngNew = NewMatrix(m, n + 1);
                var tgNew = new double[n + 1];
                for (int i = 0; i < m; i++) ngNew[i][0] = ctx.NgIn[i];
                tgNew[0] = inp.GasTemperatureK;

                for (int k = 0; k < n; k++)
                {
                    double z = k * dz;
                    var s = new double[m + 1];
                    for (int i = 0; i < m; i++) s[i] = ngNew[i][k];
                    s[m] = tgNew[k];

                    var k1 = GasDeriv(inp, ctx, z, s, LiquidAt(nl, tl, k, 0.0, m));
                    var k2 = GasDeriv(inp, ctx, z + 0.5 * dz, Axpy(s, 0.5 * dz, k1), LiquidAt(nl, tl, k, 0.5, m));
                    var k3 = GasDeriv(inp, ctx, z + 0.5 * dz, Axpy(s, 0.5 * dz, k2), LiquidAt(nl, tl, k, 0.5, m));
                    var k4 = GasDeriv(inp, ctx, z + dz, Axpy(s, dz, k3), LiquidAt(nl, tl, k, 1.0, m));

                    for (int i = 0; i < m; i++)
                    {
                        double v = s[i] + (dz / 6.0) * (k1[i] + 2.0 * k2[i] + 2.0 * k3[i] + k4[i]);
                        ngNew[i][k + 1] = Math.Max(0.0, v);
                    }
                    tgNew[k + 1] = s[m] + (dz / 6.0) * (k1[m] + 2.0 * k2[m] + 2.0 * k3[m] + k4[m]);
                }

                // ---- Liquid pass: downward from z = H ----
                var nlNew = NewMatrix(m, n + 1);
                for (int i = 0; i < m; i++)
                    for (int k = 0; k <= n; k++)
                        nlNew[i][k] = ctx.NlIn[i] + (ngNew[i][k] - ngNew[i][n]);

                var tlNew = new double[n + 1];
                tlNew[n] = inp.LiquidInletTemperatureK;
                double h = -dz;
                for (int k = n; k >= 1; k--)
                {
                    double t = tlNew[k];
                    double a1 = LiqDeriv(inp, ctx, ngNew, nlNew, tgNew, k, 0.0, dz, t);
                    double a2 = LiqDeriv(inp, ctx, ngNew, nlNew, tgNew, k, 0.5, dz, t + 0.5 * h * a1);
                    double a3 = LiqDeriv(inp, ctx, ngNew, nlNew, tgNew, k, 0.5, dz, t + 0.5 * h * a2);
                    double a4 = LiqDeriv(inp, ctx, ngNew, nlNew, tgNew, k, 1.0, dz, t + h * a3);
                    tlNew[k - 1] = t + (h / 6.0) * (a1 + 2.0 * a2 + 2.0 * a3 + a4);
                }

                // ---- Residuals and relaxation ----
                resT = 0.0;
                resN = 0.0;
                for (int k = 0; k <= n; k++)
                    resT = Math.Max(resT, Math.Abs(tlNew[k] - tl[k]));
                for (int i = 0; i < m; i++)
                {
                    double scale = Math.Max(ctx.NgIn[i], 1e-30);
                    for (int k = 0; k <= n; k++)
                        resN = Math.Max(resN, Math.Abs(nlNew[i][k] - nl[i][k]) / scale);
                }

                for (int k = 0; k <= n; k++)
                    tl[k] += Relaxation * (tlNew[k] - tl[k]);
                for (int i = 0; i < m; i++)
                    for (int k = 0; k <= n; k++)
                        nl[i][k] += Relaxation * (nlNew[i][k] - nl[i][k]);

                ng = ngNew;
                tg = tgNew;

                if (resT < TolTemperatureK && resN < TolMolarRelative)
                {
                    converged = true;
                    break;
                }
            }

            if (!converged)
                throw new SolverNonConvergenceException(
                    MaxPicardIterations,
                    Math.Max(resT / TolTemperatureK, resN / TolMolarRelative));

            return BuildOutput(inp, ctx, n, dz, ng, nl, tg, tl, iter);
        }

        // ------------------------------------------------------------------
        // Rate model
        // ------------------------------------------------------------------
        private static RateResult Rates(
            SolverInput inp, Context ctx, double z,
            double tg, double tl, double[] ngv, double[] nlv)
        {
            int m = ctx.M;

            double pKPa = inp.PressureBottomKPa - inp.TotalPressureDropKPa * z / inp.TowerHeightM;
            if (!(pKPa > 0.0))
                throw new PropertyOutOfBoundsException("PressureKPa", pKPa, 0.0, double.MaxValue);
            if (!(tg > 0.0) || double.IsNaN(tg))
                throw new PropertyOutOfBoundsException("GasTemperatureK", tg, 0.0, double.MaxValue);

            double rhoG = pKPa * inp.GasMolarMassKgKmol / (RKPaKmol * tg);
            double muG = inp.GasViscosityPasFn(tg);
            double rhoL = inp.LiquidDensityKgM3Fn(tl);
            double muL = inp.LiquidViscosityPasFn(tl);
            double sigmaL = inp.LiquidSurfaceTensionNMFn(tl);
            double cpL = inp.LiquidHeatCapacityKJKgKFn(tl);
            if (!(rhoL > 0.0) || !(cpL > 0.0))
                throw new PropertyOutOfBoundsException("LiquidProperties", rhoL, 0.0, double.MaxValue);

            double sumN = ctx.CarrierKmolS;
            for (int i = 0; i < m; i++) sumN += Math.Max(0.0, ngv[i]);

            double liquidVolFlowM3S = inp.LiquidMassFlowKgS / rhoL;
            double gasMolarConc = pKPa / (RKPaKmol * tg);

            var dng = new double[m];
            double qAbsKW = 0.0;
            double aW = 0.0;
            double heatGroup = 0.0;

            for (int i = 0; i < m; i++)
            {
                string code = inp.PollutantCodes[i];
                double dG = inp.GasDiffusivityM2SFn(code, tg, pKPa);
                double dL = inp.LiquidDiffusivityM2SFn(code, tl);

                var onda = OndaMassTransferCorrelation.Calculate(
                    ctx.Packing,
                    new GasPhaseProperties(tg, rhoG, muG, dG),
                    new LiquidPhaseProperties(tl, rhoL, muL, dL, sigmaL));

                double kc = onda.GasFilmCoeffKmolM2SPa * RPaKmol * tg;   // m/s, concentration basis
                double kL = onda.LiquidFilmCoeffMS;                      // m/s

                double hCgCl = inp.HenryCgOverClFn(code, tl);
                if (!(hCgCl > 0.0) || double.IsInfinity(hCgCl))
                    throw new PropertyOutOfBoundsException("HenryCgOverCl", hCgCl, 0.0, double.MaxValue);

                // 1/K = 1/kc + H(Cg/Cl)/kL
                double kOverall = 1.0 / (1.0 / kc + hCgCl / kL);

                double y = Math.Max(0.0, ngv[i]) / sumN;
                double cg = y * gasMolarConc;
                double cl = Math.Max(0.0, nlv[i]) / liquidVolFlowM3S;
                double cgEq = inp.EquilibriumGasConcKmolM3Fn(code, tl, cl, pKPa);
                if (double.IsNaN(cgEq) || cgEq < 0.0)
                    throw new PropertyOutOfBoundsException("EquilibriumGasConcKmolM3", cgEq, 0.0, double.MaxValue);

                double dn = -kOverall * onda.WettedAreaM2M3 * inp.TowerAreaM2 * (cg - cgEq);  // kmol/s per m
                dng[i] = dn;
                qAbsKW += -dn * ctx.Heat[i];

                aW = onda.WettedAreaM2M3;
                double scG = muG / (rhoG * dG);
                heatGroup += kc * Math.Pow(scG, 2.0 / 3.0);
            }

            // Chilton-Colburn analogy for gas-liquid sensible heat transfer
            heatGroup /= m;
            double hG = rhoG * inp.GasHeatCapacityKJKgK * heatGroup / Math.Pow(inp.GasPrandtl, 2.0 / 3.0); // kW/(m2*K)
            double qSensKW = hG * aW * inp.TowerAreaM2 * (tg - tl);                                          // kW per m

            return new RateResult
            {
                DNg = dng,
                DTg = -qSensKW / (inp.GasMassFlowKgS * inp.GasHeatCapacityKJKgK),
                DTl = -(qSensKW + qAbsKW) / (inp.LiquidMassFlowKgS * cpL),
                RhoG = rhoG,
                PressureKPa = pKPa
            };
        }

        private static double[] GasDeriv(
            SolverInput inp, Context ctx, double z, double[] s, (double Tl, double[] Nl) liq)
        {
            int m = ctx.M;
            var ngv = new double[m];
            Array.Copy(s, ngv, m);
            var r = Rates(inp, ctx, z, s[m], liq.Tl, ngv, liq.Nl);

            var d = new double[m + 1];
            Array.Copy(r.DNg, d, m);
            d[m] = r.DTg;
            return d;
        }

        private static double LiqDeriv(
            SolverInput inp, Context ctx,
            double[][] ngNew, double[][] nlNew, double[] tgNew,
            int k, double f, double dz, double tl)
        {
            int m = ctx.M;
            double z = (k - f) * dz;
            double tgv = Lerp(tgNew[k], tgNew[k - 1], f);
            var ngv = new double[m];
            var nlv = new double[m];
            for (int i = 0; i < m; i++)
            {
                ngv[i] = Lerp(ngNew[i][k], ngNew[i][k - 1], f);
                nlv[i] = Lerp(nlNew[i][k], nlNew[i][k - 1], f);
            }
            return Rates(inp, ctx, z, tgv, tl, ngv, nlv).DTl;
        }

        // ------------------------------------------------------------------
        // Output
        // ------------------------------------------------------------------
        private static SolverOutput BuildOutput(
            SolverInput inp, Context ctx, int n, double dz,
            double[][] ng, double[][] nl, double[] tg, double[] tl, int iterations)
        {
            int m = ctx.M;
            var output = new SolverOutput { Converged = true, Iterations = iterations };

            for (int k = 0; k <= n; k++)
            {
                double z = k * dz;
                double pKPa = inp.PressureBottomKPa - inp.TotalPressureDropKPa * z / inp.TowerHeightM;

                double sumN = ctx.CarrierKmolS;
                for (int i = 0; i < m; i++) sumN += ng[i][k];

                var node = new TowerOdeState
                {
                    Height = z,
                    GasTemperatureK = tg[k],
                    LiquidTemperatureK = tl[k],
                    PressureKPa = pKPa,
                    GasDensityKgM3 = pKPa * inp.GasMolarMassKgKmol / (RKPaKmol * tg[k])
                };

                double heat = 0.0;
                for (int i = 0; i < m; i++)
                {
                    string code = inp.PollutantCodes[i];
                    double y = ng[i][k] / sumN;
                    node.PollutantConcKgM3[code] = y * pKPa / (RKPaKmol * tg[k]) * ctx.Mw[i];
                    node.LiquidMassFraction[code] = nl[i][k] * ctx.Mw[i] / inp.LiquidMassFlowKgS;
                    heat += (ctx.NgIn[i] - ng[i][k]) * ctx.Heat[i];
                }
                node.CumulativeHeatKW = heat;
                output.Profile.Add(node);
            }

            var top = output.Profile[n];
            var bottom = output.Profile[0];

            output.OutletGasTemperatureK = top.GasTemperatureK;
            output.OutletLiquidTemperatureK = bottom.LiquidTemperatureK;
            output.TotalHeatAbsorbedKW = top.CumulativeHeatKW;

            for (int i = 0; i < m; i++)
            {
                string code = inp.PollutantCodes[i];
                output.OutletConcKgM3[code] = top.PollutantConcKgM3[code];
                output.OutletLiquidFraction[code] = bottom.LiquidMassFraction[code];
                output.RemovalEfficiency[code] = ctx.NgIn[i] > 0.0
                    ? (ctx.NgIn[i] - ng[i][n]) / ctx.NgIn[i] * 100.0
                    : 0.0;
            }

            return output;
        }

        // ------------------------------------------------------------------
        // Setup and helpers
        // ------------------------------------------------------------------
        private static Context BuildContext(SolverInput inp)
        {
            int m = inp.PollutantCodes.Count;

            var packing = new PackingMassTransferInput
            {
                SpecificAreaM2M3 = inp.PackingSpecificAreaM2M3,
                NominalSizeM = inp.PackingNominalSizeM,
                CriticalSurfaceTensionNM = inp.PackingCriticalSurfaceTensionNM,
                LiquidSurfaceTensionNM = inp.LiquidSurfaceTensionNMFn(inp.LiquidInletTemperatureK),
                TowerAreaM2 = inp.TowerAreaM2,
                GasMassFlowKgS = inp.GasMassFlowKgS,
                LiquidMassFlowKgS = inp.LiquidMassFlowKgS
            };
            OndaMassTransferCorrelation.RequireComplete(packing);

            double totalGasKmolS = inp.GasMassFlowKgS / inp.GasMolarMassKgKmol;
            double inletMolarDensity = inp.PressureBottomKPa / (RKPaKmol * inp.GasTemperatureK);

            var ctx = new Context
            {
                M = m,
                Packing = packing,
                Mw = new double[m],
                Heat = new double[m],
                NgIn = new double[m],
                NlIn = new double[m]
            };

            double sumY = 0.0;
            for (int i = 0; i < m; i++)
            {
                string code = inp.PollutantCodes[i];
                double mw = inp.MolWeightFn(code);
                if (!(mw > 0.0))
                    throw new PropertyOutOfBoundsException("MolecularWeight:" + code, mw, 0.0, double.MaxValue);

                double y = inp.InletConcKgM3[code] / mw / inletMolarDensity;
                if (y < 0.0)
                    throw new PropertyOutOfBoundsException("InletMoleFraction:" + code, y, 0.0, 1.0);

                sumY += y;
                ctx.Mw[i] = mw;
                ctx.Heat[i] = Math.Abs(inp.HeatOfAbsorptionFn(code));
                ctx.NgIn[i] = y * totalGasKmolS;
                ctx.NlIn[i] = inp.InitialLiquidFraction[code] * inp.LiquidMassFlowKgS / mw;
            }

            if (!(sumY < 1.0))
                throw new PropertyOutOfBoundsException("SumInletMoleFraction", sumY, 0.0, 1.0);

            ctx.CarrierKmolS = totalGasKmolS * (1.0 - sumY);
            return ctx;
        }

        private static void Validate(SolverInput inp)
        {
            if (inp == null) throw new ArgumentNullException(nameof(inp));
            if (inp.PollutantCodes == null || inp.PollutantCodes.Count == 0)
                throw new ArgumentException("At least one pollutant is required.");

            foreach (var code in inp.PollutantCodes)
            {
                if (!inp.InletConcKgM3.ContainsKey(code))
                    throw new ArgumentException($"InletConcKgM3 missing for '{code}'.");
                if (!inp.InitialLiquidFraction.ContainsKey(code))
                    throw new ArgumentException($"InitialLiquidFraction missing for '{code}'.");
            }

            RequirePositive(nameof(inp.GasTemperatureK), inp.GasTemperatureK);
            RequirePositive(nameof(inp.LiquidInletTemperatureK), inp.LiquidInletTemperatureK);
            RequirePositive(nameof(inp.TowerHeightM), inp.TowerHeightM);
            RequirePositive(nameof(inp.TowerAreaM2), inp.TowerAreaM2);
            RequirePositive(nameof(inp.GasMassFlowKgS), inp.GasMassFlowKgS);
            RequirePositive(nameof(inp.LiquidMassFlowKgS), inp.LiquidMassFlowKgS);
            RequirePositive(nameof(inp.PressureBottomKPa), inp.PressureBottomKPa);
            RequirePositive(nameof(inp.GasMolarMassKgKmol), inp.GasMolarMassKgKmol);
            RequirePositive(nameof(inp.GasHeatCapacityKJKgK), inp.GasHeatCapacityKJKgK);
            RequirePositive(nameof(inp.GasPrandtl), inp.GasPrandtl);

            if (inp.TotalPressureDropKPa < 0.0 || inp.TotalPressureDropKPa >= inp.PressureBottomKPa)
                throw new PropertyOutOfBoundsException(
                    nameof(inp.TotalPressureDropKPa), inp.TotalPressureDropKPa, 0.0, inp.PressureBottomKPa);

            if (inp.GasViscosityPasFn == null || inp.GasDiffusivityM2SFn == null
                || inp.LiquidDensityKgM3Fn == null || inp.LiquidViscosityPasFn == null
                || inp.LiquidSurfaceTensionNMFn == null || inp.LiquidHeatCapacityKJKgKFn == null
                || inp.LiquidDiffusivityM2SFn == null || inp.HenryCgOverClFn == null
                || inp.EquilibriumGasConcKmolM3Fn == null || inp.MolWeightFn == null
                || inp.HeatOfAbsorptionFn == null)
                throw new ArgumentException("All property, equilibrium and thermochemical delegates are mandatory.");
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new PropertyOutOfBoundsException(name, value, 0.0, double.MaxValue);
        }

        private static double[][] NewMatrix(int rows, int cols)
        {
            var a = new double[rows][];
            for (int i = 0; i < rows; i++) a[i] = new double[cols];
            return a;
        }

        private static (double Tl, double[] Nl) LiquidAt(double[][] nl, double[] tl, int k, double f, int m)
        {
            var nlv = new double[m];
            for (int i = 0; i < m; i++) nlv[i] = Lerp(nl[i][k], nl[i][k + 1], f);
            return (Lerp(tl[k], tl[k + 1], f), nlv);
        }

        private static double Lerp(double a, double b, double f) => a + (b - a) * f;

        private static double[] Axpy(double[] s, double a, double[] k)
        {
            var r = new double[s.Length];
            for (int i = 0; i < s.Length; i++) r[i] = s[i] + a * k[i];
            return r;
        }
    }
}