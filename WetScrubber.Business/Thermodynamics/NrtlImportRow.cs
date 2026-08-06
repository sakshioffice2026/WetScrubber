using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WetScrubber.Business.Thermodynamics
{
    public sealed class NrtlImportRow
    {
        public string ComponentACode { get; set; } = "";
        public string ComponentBCode { get; set; } = "";
        public double Tau_AB { get; set; }
        public double Tau_BA { get; set; }
        public double Alpha { get; set; } = 0.3;
        public double? ValidTempMinK { get; set; }
        public double? ValidTempMaxK { get; set; }

        /// <summary>Required. A row without a citation is rejected —
        /// this class will not let NRTL data into the system without
        /// provenance, same discipline as ReferenceSource elsewhere in
        /// this project.</summary>
        public string Citation { get; set; } = "";

        public string? SourceUrl { get; set; }
    }

    /// <summary>
    /// Parses NRTL binary-parameter data from CSV so real values sourced
    /// from DECHEMA's Chemistry Data Series, AspenTech's databank, or an
    /// in-house VLE regression can be loaded into NrtlBinaryParameter —
    /// WITHOUT this codebase fabricating any tau/alpha numbers itself.
    ///
    /// Expected header (order-independent):
    ///   ComponentACode,ComponentBCode,Tau_AB,Tau_BA,Alpha,
    ///   ValidTempMinK,ValidTempMaxK,Citation,SourceUrl
    ///
    /// Every row MUST carry a non-empty Citation — rows without one are
    /// rejected outright (see ParseResult.RejectedRows) rather than
    /// silently imported as if the value's provenance didn't matter.
    /// This does not seed or validate against a databank itself; it only
    /// gets real, sourced numbers from a CSV into the shape the engine
    /// needs. Setting NrtlBinaryParameter.ValidatedFlag = true is a
    /// separate, deliberate human review step — this importer defaults
    /// every imported row to ValidatedFlag = false.
    /// </summary>
    public static class NrtlCsvImporter
    {
        public sealed class ParseResult
        {
            public List<NrtlImportRow> Rows { get; set; } = new();
            public List<(int LineNumber, string Reason)> RejectedRows { get; set; } = new();
        }

        public static ParseResult Parse(string csvText)
        {
            var result = new ParseResult();
            if (string.IsNullOrWhiteSpace(csvText))
                return result;

            var lines = csvText.Replace("\r\n", "\n").Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count < 2)
                return result; // header only or empty

            var header = lines[0].Split(',').Select(h => h.Trim()).ToList();
            int Idx(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

            int idxA = Idx("ComponentACode");
            int idxB = Idx("ComponentBCode");
            int idxTauAB = Idx("Tau_AB");
            int idxTauBA = Idx("Tau_BA");
            int idxAlpha = Idx("Alpha");
            int idxTMin = Idx("ValidTempMinK");
            int idxTMax = Idx("ValidTempMaxK");
            int idxCitation = Idx("Citation");
            int idxUrl = Idx("SourceUrl");

            if (idxA < 0 || idxB < 0 || idxTauAB < 0 || idxTauBA < 0 || idxCitation < 0)
                throw new FormatException(
                    "CSV header must include ComponentACode, ComponentBCode, Tau_AB, Tau_BA, Citation " +
                    "(Alpha, ValidTempMinK, ValidTempMaxK, SourceUrl are optional).");

            for (int i = 1; i < lines.Count; i++)
            {
                int lineNumber = i + 1;
                var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();

                string citation = idxCitation < cells.Length ? cells[idxCitation] : "";
                if (string.IsNullOrWhiteSpace(citation))
                {
                    result.RejectedRows.Add((lineNumber, "Missing Citation — row rejected, not imported."));
                    continue;
                }

                if (idxA >= cells.Length || idxB >= cells.Length ||
                    string.IsNullOrWhiteSpace(cells[idxA]) || string.IsNullOrWhiteSpace(cells[idxB]))
                {
                    result.RejectedRows.Add((lineNumber, "Missing ComponentACode/ComponentBCode."));
                    continue;
                }

                if (!TryParseDouble(cells, idxTauAB, out var tauAB) ||
                    !TryParseDouble(cells, idxTauBA, out var tauBA))
                {
                    result.RejectedRows.Add((lineNumber, "Tau_AB/Tau_BA missing or not numeric."));
                    continue;
                }

                double alpha = 0.3;
                if (idxAlpha >= 0) TryParseDouble(cells, idxAlpha, out alpha, defaultValue: 0.3);

                double? tMin = idxTMin >= 0 && TryParseDouble(cells, idxTMin, out var tm) ? tm : null;
                double? tMax = idxTMax >= 0 && TryParseDouble(cells, idxTMax, out var tx) ? tx : null;
                string? url = idxUrl >= 0 && idxUrl < cells.Length ? cells[idxUrl] : null;

                result.Rows.Add(new NrtlImportRow
                {
                    ComponentACode = cells[idxA],
                    ComponentBCode = cells[idxB],
                    Tau_AB = tauAB,
                    Tau_BA = tauBA,
                    Alpha = alpha,
                    ValidTempMinK = tMin,
                    ValidTempMaxK = tMax,
                    Citation = citation,
                    SourceUrl = string.IsNullOrWhiteSpace(url) ? null : url
                });
            }

            return result;
        }

        private static bool TryParseDouble(string[] cells, int idx, out double value, double defaultValue = 0.0)
        {
            value = defaultValue;
            if (idx < 0 || idx >= cells.Length || string.IsNullOrWhiteSpace(cells[idx]))
                return idx < 0; // treat "column not present" as using default, "present but blank" as failure
            return double.TryParse(cells[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}