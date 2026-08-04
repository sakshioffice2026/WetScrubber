using System.Text;
using WetScrubber.Business.Diagnostics;
using WetScrubber.Database;
using WetScrubber.Database.Enums;

namespace WetScrubber.Business.Reports
{
    /// <summary>
    /// Builds a deterministic engineering report.
    ///
    /// IMPORTANT
    /// ----------
    /// This class NEVER invents numbers.
    /// Everything comes from the calculation engine/database.
    ///
    /// AI may rewrite this report later,
    /// but AI never replaces any value.
    ///
    /// The DESIGN DIAGNOSTICS section is likewise never AI-generated: the
    /// findings come from DesignDiagnosticsEngine, a fixed rule table
    /// evaluated against the numbers above it. AI is only ever allowed to
    /// reword the sentences this class already wrote.
    /// </summary>
    public class TemplateNarrativeBuilder : ITemplateNarrativeBuilder
    {
        private readonly IDesignDiagnosticsEngine _diagnosticsEngine;

        public TemplateNarrativeBuilder(IDesignDiagnosticsEngine diagnosticsEngine)
        {
            _diagnosticsEngine = diagnosticsEngine;
        }

        public string Build(ScrubberDesign design)
        {
            ArgumentNullException.ThrowIfNull(design);

            if (design.Geometry == null)
                throw new InvalidOperationException("Geometry not found.");

            if (design.GasStream == null)
                throw new InvalidOperationException("Gas Stream not found.");

            if (design.LiquidSpec == null)
                throw new InvalidOperationException("Liquid specification not found.");

            var sb = new StringBuilder();

            BuildHeader(sb, design);

            BuildGasSection(sb, design);

            BuildLiquidSection(sb, design);

            BuildGeometrySection(sb, design);

            BuildDiagnosticsSection(sb, design);

            BuildEngineeringSummary(sb);

            BuildDisclaimer(sb);

            return sb.ToString();
        }

        private static void BuildHeader(
            StringBuilder sb,
            ScrubberDesign design)
        {
            sb.AppendLine("ENGINEERING DESIGN SUMMARY");
            sb.AppendLine("--------------------------------------------");

            sb.AppendLine($"Design Name : {design.DesignName}");
            sb.AppendLine($"Scrubber Type : {design.ScrubberType}");
            sb.AppendLine($"Shell Material : {design.ShellMaterial}");
            sb.AppendLine($"Internal Material : {design.InternalMaterial}");

            sb.AppendLine();
        }

        private static void BuildGasSection(
            StringBuilder sb,
            ScrubberDesign design)
        {
            var gas = design.GasStream;

            sb.AppendLine("GAS STREAM");
            sb.AppendLine("--------------------------------------------");

            //
            // Replace property names below
            // with your existing GasStream properties.
            //

            sb.AppendLine($"Actual Gas Flow : {gas.ActualFlowRate} m³/h");
            sb.AppendLine($"Normal Gas Flow : {gas.NormalFlowRate} Nm³/h");
            sb.AppendLine($"Temperature : {gas.InletTemperature}");
            sb.AppendLine($"Pressure : {gas.InletPressure}");

            sb.AppendLine();
        }

        private static void BuildLiquidSection(
    StringBuilder sb,
    ScrubberDesign design)
        {
            var liquid = design.LiquidSpec;

            sb.AppendLine("SCRUBBING LIQUID");
            sb.AppendLine("--------------------------------------------");

            var liquidName =
                liquid.ScrubbingLiquid?.DisplayName
                ?? "Unknown";

            sb.AppendLine($"Scrubbing Liquid : {liquidName}");

            sb.AppendLine($"Chemical Formula : {liquid.ScrubbingLiquid?.Formula}");

            sb.AppendLine($"Concentration : {liquid.Concentration:N2}");

            sb.AppendLine($"Operating pH : {liquid.pH:N2}");

            sb.AppendLine($"Temperature : {liquid.Temperature:N2} °C");

            sb.AppendLine($"Density : {liquid.Density:N2} kg/m³");

            sb.AppendLine($"Viscosity : {liquid.Viscosity:N2}");

            sb.AppendLine($"Liquid/Gas Ratio : {liquid.LiquidToGasRatio:N2}");

            sb.AppendLine();
        }

        private static void BuildGeometrySection(
            StringBuilder sb,
            ScrubberDesign design)
        {
            var geometry = design.Geometry;

            sb.AppendLine("CALCULATED DESIGN");
            sb.AppendLine("--------------------------------------------");

            //
            // Replace with your Geometry properties.
            //

            sb.AppendLine($"Tower Diameter : {geometry.TowerDiameter}");

            sb.AppendLine($"Tower Height : {geometry.TowerHeight}");

            sb.AppendLine($"Packing Height : {geometry.PackingHeight}");

            sb.AppendLine($"Pressure Drop : {geometry.PressureDrop}");

            sb.AppendLine($"Removal Efficiency : {geometry.RemovalEfficiency}");

            sb.AppendLine();
        }

        private void BuildDiagnosticsSection(
            StringBuilder sb,
            ScrubberDesign design)
        {
            var geometry = design.Geometry!;
            var pollutant = design.GasStream?.Pollutants?.FirstOrDefault();

            var metrics = new DesignMetrics
            {
                ScrubberType = ToScrubberTypeLabel(design.ScrubberType),
                AbsorptionFactor = geometry.AbsorptionFactor,
                ActualLGRatio = geometry.ActualLGRatio,
                MinLGRatio = geometry.MinLGRatio,
                PressureDrop = geometry.PressureDrop,
                GasVelocity = geometry.GasVelocity,
                PackingHeight = geometry.PackingHeight,
                RemovalEfficiency = geometry.RemovalEfficiency,
                TargetRemovalEfficiency = pollutant?.TargetRemovalEfficiency
            };

            var findings = _diagnosticsEngine.Evaluate(metrics);

            sb.AppendLine("DESIGN DIAGNOSTICS");
            sb.AppendLine("--------------------------------------------");

            if (findings.Count == 0)
            {
                sb.AppendLine("No findings — the design fell within all checked reference ranges.");
            }
            else
            {
                foreach (var finding in findings)
                {
                    sb.AppendLine($"- {finding.Symptom} {finding.Diagnosis} Recommendation: {finding.Recommendation}");
                }
            }

            sb.AppendLine();
        }

        private static string ToScrubberTypeLabel(ScrubberType type) => type switch
        {
            ScrubberType.PackedTower => "Packed Tower",
            ScrubberType.VenturiScrubber => "Venturi Scrubber",
            ScrubberType.SprayTower => "Spray Tower",
            _ => type.ToString()
        };

        private static void BuildEngineeringSummary(
            StringBuilder sb)
        {
            sb.AppendLine("ENGINEERING SUMMARY");
            sb.AppendLine("--------------------------------------------");

            sb.AppendLine("The equipment dimensions shown above were");
            sb.AppendLine("calculated using the deterministic wet");
            sb.AppendLine("scrubber calculation engine.");

            sb.AppendLine();

            sb.AppendLine("All engineering values originate from");
            sb.AppendLine("validated mathematical models and");
            sb.AppendLine("industry design correlations.");

            sb.AppendLine();
        }

        private static void BuildDisclaimer(
            StringBuilder sb)
        {
            sb.AppendLine("--------------------------------------------");

            sb.AppendLine("This report contains deterministic");
            sb.AppendLine("engineering calculations.");

            sb.AppendLine();

            sb.AppendLine("Any AI-generated narrative is");
            sb.AppendLine("strictly descriptive and never");
            sb.AppendLine("modifies engineering values.");

            sb.AppendLine();

            sb.AppendLine("Human engineering review is");
            sb.AppendLine("required before release.");
        }
    }
}