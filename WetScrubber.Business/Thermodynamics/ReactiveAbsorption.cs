using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Reaction regime classification for reactive gas absorption.
    /// </summary>
    public enum ReactionRegime
    {
        PhysicalAbsorption,         // No reaction; mass transfer by diffusion only
        SlowReactionLiquidBulk,     // Reaction slow; occurs in bulk liquid
        FastReactionNearInterface,  // Reaction fast; occurs at gas-liquid interface
        InstantReactionLiquidBulk,  // Instantaneous reaction; limited by reagent availability
        VeryFastReactionInterface   // Extremely fast; diffusion-limited even with excess reagent
    }

    /// <summary>
    /// Acid-base equilibrium constants and dissociation data.
    /// </summary>
    public sealed class AcidBasePair
    {
        public string SpeciesCode { get; set; }

        /// <summary>First dissociation constant (Ka1 or Kb1)</summary>
        public double FirstDissociationConstant { get; set; }

        /// <summary>Second dissociation constant (Ka2), if applicable</summary>
        public double? SecondDissociationConstant { get; set; }

        /// <summary>pKa1 at 25 C, pKa1 = -log10(Ka1)</summary>
        public double Pka1_At25C { get; set; }

        /// <summary>pKa2 at 25 C, if polyprotic</summary>
        public double? Pka2_At25C { get; set; }

        /// <summary>Temperature coefficient for pKa (dpKa/dT, per Kelvin)</summary>
        public double TemperatureCoefficient { get; set; } = -0.002;
    }

    /// <summary>
    /// pH calculation and acid-base equilibrium solver (monoprotic, with water autoionization).
    /// </summary>
    public sealed class PhChemistry
    {
        public const double WaterKwAt25C = 1e-14;

        public sealed class PhResult
        {
            public double pH { get; set; }
            public double pOH { get; set; }
            public double HydroniumConcentrationMolL { get; set; }
            public double HydroxideConcentrationMolL { get; set; }
            public bool Converged { get; set; }
            public string EquilibriumModel { get; set; }
            /// <summary>Relative charge-balance residual; should be ~0.</summary>
            public double ChargeBalance { get; set; }
        }

        /// <summary>
        /// Weak acid HA : charge balance [H+] = [A-] + Kw/[H+],  [A-] = Ka*C/(Ka+[H+]).
        /// </summary>
        public static PhResult WeakAcidPH(double acidConcentrationMolL, double Ka, double temperatureC = 25.0)
        {
            if (Ka <= 0) throw new ArgumentException("Ka must be positive");
            if (acidConcentrationMolL < 0) throw new ArgumentException("Concentration must be non-negative");

            double kw = GetWaterIonProduct(temperatureC + 273.15);
            double h = SolveMonoprotic(acidConcentrationMolL, Ka, kw, out bool converged);
            return BuildResult(h, kw / h, kw, converged, "weak_acid", acidConcentrationMolL, Ka);
        }

        /// <summary>
        /// Weak base B : charge balance [OH-] = [BH+] + Kw/[OH-],  [BH+] = Kb*C/(Kb+[OH-]).
        /// </summary>
        public static PhResult WeakBasePH(double baseConcentrationMolL, double Kb, double temperatureC = 25.0)
        {
            if (Kb <= 0) throw new ArgumentException("Kb must be positive");
            if (baseConcentrationMolL < 0) throw new ArgumentException("Concentration must be non-negative");

            double kw = GetWaterIonProduct(temperatureC + 273.15);
            double oh = SolveMonoprotic(baseConcentrationMolL, Kb, kw, out bool converged);
            return BuildResult(kw / oh, oh, kw, converged, "weak_base", baseConcentrationMolL, Kb);
        }

        /// <summary>
        /// Strong acid, complete dissociation: [H+] = (C + sqrt(C^2 + 4Kw)) / 2.
        /// </summary>
        public static PhResult StrongAcidPH(double acidConcentrationMolL, double temperatureC = 25.0)
        {
            if (acidConcentrationMolL < 0) throw new ArgumentException("Concentration must be non-negative");

            double kw = GetWaterIonProduct(temperatureC + 273.15);
            double h = 0.5 * (acidConcentrationMolL + Math.Sqrt(acidConcentrationMolL * acidConcentrationMolL + 4.0 * kw));
            return BuildResult(h, kw / h, kw, true, "strong_acid", 0.0, 0.0);
        }

        /// <summary>
        /// Strong base, complete dissociation: [OH-] = (C + sqrt(C^2 + 4Kw)) / 2.
        /// </summary>
        public static PhResult StrongBasePH(double baseConcentrationMolL, double temperatureC = 25.0)
        {
            if (baseConcentrationMolL < 0) throw new ArgumentException("Concentration must be non-negative");

            double kw = GetWaterIonProduct(temperatureC + 273.15);
            double oh = 0.5 * (baseConcentrationMolL + Math.Sqrt(baseConcentrationMolL * baseConcentrationMolL + 4.0 * kw));
            return BuildResult(kw / oh, oh, kw, true, "strong_base", 0.0, 0.0);
        }

        /// <summary>
        /// Ion product of water, 0-100 C:
        /// ln(Kw) = 148.9802 - 13847.26/T - 23.6521*ln(T)   (Kw in mol^2/L^2, T in K)
        /// 298.15 K -> 1.0e-14, 373.15 K -> ~5.6e-13.
        /// </summary>
        public static double GetWaterIonProduct(double temperatureK)
        {
            if (temperatureK < 273.15 || temperatureK > 373.15)
                throw new ArgumentException("Temperature out of valid range (0-100 C)");

            double lnKw = 148.9802 - 13847.26 / temperatureK - 23.6521 * Math.Log(temperatureK);
            return Math.Exp(lnKw);
        }

        /// <summary>
        /// Linear pKa temperature correction: pKa(T) = pKa(25 C) + dpKa/dT * (T - 25).
        /// </summary>
        public static double GetTemperatureCorrectedPka(
            double pkaAt25C,
            double temperatureC,
            double temperatureCoefficientPerK = -0.002)
        {
            return pkaAt25C + temperatureCoefficientPerK * (temperatureC - 25.0);
        }

        // Solves  f(x) = x - Kw/x - K*C/(K + x) = 0  for x = [H+] (acid) or [OH-] (base).
        // f is strictly increasing in x, so bisection in log-space is robust.
        private static double SolveMonoprotic(double c, double k, double kw, out bool converged)
        {
            double lo = 1e-16;
            double hi = Math.Max(1.0, c + 1.0);
            double mid = Math.Sqrt(lo * hi);
            converged = false;

            for (int i = 0; i < 300; i++)
            {
                mid = Math.Sqrt(lo * hi);
                double f = mid - kw / mid - k * c / (k + mid);
                if (f > 0) hi = mid; else lo = mid;

                if (hi / lo < 1.0 + 1e-12)
                {
                    converged = true;
                    break;
                }
            }

            return mid;
        }

        private static PhResult BuildResult(
            double h, double oh, double kw, bool converged, string model, double c, double k)
        {
            double balance;
            if (model == "weak_acid")
                balance = Math.Abs(h - oh - k * c / (k + h)) / Math.Max(h, 1e-16);
            else if (model == "weak_base")
                balance = Math.Abs(oh - h - k * c / (k + oh)) / Math.Max(oh, 1e-16);
            else
                balance = 0.0;

            return new PhResult
            {
                pH = -Math.Log10(h),
                pOH = -Math.Log10(oh),
                HydroniumConcentrationMolL = h,
                HydroxideConcentrationMolL = oh,
                Converged = converged,
                EquilibriumModel = model,
                ChargeBalance = balance
            };
        }
    }

    /// <summary>
    /// Enhancement factor for reactive absorption (film theory).
    /// </summary>
    public sealed class EnhancementFactor
    {
        public sealed class Result
        {
            /// <summary>Hatta number  Ha = sqrt(k * Cb^n * D_A) / kL  (dimensionless)</summary>
            public double HattaNumber { get; set; }

            /// <summary>Enhancement factor E (dimensionless, >= 1)</summary>
            public double Factor { get; set; }

            public ReactionRegime Regime { get; set; }

            public bool IsReactionLimited { get; set; }
        }

        public const double MaxFactor = 1000.0;

        /// <summary>
        /// Pseudo-first-order enhancement, rate = k * C_A * C_B^n.
        ///   k1 = k * Cb^n            [1/s]
        ///   Ha = sqrt(k1 * D_A) / kL [-]
        ///   E  = Ha / tanh(Ha)       [-]  (-> 1 as Ha -> 0, -> Ha for Ha >> 1)
        /// Units: Cb in kmol/m3 (= mol/L); k in (m3/kmol)^n / s; D_A in m2/s; kL in m/s.
        /// Cap E additionally with the instantaneous-reaction limit
        /// (see ReactiveEnhancementService).
        /// </summary>
        public static Result CalculateEnhancementFactor(
            double reactionRateConstant,
            double bulkReagentConcentrationMolL,
            double liquidDiffusivityM2S,
            double liquidFilmCoeffMS,
            int reactionOrder = 1)
        {
            if (liquidDiffusivityM2S <= 0)
                throw new ArgumentException("Diffusivity must be positive");
            if (liquidFilmCoeffMS <= 0)
                throw new ArgumentException("kL must be positive");
            if (reactionRateConstant < 0 || bulkReagentConcentrationMolL < 0)
                throw new ArgumentException("Rate constant and concentration must be non-negative");

            double k1 = reactionRateConstant * Math.Pow(bulkReagentConcentrationMolL, Math.Max(reactionOrder, 0));
            double ha = Math.Sqrt(k1 * liquidDiffusivityM2S) / liquidFilmCoeffMS;

            double e;
            if (ha < 1e-6)
                e = 1.0;
            else if (ha > 20.0)
                e = ha;
            else
                e = ha / Math.Tanh(ha);

            ReactionRegime regime;
            if (ha < 0.1)
                regime = ReactionRegime.PhysicalAbsorption;
            else if (ha < 0.3)
                regime = ReactionRegime.SlowReactionLiquidBulk;
            else if (ha < 3.0)
                regime = ReactionRegime.FastReactionNearInterface;
            else
                regime = ReactionRegime.VeryFastReactionInterface;

            return new Result
            {
                HattaNumber = ha,
                Factor = Math.Min(Math.Max(e, 1.0), MaxFactor),
                Regime = regime,
                IsReactionLimited = ha > 3.0
            };
        }
    }

    /// <summary>
    /// Reaction stoichiometry and reagent consumption.
    /// </summary>
    public sealed class ReactionStoichiometry
    {
        /// <summary>e.g. SO2 + 2NaOH -> Na2SO3 + H2O  (pollutant coeff 1, reagent coeff 2)</summary>
        public sealed class ReactantRatio
        {
            public string Pollutant { get; set; }
            public string Reagent { get; set; }
            public double PollutantCoeff { get; set; }
            public double ReagentCoeff { get; set; }
            public double ReagentConsumptionPerPollutant { get; set; }
        }

        /// <summary>Stoichiometric reagent demand, kmol/h.</summary>
        public static double GetReagentDemand(
            double pollutantAbsorbedKmolPerHr,
            double pollutantStoichCoeff,
            double reagentStoichCoeff)
        {
            if (pollutantStoichCoeff <= 0 || reagentStoichCoeff <= 0)
                throw new ArgumentException("Stoichiometric coefficients must be positive");

            return pollutantAbsorbedKmolPerHr * (reagentStoichCoeff / pollutantStoichCoeff);
        }

        /// <summary>Excess factor = supplied / stoichiometric demand (1.5 = 50 % excess).</summary>
        public static double GetExcessFactor(
            double reagentSuppliedKmolPerHr,
            double reagentDemandedStoichKmolPerHr)
        {
            if (reagentDemandedStoichKmolPerHr <= 0)
                return 0.0;
            return reagentSuppliedKmolPerHr / reagentDemandedStoichKmolPerHr;
        }

        /// <summary>Utilization = demand / supplied, limited to [0, 1].</summary>
        public static double GetReagentUtilization(
            double reagentSuppliedKmolPerHr,
            double reagentDemandedStoichKmolPerHr)
        {
            if (reagentSuppliedKmolPerHr <= 0)
                return 0.0;
            return Math.Min(Math.Max(reagentDemandedStoichKmolPerHr / reagentSuppliedKmolPerHr, 0.0), 1.0);
        }
    }
}