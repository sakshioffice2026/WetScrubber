using System;

namespace WetScrubber.Services
{
    public static class PackedTowerEfficiencyCalculator
    {
        public static double AtHeight(double packingHeightM, double htuM, double absorptionFactor)
        {
            if (packingHeightM <= 0 || htuM <= 0)
                return 0.0;

            double ntu = packingHeightM / htuM;
            double a = absorptionFactor;
            double inletToOutlet;

            if (Math.Abs(a - 1.0) < 0.01)
            {
                inletToOutlet = 1.0 + ntu;
            }
            else
            {
                double exponent = ntu * (a - 1.0) / a;
                exponent = Math.Min(exponent, 700.0);
                inletToOutlet = (Math.Exp(exponent) - 1.0 / a) / (1.0 - 1.0 / a);
            }

            if (double.IsNaN(inletToOutlet) || inletToOutlet < 1.0)
                inletToOutlet = 1.0;

            double efficiency = (1.0 - 1.0 / inletToOutlet) * 100.0;
            return Math.Min(Math.Max(efficiency, 0.0), 99.99);
        }
    }
}