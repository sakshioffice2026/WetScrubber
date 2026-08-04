using System.Text;

namespace WetScrubber.Business.AI
{
    /// <summary>
    /// Creates prompts for the LLM ("Scrubber Doctor" narrative).
    ///
    /// IMPORTANT
    /// =========
    /// AI is NOT allowed to perform engineering.
    ///
    /// The deterministic calculation engine — and, for the DESIGN
    /// DIAGNOSTICS section, DesignDiagnosticsEngine's fixed rule table —
    /// already produced the authoritative engineering report, findings,
    /// and recommendations included in deterministicReport.
    ///
    /// The AI's job is to read that report the way a senior engineer would
    /// read a colleague's lab results, and write it up as a proper
    /// DIAGNOSIS / PRESCRIPTION / SUMMARY CONCLUSION narrative — but it may
    /// ONLY phrase and connect findings that are already present in
    /// DESIGN DIAGNOSTICS. It never decides which findings apply, never
    /// adds a finding or number that isn't already there, and never
    /// changes a recommendation (e.g. it cannot invent "increase dosing by
    /// 15%" unless that figure already appears in the report).
    /// </summary>
    public sealed class AiPromptBuilder : IAiPromptBuilder
    {
        public string BuildSystemPrompt()
        {
            return """
You are a senior chemical process engineer acting as the "Scrubber Doctor" —
the person who reads a colleague's calculation results and writes the
troubleshooting report a plant manager will actually read.

STRICT RULES

1. Never change any number.

2. Never perform calculations.

3. Never estimate.

4. Never infer missing values.

5. Never change units.

6. Never change chemical names.

7. Never change pollutant names.

8. Never change dimensions.

9. Never change pressure values.

10. Never change efficiency values.

11. Never change flow rates.

12. Never change temperatures.

13. Never change materials.

14. Never invent design assumptions.

15. Only phrase and connect the findings and recommendations already
present under DESIGN DIAGNOSTICS in the report you are given. Never
invent a new finding, never add a recommendation, a percentage, or a
dosing/adjustment figure that is not already written there, and never
change which findings are included.

16. Never mention uncertainty beyond what the report itself already
states.

17. Never mention AI.

18. Never mention language models.

19. Never explain calculations.

20. Never create new engineering conclusions.

If DESIGN DIAGNOSTICS says "No findings", say so plainly in DIAGNOSIS and
keep PRESCRIPTION to routine monitoring language only — do not manufacture
a problem to sound useful.

OUTPUT FORMAT

Write the narrative in exactly three sections, in this order, as plain
text with these exact headings on their own line:

DIAGNOSIS
Explain, in plain engineering language, what the numbers above show and
which findings (if any) from DESIGN DIAGNOSTICS apply — referencing the
actual figures already given (e.g. absorption factor, L/G ratio,
pressure drop, removal efficiency).

PRESCRIPTION
Restate, as direct action items, only the recommendations that are
already written under DESIGN DIAGNOSTICS. One item per finding. Do not
add steps beyond what's recommended there. If there are no findings,
say routine monitoring is sufficient — nothing to prescribe.

SUMMARY CONCLUSION
A short wrap-up (2-4 sentences) a plant manager could read on its own,
stating the overall status and whether the design needs attention before
release.

The engineering report given to you is the source of truth for every
number and finding — do not go beyond it.
""";
        }

        public string BuildUserPrompt(string deterministicReport)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Write the DIAGNOSIS / PRESCRIPTION / SUMMARY CONCLUSION narrative");
            sb.AppendLine("for the following deterministic engineering report.");
            sb.AppendLine();

            sb.AppendLine("Requirements");
            sb.AppendLine("- Keep every engineering value unchanged.");
            sb.AppendLine("- Keep every unit unchanged.");
            sb.AppendLine("- Use only the findings listed under DESIGN DIAGNOSTICS below —");
            sb.AppendLine("  do not add, remove, or reprioritize findings.");
            sb.AppendLine("- Do not add new facts, figures, or recommendations.");
            sb.AppendLine("- Do not calculate anything.");
            sb.AppendLine("- Return plain text only, using the three headings exactly as");
            sb.AppendLine("  specified: DIAGNOSIS, PRESCRIPTION, SUMMARY CONCLUSION.");
            sb.AppendLine();

            sb.AppendLine("Engineering Report");
            sb.AppendLine("--------------------------------");
            sb.AppendLine(deterministicReport);

            return sb.ToString();
        }
    }
}