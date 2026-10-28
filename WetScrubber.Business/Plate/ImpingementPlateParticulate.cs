using System;
using WetScrubber.Business.Common;
using WetScrubber.Business.Particulate;

namespace WetScrubber.Business.Plate
{
    /// <summary>
    /// Screening inputs for inertial particle impaction on an impingement plate.
    /// SI units are used throughout. The collection correlation is an unvalidated
    /// screening approximation and must not be treated as a vendor-validated design method.
    /// </summary>
    public sealed class PlateParticulateInput
    {
        public double GasFlowM3s { get; set; }
        public double GasDensityKgM3 { get; set; }
        public double GasViscosityPas { get; set; }
        public double ParticleDensityKgM3 { get; set; }
        public double CharacteristicLengthM { get; set; }
        public double CharacteristicGasVelocityMs { get; set; }
        public ParticleSizeDistribution? Distribution { get; set; }

        /// <summary>Stokes number at 50% collection; screening default, not validated.</summary>
        public double StokesNumberAt50Percent { get; set; } = 0.5;

        /// <summary>Apply Cunningham slip correction to particle relaxation time.</summary>
        public bool ApplyCunninghamSlip { get; set; }
        public double TemperatureK { get; set; } = 293.15;
        public double PressurePa { get; set; } = 101325.0;
        public double GasMolarMassKgMol { get; set; } = CunninghamSlip.AirMolarMassKgMol;

        public double? TargetEfficiency { get; set; }
    }

    /// <summary>Particulate collection result for an impingement-plate bank.</summary>
    public sealed class PlateParticulateResult : ModelResultBase
    {
        public PlateParticulateResult()
        {
            Scope = ModelScope.ParticulateOnly;
            ValidationStatus = ModelValidationStatus.NotYetValidated;
        }

        public double GasFlowM3s { get; set; }
        public double GasDensityKgM3 { get; set; }
        public double ParticleDensityKgM3 { get; set; }
        public double CharacteristicLengthM { get; set; }
        public double CharacteristicGasVelocityMs { get; set; }
        public double StokesNumberAt50Percent { get; set; }
        public double? CutDiameterM { get; set; }
        public double OverallEfficiency { get; set; }
        public double OverallEfficiencyPercent => 100.0 * OverallEfficiency;
        public double OverallPenetration => 1.0 - OverallEfficiency;
        public System.Collections.Generic.List<FractionalEfficiencyPoint> FractionalEfficiency { get; set; } = new();
        public double? TargetEfficiency { get; set; }
        public bool? TargetMet { get; set; }
    }

    /// <summary>
    /// Inertial impaction screening model.
    /// Stk = rho_p * d_p^2 * Cc * U / (18 * mu_g * L)
    /// eta(d) = Stk / (Stk + Stk50)
    /// Overall efficiency is mass-weighted using the shared FractionalEfficiency helper.
    /// The Stk50 default and curve shape are unvalidated placeholders requiring benchmark confirmation.
    /// </summary>
    public static class ImpingementPlateParticulate
    {
        public const double DefaultStokesNumberAt50Percent = 0.5;

        public static PlateParticulateResult Evaluate(PlateParticulateInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            RequirePositive(nameof(input.GasFlowM3s), input.GasFlowM3s);
            RequirePositive(nameof(input.GasDensityKgM3), input.GasDensityKgM3);
            RequirePositive(nameof(input.GasViscosityPas), input.GasViscosityPas);
            RequirePositive(nameof(input.ParticleDensityKgM3), input.ParticleDensityKgM3);
            RequirePositive(nameof(input.CharacteristicLengthM), input.CharacteristicLengthM);
            RequirePositive(nameof(input.CharacteristicGasVelocityMs), input.CharacteristicGasVelocityMs);
            RequirePositive(nameof(input.StokesNumberAt50Percent), input.StokesNumberAt50Percent);

            if (input.Distribution == null)
                throw new ArgumentException("A particle size distribution is required.", nameof(input));

            if (input.ApplyCunninghamSlip)
            {
                RequirePositive(nameof(input.TemperatureK), input.TemperatureK);
                RequirePositive(nameof(input.PressurePa), input.PressurePa);
                RequirePositive(nameof(input.GasMolarMassKgMol), input.GasMolarMassKgMol);
            }

            if (input.TargetEfficiency.HasValue &&
                (double.IsNaN(input.TargetEfficiency.Value) ||
                 double.IsInfinity(input.TargetEfficiency.Value) ||
                 input.TargetEfficiency.Value < 0.0 || input.TargetEfficiency.Value > 1.0))
                throw new ArgumentOutOfRangeException(nameof(input.TargetEfficiency), "Target efficiency must be between 0 and 1.");

            double lambda = input.ApplyCunninghamSlip
                ? CunninghamSlip.MeanFreePathM(input.GasViscosityPas, input.TemperatureK,
                    input.PressurePa, input.GasMolarMassKgMol)
                : 0.0;

            Func<double, double> efficiencyAtDiameter = diameterM =>
            {
                double slip = input.ApplyCunninghamSlip
                    ? CunninghamSlip.Factor(diameterM, lambda)
                    : 1.0;
                double stokes = input.ParticleDensityKgM3 * diameterM * diameterM * slip *
                    input.CharacteristicGasVelocityMs /
                    (18.0 * input.GasViscosityPas * input.CharacteristicLengthM);
                return stokes / (stokes + input.StokesNumberAt50Percent);
            };

            FractionalEfficiencyResult fractional =
                FractionalEfficiency.Evaluate(input.Distribution, efficiencyAtDiameter);

            double? cutDiameterM = FractionalEfficiency.FindCutDiameterM(
                efficiencyAtDiameter, 0.5,
                Math.Max(1.0e-12, input.Distribution.MinDiameterM / 100.0),
                Math.Min(1.0, input.Distribution.MaxDiameterM * 100.0));

            var result = new PlateParticulateResult
            {
                GasFlowM3s = input.GasFlowM3s,
                GasDensityKgM3 = input.GasDensityKgM3,
                ParticleDensityKgM3 = input.ParticleDensityKgM3,
                CharacteristicLengthM = input.CharacteristicLengthM,
                CharacteristicGasVelocityMs = input.CharacteristicGasVelocityMs,
                StokesNumberAt50Percent = input.StokesNumberAt50Percent,
                CutDiameterM = cutDiameterM,
                OverallEfficiency = fractional.OverallEfficiency,
                FractionalEfficiency = fractional.Points,
                TargetEfficiency = input.TargetEfficiency,
                TargetMet = input.TargetEfficiency.HasValue
                    ? fractional.OverallEfficiency >= input.TargetEfficiency.Value
                    : (bool?)null
            };

            foreach (string warning in fractional.Warnings)
                result.AddWarning("PLATE_PSD", warning);

            result.AddWarning("PLATE_IMPACTION_SCREENING",
                "Particle collection uses an unvalidated Stokes-number screening curve. " +
                "Confirm the Stokes50 parameter and correlation against impingement-plate benchmark or vendor data.");

            if (input.ParticleDensityKgM3 <= input.GasDensityKgM3)
                result.AddWarning("PLATE_DENSITY_CONDITION",
                    "Particle density does not exceed gas density; verify that the selected impaction model is appropriate.");

            if (!cutDiameterM.HasValue)
                result.AddWarning("PLATE_CUT_DIAMETER_UNBRACKETED",
                    "The 50% cut diameter could not be bracketed within the searched diameter range.");

            return result;
        }

        private static void RequirePositive(string name, double value)
        {
            if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        }
    }
}
