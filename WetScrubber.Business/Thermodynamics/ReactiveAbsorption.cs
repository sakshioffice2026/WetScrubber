using System;

namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Reaction regime classification for reactive gas absorption.
    /// Distinguishes physical from chemical, fast from slow.
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
        /// <summary>e.g., "SO2", "H2SO3"</summary>
        public string SpeciesCode { get; set; }

        /// <summary>First dissociation constant (Ka1 or Kb1)</summary>
        public double FirstDissociationConstant { get; set; }

        /// <summary>Second dissociation constant (Ka2), if applicable</summary>
        public double? SecondDissociationConstant { get; set; }

        /// <summary>pKa1 at 25°C (log10(Ka1))</summary>
        public double Pka1_At25C { get; set; }

        /// <summary>pKa2 at 25°C, if polyprotic</summary>
        public double? Pka2_At25C { get; set; }

        /// <summary>Temperature coefficient for pKa (dpKa/dT, per Kelvin)</summary>
        public double TemperatureCoefficient { get; set; } = -0.002; // typical ~-0.002/K
    }

    /// <summary>
    /// pH calculation and acid-base equilibrium solver.
    /// Handles single and polyprotic acids/bases.
    /// </summary>
    public sealed class PhChemistry
    {
        public const double WaterKwAt25C = 1e-14; // Kw at 298.15 K

        public sealed class PhResult
        {
            public double pH { get; set; }
            public double pOH { get; set; }
            public double HydroniumConcentrationMolL { get; set; }  // [H+]
            public double HydroxideConcentrationMolL { get; set; }  // [OH-]
            public bool Converged { get; set; }
            public string EquilibriumModel { get; set; } // "weak_acid", "strong_base", "buffer", etc.
            public double ChargeBalance { get; set; }  // Should be ~0; deviation flags error
        }

        /// <summary>
        /// Calculate pH from acid concentration and Ka (weak acid case).
        /// </summary>
        public static PhResult WeakAcidPH(
            double acidConcentrationMolL,
            double Ka,
            double temperatureC = 25.0)
        {
            if (Ka <= 0)
                throw new ArgumentException("Ka must be positive");

            // HA ⇌ H+ + A−
            // [H+] ≈ √(Ka * C) for weak acid with C >> Ka
            double tempK = temperatureC + 273.15;
            double Kw = GetWaterIonProduct(tempK);

            double hConc;
            if (acidConcentrationMolL < Ka * 0.01)
            {
                // Very weak or very dilute: use full quadratic
                double discriminant = Ka * Ka + 4 * Ka * acidConcentrationMolL;
                hConc = (-Ka + Math.Sqrt(discriminant)) / 2.0;
            }
            else
            {
                // Standard: [H+] ≈ √(Ka * C)
                hConc = Math.Sqrt(Ka * acidConcentrationMolL);
            }

            hConc = Math.Max(hConc, 1e-14); // floor at water autoionization

            double pH = -Math.Log10(hConc);
            double pOH = Math.Log10(Kw) + Math.Log10(hConc);
            double ohConc = Kw / hConc;

            return new PhResult
            {
                pH = pH,
                pOH = pOH,
                HydroniumConcentrationMolL = hConc,
                HydroxideConcentrationMolL = ohConc,
                Converged = true,
                EquilibriumModel = "weak_acid",
                ChargeBalance = Math.Abs(hConc - ohConc) / Math.Max(hConc, 1e-12)
            };
        }

        /// <summary>
        /// Calculate pH from base concentration and Kb (weak base case).
        /// </summary>
        public static PhResult WeakBasePH(
            double baseConcentrationMolL,
            double Kb,
            double temperatureC = 25.0)
        {
            if (Kb <= 0)
                throw new ArgumentException("Kb must be positive");

            double tempK = temperatureC + 273.15;
            double Kw = GetWaterIonProduct(tempK);

            // B + H2O ⇌ BH+ + OH−
            // [OH−] ≈ √(Kb * C)
            double ohConc;
            if (baseConcentrationMolL < Kb * 0.01)
            {
                double discriminant = Kb * Kb + 4 * Kb * baseConcentrationMolL;
                ohConc = (-Kb + Math.Sqrt(discriminant)) / 2.0;
            }
            else
            {
                ohConc = Math.Sqrt(Kb * baseConcentrationMolL);
            }

            ohConc = Math.Max(ohConc, 1e-14);
            double hConc = Kw / ohConc;
            double pH = -Math.Log10(hConc);
            double pOH = -Math.Log10(ohConc);

            return new PhResult
            {
                pH = pH,
                pOH = pOH,
                HydroniumConcentrationMolL = hConc,
                HydroxideConcentrationMolL = ohConc,
                Converged = true,
                EquilibriumModel = "weak_base",
                ChargeBalance = Math.Abs(hConc - ohConc) / Math.Max(hConc, 1e-12)
            };
        }

        /// <summary>
        /// Calculate pH from strong acid concentration (complete dissociation).
        /// </summary>
        public static PhResult StrongAcidPH(
            double acidConcentrationMolL,
            double temperatureC = 25.0)
        {
            if (acidConcentrationMolL < 0)
                throw new ArgumentException("Concentration must be non-negative");

            double tempK = temperatureC + 273.15;
            double Kw = GetWaterIonProduct(tempK);

            // Fully dissociates: [H+] = C_acid
            double hConc = Math.Max(acidConcentrationMolL, 1e-14);
            double ohConc = Kw / hConc;
            double pH = -Math.Log10(hConc);
            double pOH = Math.Log10(Kw) + Math.Log10(hConc);

            return new PhResult
            {
                pH = pH,
                pOH = pOH,
                HydroniumConcentrationMolL = hConc,
                HydroxideConcentrationMolL = ohConc,
                Converged = true,
                EquilibriumModel = "strong_acid",
                ChargeBalance = 0
            };
        }

        /// <summary>
        /// Calculate pH from strong base concentration (complete dissociation).
        /// </summary>
        public static PhResult StrongBasePH(
            double baseConcentrationMolL,
            double temperatureC = 25.0)
        {
            if (baseConcentrationMolL < 0)
                throw new ArgumentException("Concentration must be non-negative");

            double tempK = temperatureC + 273.15;
            double Kw = GetWaterIonProduct(tempK);

            // Fully dissociates: [OH−] = C_base
            double ohConc = Math.Max(baseConcentrationMolL, 1e-14);
            double hConc = Kw / ohConc;
            double pH = -Math.Log10(hConc);
            double pOH = -Math.Log10(ohConc);

            return new PhResult
            {
                pH = pH,
                pOH = pOH,
                HydroniumConcentrationMolL = hConc,
                HydroxideConcentrationMolL = ohConc,
                Converged = true,
                EquilibriumModel = "strong_base",
                ChargeBalance = 0
            };
        }

        /// <summary>
        /// Get water ion product Kw at arbitrary temperature (used by pH solver).
        /// Approximation: ln(Kw) ≈ 48.1645 - 13445.93/T - 23.6521*ln(T)
        /// At 298.15 K → Kw = 1.008e-14 ✓
        /// </summary>
        public static double GetWaterIonProduct(double temperatureK)
        {
            if (temperatureK < 273.15 || temperatureK > 373.15)
                throw new ArgumentException("Temperature out of valid range (0–100°C)");

            // Coefficients for ln(Kw) = A - B/T - C*ln(T)
            double lnKw = 48.1645 - 13445.93 / temperatureK - 23.6521 * Math.Log(temperatureK);
            return Math.Exp(lnKw);
        }

        /// <summary>
        /// Temperature correction for pKa using simplified van't Hoff.
        /// pKa(T) ≈ pKa(25°C) + d(pKa)/dT * (T - 25)
        /// </summary>
        public static double GetTemperatureCorrectedPka(
            double pkaAt25C,
            double temperatureC,
            double temperatureCoefficientPerK = -0.002)
        {
            return pkaAt25C + temperatureCoefficientPerK * (temperatureC - 25.0);
        }
    }

    /// <summary>
    /// Enhancement factor for reactive absorption.
    /// Hatta number and reaction regime determination.
    /// </summary>
    public sealed class EnhancementFactor
    {
        /// <summary>Result of enhancement factor calculation</summary>
        public sealed class Result
        {
            /// <summary>Dimensionless Hatta number Ha = √(k*Cb/DAB) / (kL)</summary>
            public double HattaNumber { get; set; }

            /// <summary>Enhancement factor E (dimensionless, typically 1 to 100+)</summary>
            public double Factor { get; set; }

            /// <summary>Regime classification</summary>
            public ReactionRegime Regime { get; set; }

            /// <summary>Was reaction fast/instantaneous?</summary>
            public bool IsReactionLimited { get; set; }
        }

        /// <summary>
        /// Estimate enhancement factor based on Hatta number.
        /// 
        /// Ha < 0.1       → E ≈ 1 (slow reaction, physical absorption)
        /// Ha ~ 0.1-2     → E intermediate (transition regime)
        /// Ha > 2-3       → E >> 1 (instantaneous, interface-limited)
        /// 
        /// For instantaneous reaction: E ≈ 1 + (kL / k_rxn) * (Cbulk / Cboundary)
        /// </summary>
        public static Result CalculateEnhancementFactor(
            double reactionRateConstantS_Inv,     // k for n-th order (units depend on order)
            double bulkReagentConcentrationMolL,  // Cb or [OH-], [H+], etc.
            double liquidDiffusivityM2S,           // DAB liquid-phase
            double liquidFilmCoeffMS,              // kL
            int reactionOrder = 1)                 // typical first-order
        {
            if (liquidDiffusivityM2S <= 0)
                throw new ArgumentException("Diffusivity must be positive");
            if (liquidFilmCoeffMS <= 0)
                throw new ArgumentException("kL must be positive");

            // Hatta number (simplified for 1st order in A, bulk reagent reaction)
            // Ha = √(k1 * Cb / DAB) / kL
            // where k1 is effective rate constant, Cb is reagent concentration
            double kEffective = reactionRateConstantS_Inv * bulkReagentConcentrationMolL;
            double hattaNumber = Math.Sqrt(kEffective / liquidDiffusivityM2S) / liquidFilmCoeffMS;

            // Regime and enhancement factor
            double enhancementFactor;
            ReactionRegime regime;

            if (hattaNumber < 0.1)
            {
                enhancementFactor = 1.0; // no enhancement; physical absorption
                regime = ReactionRegime.PhysicalAbsorption;
            }
            else if (hattaNumber < 2.0)
            {
                // Intermediate: E ≈ Ha / tan(Ha) for instantaneous (or similar)
                // Simplified: E ≈ hattaNumber
                enhancementFactor = Math.Sqrt(1.0 + hattaNumber * hattaNumber);
                regime = ReactionRegime.FastReactionNearInterface;
            }
            else
            {
                // Fast/instantaneous: E ≈ Ha / tan(Ha) ≈ Ha for large Ha
                enhancementFactor = hattaNumber / Math.Tanh(hattaNumber);
                regime = ReactionRegime.VeryFastReactionInterface;
            }

            return new Result
            {
                HattaNumber = hattaNumber,
                Factor = Math.Min(enhancementFactor, 100.0), // cap unrealistic values
                Regime = regime,
                IsReactionLimited = hattaNumber > 2.0
            };
        }
    }

    /// <summary>
    /// Reaction stoichiometry and reagent consumption tracker.
    /// </summary>
    public sealed class ReactionStoichiometry
    {
        /// <summary>e.g., SO2 + 2OH− → SO3²− + H2O (stoich coeff: 1, 2, 1)</summary>
        public sealed class ReactantRatio
        {
            public string Pollutant { get; set; }
            public string Reagent { get; set; }
            public double PollutantCoeff { get; set; }    // e.g., 1 for SO2
            public double ReagentCoeff { get; set; }      // e.g., 2 for OH−
            public double ReagentConsumptionPerPollutant { get; set; } // Reagent / Pollutant
        }

        /// <summary>
        /// Calculate stoichiometric reagent demand.
        /// </summary>
        public static double GetReagentDemand(
            double pollutantAbsorbedKmolPerHr,
            double pollutantStoichCoeff,
            double reagentStoichCoeff)
        {
            if (pollutantStoichCoeff <= 0 || reagentStoichCoeff <= 0)
                throw new ArgumentException("Stoichiometric coefficients must be positive");

            return pollutantAbsorbedKmolPerHr * (reagentStoichCoeff / pollutantStoichCoeff);
        }

        /// <summary>
        /// Calculate excess reagent factor and utilization.
        /// If excess = 1.5, then 50% more than stoichiometric is supplied.
        /// </summary>
        public static double GetReagentUtilization(
            double reagentSuppliedKmolPerHr,
            double reagentDemandedStoichKmolPerHr)
        {
            if (reagentDemandedStoichKmolPerHr <= 0)
                return 0.0;
            return Math.Min(reagentSuppliedKmolPerHr / reagentDemandedStoichKmolPerHr, 1.0);
        }
    }
}