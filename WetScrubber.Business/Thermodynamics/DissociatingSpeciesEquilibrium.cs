using System;
using WetScrubber.Business.Exceptions;

namespace WetScrubber.Business.Thermodynamics
{
    public enum DissociationKind
    {
        None = 0,
        Acid = 1,
        Base = 2
    }

    public static class DissociatingSpeciesEquilibrium
    {
        public static double MolecularFraction(
            DissociationKind kind,
            double dissociationConstantMolPerL,
            double pH,
            double meanIonicActivityCoefficient = 1.0)
        {
            if (kind == DissociationKind.None) return 1.0;

            if (!(dissociationConstantMolPerL > 0))
                throw new ArgumentOutOfRangeException(nameof(dissociationConstantMolPerL));
            if (pH < 0.0 || pH > 14.0)
                throw new PropertyOutOfBoundsException("pH", pH, 0.0, 14.0);
            if (!(meanIonicActivityCoefficient > 0) || meanIonicActivityCoefficient > 1.5)
                throw new PropertyOutOfBoundsException("gamma_pm", meanIonicActivityCoefficient, 0.0, 1.5);

            double hPlus = Math.Pow(10.0, -pH);
            double ka = dissociationConstantMolPerL * meanIonicActivityCoefficient * meanIonicActivityCoefficient;

            // Acid HA <-> H+ + A- : [HA]/C_T = [H+] / ([H+] + g^2 Ka)
            // Base B (Ka of conjugate acid BH+) : [B]/C_T = g^2 Ka / ([H+] + g^2 Ka)
            return kind == DissociationKind.Acid
                ? hPlus / (hPlus + ka)
                : ka / (hPlus + ka);
        }

        public static double ApparentCgOverCl(
            double physicalCgOverCl,
            DissociationKind kind,
            double dissociationConstantMolPerL,
            double pH,
            double meanIonicActivityCoefficient = 1.0)
        {
            if (!(physicalCgOverCl > 0))
                throw new ArgumentOutOfRangeException(nameof(physicalCgOverCl));

            return physicalCgOverCl
                 * MolecularFraction(kind, dissociationConstantMolPerL, pH, meanIonicActivityCoefficient);
        }

        public static double EquilibriumGasConcKmolM3(
            double physicalCgOverCl,
            double totalDissolvedKmolM3,
            DissociationKind kind,
            double dissociationConstantMolPerL,
            double pH,
            double meanIonicActivityCoefficient = 1.0)
        {
            if (totalDissolvedKmolM3 < 0)
                throw new ArgumentOutOfRangeException(nameof(totalDissolvedKmolM3));

            return ApparentCgOverCl(physicalCgOverCl, kind, dissociationConstantMolPerL, pH, meanIonicActivityCoefficient)
                 * totalDissolvedKmolM3;
        }
    }
}
