using System;

namespace WetScrubber.Business.Cyclone
{
    /// <summary>
    /// Dimension ratios (relative to body diameter D) for a tangential-inlet cyclone.
    /// </summary>
    public sealed class CycloneRatios
    {
        public string Name { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;

        public double InletHeight { get; init; }      // a / D
        public double InletWidth { get; init; }       // b / D
        public double GasOutletDiameter { get; init; } // De / D
        public double OutletLength { get; init; }     // S / D (vortex finder)
        public double CylinderHeight { get; init; }   // h / D
        public double ConeHeight { get; init; }       // z / D
        public double DustOutletDiameter { get; init; } // B / D

        public double TotalHeight => CylinderHeight + ConeHeight;

        /// <summary>
        /// Lapple (1951) conventional cyclone: a/D = 0.5, b/D = 0.25, De/D = 0.5,
        /// S/D = 0.125, h/D = 1.5, z/D = 2.5, B/D = 0.25.
        /// Status: standard textbook ratios (Cooper and Alley); confirm against the cited edition.
        /// </summary>
        public static CycloneRatios Lapple { get; } = new CycloneRatios
        {
            Name = "Lapple",
            Source = "Lapple (1951); Cooper and Alley, Air Pollution Control: A Design Approach",
            InletHeight = 0.5,
            InletWidth = 0.25,
            GasOutletDiameter = 0.5,
            OutletLength = 0.125,
            CylinderHeight = 1.5,
            ConeHeight = 2.5,
            DustOutletDiameter = 0.25
        };
    }

    /// <summary>
    /// Resolved cyclone geometry (metres) for a given body diameter.
    /// </summary>
    public sealed class WetCycloneGeometry
    {
        public CycloneRatios Ratios { get; }

        public int ParallelUnits { get; }
        public double BodyDiameterM { get; }
        public double InletHeightM { get; }
        public double InletWidthM { get; }
        public double GasOutletDiameterM { get; }
        public double OutletLengthM { get; }
        public double CylinderHeightM { get; }
        public double ConeHeightM { get; }
        public double DustOutletDiameterM { get; }

        public double InletAreaM2 => InletHeightM * InletWidthM;
        public double TotalHeightM => CylinderHeightM + ConeHeightM;
        public double GasOutletAreaM2 => Math.PI * GasOutletDiameterM * GasOutletDiameterM / 4.0;

        /// <summary>
        /// Natural-length-based effective number of turns (Lapple):
        /// Ne = (1 / a) * (h + z / 2). For Lapple ratios this gives 5.5.
        /// </summary>
        public double EffectiveTurns => (CylinderHeightM + ConeHeightM / 2.0) / InletHeightM;

        /// <summary>
        /// Inlet-to-outlet area ratio used by pressure-drop correlations.
        /// </summary>
        public double InletToOutletAreaRatio => InletAreaM2 / GasOutletAreaM2;

        private WetCycloneGeometry(double bodyDiameterM, int parallelUnits, CycloneRatios ratios)
        {
            Ratios = ratios;
            ParallelUnits = parallelUnits;
            BodyDiameterM = bodyDiameterM;
            InletHeightM = ratios.InletHeight * bodyDiameterM;
            InletWidthM = ratios.InletWidth * bodyDiameterM;
            GasOutletDiameterM = ratios.GasOutletDiameter * bodyDiameterM;
            OutletLengthM = ratios.OutletLength * bodyDiameterM;
            CylinderHeightM = ratios.CylinderHeight * bodyDiameterM;
            ConeHeightM = ratios.ConeHeight * bodyDiameterM;
            DustOutletDiameterM = ratios.DustOutletDiameter * bodyDiameterM;
        }

        /// <summary>
        /// Sizes the body from total gas flow and the target inlet velocity.
        /// Per-unit flow = Q / N;  inlet area = Qunit / vi;  D = sqrt(A / (a/D * b/D)).
        /// </summary>
        public static WetCycloneGeometry FromFlowAndInletVelocity(
            double gasFlowM3s,
            double inletVelocityMs,
            int parallelUnits = 1,
            CycloneRatios? ratios = null)
        {
            if (!(gasFlowM3s > 0) || double.IsInfinity(gasFlowM3s))
                throw new ArgumentOutOfRangeException(nameof(gasFlowM3s), "Gas flow must be positive and finite (m3/s).");
            if (!(inletVelocityMs > 0) || double.IsInfinity(inletVelocityMs))
                throw new ArgumentOutOfRangeException(nameof(inletVelocityMs), "Inlet velocity must be positive and finite (m/s).");
            if (parallelUnits < 1)
                throw new ArgumentOutOfRangeException(nameof(parallelUnits), "At least one cyclone is required.");

            ratios ??= CycloneRatios.Lapple;

            double flowPerUnit = gasFlowM3s / parallelUnits;
            double inletArea = flowPerUnit / inletVelocityMs;
            double areaRatio = ratios.InletHeight * ratios.InletWidth;
            double diameter = Math.Sqrt(inletArea / areaRatio);

            return new WetCycloneGeometry(diameter, parallelUnits, ratios);
        }

        /// <summary>
        /// Builds geometry from a given body diameter.
        /// </summary>
        public static WetCycloneGeometry FromBodyDiameter(
            double bodyDiameterM,
            int parallelUnits = 1,
            CycloneRatios? ratios = null)
        {
            if (!(bodyDiameterM > 0) || double.IsInfinity(bodyDiameterM))
                throw new ArgumentOutOfRangeException(nameof(bodyDiameterM), "Body diameter must be positive and finite (m).");
            if (parallelUnits < 1)
                throw new ArgumentOutOfRangeException(nameof(parallelUnits), "At least one cyclone is required.");

            return new WetCycloneGeometry(bodyDiameterM, parallelUnits, ratios ?? CycloneRatios.Lapple);
        }

        /// <summary>
        /// Gas inlet velocity (m/s) for a given total gas flow through this geometry.
        /// </summary>
        public double InletVelocityMs(double gasFlowM3s)
        {
            if (!(gasFlowM3s > 0) || double.IsInfinity(gasFlowM3s))
                throw new ArgumentOutOfRangeException(nameof(gasFlowM3s), "Gas flow must be positive and finite (m3/s).");

            return gasFlowM3s / ParallelUnits / InletAreaM2;
        }
    }
}
