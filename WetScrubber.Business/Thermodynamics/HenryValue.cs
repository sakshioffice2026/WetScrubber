using System;

namespace WetScrubber.Business.Thermodynamics
{
    public enum HenryConvention
    {
        CgOverCl = 1,
        ClOverCg = 2,
        PaM3PerMol = 3,
        MolPerM3Pa = 4,
        YOverX = 5,
        XOverY = 6
    }

    public readonly struct HenryValue
    {
        private const double RJmolK = 8.314462;

        public double StandardValue { get; }
        public HenryConvention Convention { get; }

        public HenryValue(double value, HenryConvention convention)
        {
            if (!(value > 0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Henry value must be finite and positive.");
            if (!Enum.IsDefined(typeof(HenryConvention), convention))
                throw new ArgumentException("Henry convention must be explicitly declared.", nameof(convention));

            StandardValue = value;
            Convention = convention;
        }

        public double ToCgOverCl(double temperatureK, double pressureKPa = 0.0,
                                 double liquidDensityKgM3 = 0.0, double liquidMolarMassKgKmol = 0.0)
        {
            if (Convention == 0)
                throw new InvalidOperationException("HenryValue has no declared convention.");
            if (!(temperatureK > 0))
                throw new ArgumentOutOfRangeException(nameof(temperatureK));

            switch (Convention)
            {
                case HenryConvention.CgOverCl:
                    return StandardValue;

                case HenryConvention.ClOverCg:
                    return 1.0 / StandardValue;

                case HenryConvention.PaM3PerMol:
                    return StandardValue / (RJmolK * temperatureK);

                case HenryConvention.MolPerM3Pa:
                    return 1.0 / (StandardValue * RJmolK * temperatureK);

                case HenryConvention.YOverX:
                    return StandardValue * PressureOverRtCtot(temperatureK, pressureKPa, liquidDensityKgM3, liquidMolarMassKgKmol);

                case HenryConvention.XOverY:
                    return 1.0 / (StandardValue * PressureOverRtCtot(temperatureK, pressureKPa, liquidDensityKgM3, liquidMolarMassKgKmol));

                default:
                    throw new InvalidOperationException("Unsupported Henry convention.");
            }
        }

        public double ToClOverCg(double temperatureK, double pressureKPa = 0.0,
                                 double liquidDensityKgM3 = 0.0, double liquidMolarMassKgKmol = 0.0)
            => 1.0 / ToCgOverCl(temperatureK, pressureKPa, liquidDensityKgM3, liquidMolarMassKgKmol);

        private static double PressureOverRtCtot(double temperatureK, double pressureKPa,
                                                 double liquidDensityKgM3, double liquidMolarMassKgKmol)
        {
            if (!(pressureKPa > 0) || !(liquidDensityKgM3 > 0) || !(liquidMolarMassKgKmol > 0))
                throw new ArgumentException(
                    "Mole-fraction Henry conventions require pressure, liquid density and liquid molar mass.");

            double totalLiquidMolPerM3 = 1000.0 * liquidDensityKgM3 / liquidMolarMassKgKmol;
            return pressureKPa * 1000.0 / (RJmolK * temperatureK * totalLiquidMolPerM3);
        }
    }
}
