using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Thermodynamics
{
    public sealed class LiquidComponent
    {
        public string Name { get; set; } = "";
        public double MassFraction { get; set; }
        public double MolarMassKgKmol { get; set; }
    }

    public static class LiquidMixtureProperties
    {
        public static double MixtureMolarMassKgKmol(IReadOnlyList<LiquidComponent> components)
        {
            if (components == null || components.Count == 0)
                throw new ArgumentException("Liquid composition is required.", nameof(components));

            double sumW = 0.0;
            double sumWOverM = 0.0;
            foreach (var c in components)
            {
                if (c.MassFraction < 0 || !(c.MolarMassKgKmol > 0))
                    throw new ArgumentException($"Invalid liquid component '{c.Name}'.");
                sumW += c.MassFraction;
                sumWOverM += c.MassFraction / c.MolarMassKgKmol;
            }

            if (Math.Abs(sumW - 1.0) > 1e-4)
                throw new ArgumentException($"Liquid mass fractions must sum to 1.0; got {sumW}.");

            return 1.0 / sumWOverM;
        }

        public static double MolarFlowKmolPerHr(
            double volumetricFlowM3PerHr,
            double liquidDensityKgM3,
            double mixtureMolarMassKgKmol)
        {
            if (!(volumetricFlowM3PerHr > 0) || !(liquidDensityKgM3 > 0) || !(mixtureMolarMassKgKmol > 0))
                throw new ArgumentException(
                    "Volumetric flow, liquid density and mixture molar mass must all be positive.");

            return volumetricFlowM3PerHr * liquidDensityKgM3 / mixtureMolarMassKgKmol;
        }
    }
}
