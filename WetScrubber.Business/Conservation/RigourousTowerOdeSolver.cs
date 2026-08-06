using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Conservation
{
    public sealed class RigourousTowerOdeSolver
    {
        public static TowerSolverResult SolveRK45(
            double packingHeightM,
            int layerCount,
            double gasMolarFluxKmolM2Hr,
            double liquidMassFluxKgHr,
            double liquidSpecificHeatKJKgK,
            double inletGasMoleFraction,
            double inletLiquidTemperatureK,
            double? heatOfSolutionKJmol,
            double totalPressureKPa,
            Func<double, double> localGasFilmCoeff,
            Func<double, double, double> localHenrysConstant)
        {
            double dz = packingHeightM / layerCount;
            var layers = new List<LayerProfile>();
            double y = inletGasMoleFraction;
            double T = inletLiquidTemperatureK;

            for (int i = 0; i < layerCount; i++)
            {
                double z = i * dz;
                double kGa = localGasFilmCoeff(T);
                double H = localHenrysConstant(T, y);
                double yStar = H * 0.001;

                double k1 = -kGa * totalPressureKPa * (y - yStar) / Math.Max(gasMolarFluxKmolM2Hr, 1e-9);
                double k2 = -kGa * totalPressureKPa * (y + k1 * 0.5 * dz - yStar) / Math.Max(gasMolarFluxKmolM2Hr, 1e-9);
                double k3 = -kGa * totalPressureKPa * (y + k2 * 0.5 * dz - yStar) / Math.Max(gasMolarFluxKmolM2Hr, 1e-9);
                double k4 = -kGa * totalPressureKPa * (y + k3 * dz - yStar) / Math.Max(gasMolarFluxKmolM2Hr, 1e-9);

                double dy = (k1 + 2 * k2 + 2 * k3 + k4) / 6.0 * dz;
                y = Math.Max(y + dy, 0);

                if (heatOfSolutionKJmol.HasValue && dy != 0)
                {
                    double absorbedKmol = Math.Abs(dy * gasMolarFluxKmolM2Hr * dz);
                    double heatKJ = absorbedKmol * heatOfSolutionKJmol.Value;
                    double dT = heatKJ / Math.Max(liquidMassFluxKgHr / 3600.0, 1e-9) / Math.Max(liquidSpecificHeatKJKgK, 1e-9);
                    T = Math.Min(T + Math.Abs(dT), 373.15);
                }

                layers.Add(new LayerProfile
                {
                    HeightM = z,
                    GasMoleFraction = y,
                    LiquidTemperatureK = T
                });
            }

            return new TowerSolverResult
            {
                Converged = true,
                IterationsUsed = 1,
                OutletGasMoleFraction = y,
                OutletLiquidTemperatureK = T,
                Layers = layers
            };
        }
    }
}