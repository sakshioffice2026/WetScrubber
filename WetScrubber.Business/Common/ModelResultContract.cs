using System;
using System.Collections.Generic;

namespace WetScrubber.Business.Common
{
    /// <summary>
    /// What a calculation model actually covers.
    /// </summary>
    [Flags]
    public enum ModelScope
    {
        None = 0,
        ParticulateOnly = 1,
        GasAbsorptionOnly = 2,
        ParticulateAndGas = ParticulateOnly | GasAbsorptionOnly
    }

    /// <summary>
    /// Validation status of a model before it is allowed to drive final designs.
    /// </summary>
    public enum ModelValidationStatus
    {
        NotYetValidated = 0,
        Preview = 1,
        Screening = 2,
        Validated = 3
    }

    public enum FindingSeverity
    {
        Info = 0,
        Warning = 1,
        Infeasible = 2
    }

    public sealed class ModelFinding
    {
        public FindingSeverity Severity { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public ModelFinding() { }

        public ModelFinding(FindingSeverity severity, string code, string message)
        {
            Severity = severity;
            Code = code;
            Message = message;
        }
    }

    /// <summary>
    /// Shared base for scrubber model results: feasibility flag, findings, scope and validation status.
    /// A result is feasible only when no finding has Infeasible severity.
    /// </summary>
    public abstract class ModelResultBase
    {
        public ModelScope Scope { get; set; } = ModelScope.None;
        public ModelValidationStatus ValidationStatus { get; set; } = ModelValidationStatus.NotYetValidated;
        public List<ModelFinding> Findings { get; } = new();

        public bool IsFeasible
        {
            get
            {
                foreach (var f in Findings)
                    if (f.Severity == FindingSeverity.Infeasible) return false;
                return true;
            }
        }

        /// <summary>
        /// True when the model does not cover gas absorption and the caller should say so.
        /// </summary>
        public bool GasAbsorptionNotModeled => (Scope & ModelScope.GasAbsorptionOnly) == 0;

        public IEnumerable<string> Warnings
        {
            get
            {
                foreach (var f in Findings)
                    if (f.Severity == FindingSeverity.Warning) yield return f.Message;
            }
        }

        public IEnumerable<string> InfeasibilityReasons
        {
            get
            {
                foreach (var f in Findings)
                    if (f.Severity == FindingSeverity.Infeasible) yield return f.Message;
            }
        }

        public void AddInfo(string code, string message)
            => Findings.Add(new ModelFinding(FindingSeverity.Info, code, message));

        public void AddWarning(string code, string message)
            => Findings.Add(new ModelFinding(FindingSeverity.Warning, code, message));

        public void AddInfeasible(string code, string message)
            => Findings.Add(new ModelFinding(FindingSeverity.Infeasible, code, message));

        /// <summary>
        /// Copies plain warning strings (from lower-level helpers) into the findings list.
        /// </summary>
        public void AddWarnings(string code, IEnumerable<string>? messages)
        {
            if (messages == null) return;
            foreach (var m in messages) AddWarning(code, m);
        }
    }

    /// <summary>
    /// Describes the source, range and status of a single model constant.
    /// </summary>
    public sealed class ModelConstantInfo
    {
        public string Name { get; set; } = string.Empty;
        public double Value { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public double? ValidMin { get; set; }
        public double? ValidMax { get; set; }
        public ModelValidationStatus Status { get; set; } = ModelValidationStatus.NotYetValidated;
    }
}
