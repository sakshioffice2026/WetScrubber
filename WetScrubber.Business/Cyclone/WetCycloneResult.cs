using System.Collections.Generic;
using WetScrubber.Business.Common;
using WetScrubber.Business.Particulate;

namespace WetScrubber.Business.Cyclone
{
    /// <summary>
    /// Output of the wet cyclone calculation.
    /// Scope is particulate only; gas absorption is not modeled (see GasAbsorptionNotModeled).
    /// </summary>
    public sealed class WetCycloneResult : ModelResultBase
    {
        public WetCycloneResult()
        {
            Scope = ModelScope.ParticulateOnly;
            ValidationStatus = ModelValidationStatus.NotYetValidated;
        }

        // Geometry
        public WetCycloneGeometry? Geometry { get; set; }
        public int ParallelUnits { get; set; }
        public double BodyDiameterM { get; set; }
        public double InletAreaM2 { get; set; }
        public double GasOutletDiameterM { get; set; }
        public double TotalHeightM { get; set; }
        public double EffectiveTurns { get; set; }

        // Operating point
        public double GasFlowM3s { get; set; }
        public double InletVelocityMs { get; set; }
        public double GasDensityKgM3 { get; set; }
        public double LiquidToGasRatioLM3 { get; set; }

        // Collection
        public double CutDiameterM { get; set; }                  // d50
        public double OverallEfficiency { get; set; }             // 0-1
        public double OverallEfficiencyPercent => OverallEfficiency * 100.0;
        public double? CrossCheckOverallEfficiency { get; set; }  // Leith-Licht, 0-1
        public double? CrossCheckDeviation { get; set; }          // absolute difference, 0-1
        public List<FractionalEfficiencyPoint> FractionalEfficiency { get; set; } = new();

        // Hydraulics and power
        public double PressureDropPa { get; set; }
        public double DryPressureDropPa { get; set; }
        public double FanPowerW { get; set; }
        public double WaterRateM3s { get; set; }
        public double WaterRateLMin => WaterRateM3s * 1000.0 * 60.0;
        public double PumpPowerW { get; set; }

        // Target check
        public double? TargetEfficiency { get; set; }             // 0-1
        public bool? TargetMet { get; set; }
    }
}
