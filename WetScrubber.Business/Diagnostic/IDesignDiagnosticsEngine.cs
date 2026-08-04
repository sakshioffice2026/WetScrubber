using System.Collections.Generic;

namespace WetScrubber.Business.Diagnostics
{
    public interface IDesignDiagnosticsEngine
    {
        /// <summary>
        /// Evaluates the design's computed metrics against the fixed
        /// engineering rule table and returns whichever findings apply.
        /// Deterministic — same input always yields the same findings.
        /// </summary>
        IReadOnlyList<DesignFinding> Evaluate(DesignMetrics metrics);
    }
}
