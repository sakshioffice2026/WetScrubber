using System;

namespace WetScrubber.Business.MassTransfer
{
    /// <summary>
    /// Wilke-Chang (1955) estimation of a dilute solute's liquid-phase
    /// diffusion coefficient:
    ///   D_AB [cm2/s] = 7.4e-8 * sqrt(phi * M_B) * T / (mu_B[cP] * V_A^0.6)
    /// A = solute, B = solvent.
    /// </summary>
    public static class WilkeChangDiffusivity
    {
        public static double Calculate(
            double soluteMolarVolumeCm3Mol,
            double solventAssociationFactor,
            double solventMolecularWeightGMol,
            double solventViscosityCp,
            double temperatureK)
        {
            if (soluteMolarVolumeCm3Mol <= 0)
                throw new ArgumentException("Solute molar volume must be positive.", nameof(soluteMolarVolumeCm3Mol));
            if (solventViscosityCp <= 0)
                throw new ArgumentException("Solvent viscosity must be positive.", nameof(solventViscosityCp));

            double dAbCm2S = 7.4e-8
                * Math.Sqrt(solventAssociationFactor * solventMolecularWeightGMol)
                * temperatureK
                / (solventViscosityCp * Math.Pow(soluteMolarVolumeCm3Mol, 0.6));

            return dAbCm2S * 1e-4; // cm2/s -> m2/s
        }
    }
}