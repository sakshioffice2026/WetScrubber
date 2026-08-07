using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Conservation
{
    /// <summary>
    /// Rigorous material balance tracking for all species and byproducts.
    /// Verifies: In = OutGas + Absorbed + Chemically Reacted + Product Species
    /// 
    /// Nothing should "disappear" from the calculation.
    /// </summary>
    public sealed class MaterialBalanceTracker
    {
        /// <summary>Balance result for a single species/pollutant</summary>
        public sealed class SpeciesBalance
        {
            /// <summary>Species code (SO2, SO3, H2SO4, H+, OH−, etc.)</summary>
            public string SpeciesCode { get; set; }

            /// <summary>Inlet molar flow, kmol/hr</summary>
            public double InletKmolPerHr { get; set; }

            /// <summary>Outlet gas molar flow, kmol/hr</summary>
            public double OutletGasKmolPerHr { get; set; }

            /// <summary>Absorbed into liquid, kmol/hr</summary>
            public double AbsorbedKmolPerHr { get; set; }

            /// <summary>Reacted (converted to products), kmol/hr</summary>
            public double ReactedKmolPerHr { get; set; }

            /// <summary>Any other disposal route (accumulation, etc.)</summary>
            public double OtherKmolPerHr { get; set; }

            /// <summary>Outlet in liquid phase (dissolved + reacted), kmol/hr</summary>
            public double OutletLiquidKmolPerHr { get; set; }

            /// <summary>Residual after balance (should be ~0)</summary>
            public double ClosureErrorKmolPerHr { get; set; }

            /// <summary>Fractional closure error (should be << 0.01)</summary>
            public double FractionalError { get; set; }

            /// <summary>Balance is acceptable if FractionalError < tolerance</summary>
            public bool IsBalanced(double toleranceFraction = 0.001)
                => Math.Abs(FractionalError) < toleranceFraction;
        }

        /// <summary>Overall mass balance result</summary>
        public sealed class OverallBalance
        {
            /// <summary>All species balances</summary>
            public IReadOnlyList<SpeciesBalance> Balances { get; set; }

            /// <summary>Is every species balanced?</summary>
            public bool AllSpeciesBalanced { get; set; }

            /// <summary>Worst fractional error across all species</summary>
            public double WorstFractionalError { get; set; }

            /// <summary>Summary for user display</summary>
            public string ClosureStatement { get; set; }

            /// <summary>Unmatched inlet that didn't exit as gas or reaction product</summary>
            public double UnaccountedKmolPerHr { get; set; }
        }

        /// <summary>
        /// Calculate single-species material balance.
        /// </summary>
        public static SpeciesBalance CalculateBalance(
            string speciesCode,
            double inletKmolPerHr,
            double outletGasKmolPerHr,
            double absorbedKmolPerHr,
            double reactedKmolPerHr,
            double otherKmolPerHr = 0.0)
        {
            if (inletKmolPerHr < 0)
                throw new ArgumentException($"Inlet flow cannot be negative for {speciesCode}");
            if (outletGasKmolPerHr < 0)
                throw new ArgumentException($"Outlet gas flow cannot be negative for {speciesCode}");

            double outletLiquid = absorbedKmolPerHr + reactedKmolPerHr + otherKmolPerHr;
            double totalOut = outletGasKmolPerHr + outletLiquid;

            double closureError = inletKmolPerHr - totalOut;
            double fractionalError = inletKmolPerHr > 1e-12
                ? closureError / inletKmolPerHr
                : (Math.Abs(closureError) > 1e-12 ? double.PositiveInfinity : 0.0);

            return new SpeciesBalance
            {
                SpeciesCode = speciesCode,
                InletKmolPerHr = inletKmolPerHr,
                OutletGasKmolPerHr = outletGasKmolPerHr,
                AbsorbedKmolPerHr = absorbedKmolPerHr,
                ReactedKmolPerHr = reactedKmolPerHr,
                OtherKmolPerHr = otherKmolPerHr,
                OutletLiquidKmolPerHr = outletLiquid,
                ClosureErrorKmolPerHr = closureError,
                FractionalError = fractionalError
            };
        }

        /// <summary>
        /// Aggregate balances for all species and produce overall statement.
        /// </summary>
        public static OverallBalance AggregateBalances(
            IReadOnlyList<SpeciesBalance> allBalances,
            double closureTolerance = 0.001)
        {
            if (allBalances == null || allBalances.Count == 0)
                throw new ArgumentException("Must have at least one species balance");

            double worstError = 0.0;
            bool allBalanced = true;
            double totalUnaccounted = 0.0;

            foreach (var balance in allBalances)
            {
                if (!balance.IsBalanced(closureTolerance))
                    allBalanced = false;

                double absError = Math.Abs(balance.FractionalError);
                if (absError > worstError)
                    worstError = absError;

                if (balance.ClosureErrorKmolPerHr > 1e-12)
                    totalUnaccounted += balance.ClosureErrorKmolPerHr;
            }

            string closureStatement;
            if (allBalanced)
            {
                closureStatement = "✓ Material balance closed successfully. All species accounted for.";
            }
            else if (worstError < 0.01)
            {
                closureStatement = $"⚠ Material balance within 1% tolerance (worst: {worstError * 100:F2}%). " +
                    $"Unaccounted: {totalUnaccounted:E3} kmol/hr. Check reaction products.";
            }
            else
            {
                closureStatement = $"✗ Material balance FAILED. Worst error: {worstError * 100:F2}%. " +
                    $"Unaccounted: {totalUnaccounted:E3} kmol/hr. Missing mass or incorrectly modeled reaction.";
            }

            return new OverallBalance
            {
                Balances = allBalances,
                AllSpeciesBalanced = allBalanced,
                WorstFractionalError = worstError,
                ClosureStatement = closureStatement,
                UnaccountedKmolPerHr = totalUnaccounted
            };
        }

        /// <summary>
        /// Flag any pollutant that disappears (inlet > outlet gas + absorption).
        /// </summary>
        public static IReadOnlyList<string> IdentifyMissingMass(
            IReadOnlyList<SpeciesBalance> balances,
            double toleranceFraction = 0.001)
        {
            var missing = new List<string>();

            foreach (var balance in balances)
            {
                if (!balance.IsBalanced(toleranceFraction)
                    && balance.ClosureErrorKmolPerHr > 1e-12)
                {
                    missing.Add($"{balance.SpeciesCode}: {balance.ClosureErrorKmolPerHr:E3} kmol/hr unaccounted");
                }
            }

            return missing;
        }
    }
}