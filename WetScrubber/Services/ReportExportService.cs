using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace WetScrubber.Services
{
    public sealed record ReportRow(string Label, string Value);

    public sealed class ReportSection
    {
        public string Title { get; set; } = string.Empty;
        public List<ReportRow> Rows { get; } = new();
    }

    public sealed class ReportExportData
    {
        public string ProjectNumber { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string DesignName { get; set; } = string.Empty;
        public string ScrubberType { get; set; } = string.Empty;
        public int Revision { get; set; } = 1;
        public List<ReportSection> Sections { get; } = new();
    }

    public static class ReportExportService
    {
        private static string Heading(ReportExportData d) =>
            $"Wet Scrubber Calculation Report - {d.ScrubberType}";

        private static string SubHeading(ReportExportData d) =>
            $"{d.ProjectNumber} | {d.ProjectName} | {d.DesignName} | Rev {d.Revision} | {DateTime.Now:dd MMM yyyy HH:mm}";

        // ── PDF ──────────────────────────────────────────────
        public static byte[] ToPdf(ReportExportData d)
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            return QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Column(h =>
                    {
                        h.Item().Text(Heading(d)).FontSize(16).Bold();
                        h.Item().Text(SubHeading(d)).FontSize(9);
                    });

                    page.Content().PaddingVertical(10).Column(col =>
                    {
                        col.Spacing(10);

                        foreach (var sec in d.Sections)
                        {
                            col.Item().Column(c =>
                            {
                                c.Item().PaddingBottom(3).Text(sec.Title).FontSize(12).Bold();

                                c.Item().Table(t =>
                                {
                                    t.ColumnsDefinition(cd =>
                                    {
                                        cd.RelativeColumn(2);
                                        cd.RelativeColumn(3);
                                    });

                                    foreach (var row in sec.Rows)
                                    {
                                        t.Cell().BorderBottom(0.5f).PaddingVertical(3).Text(row.Label);
                                        t.Cell().BorderBottom(0.5f).PaddingVertical(3).Text(row.Value).SemiBold();
                                    }
                                });
                            });
                        }
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            }).GeneratePdf();
        }

        // ── DOCX ─────────────────────────────────────────────
        private static TableCell Cell(string text, bool bold)
        {
            var rp = new RunProperties();
            if (bold) rp.Append(new Bold());

            return new TableCell(
                new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "4500" }),
                new Paragraph(new Run(rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
        }

        public static byte[] ToDocx(ReportExportData d)
        {
            using var ms = new MemoryStream();

            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body());
                var body = main.Document.Body!;

                body.Append(new Paragraph(new Run(
                    new RunProperties(new Bold(), new FontSize { Val = "34" }),
                    new Text(Heading(d)))));

                body.Append(new Paragraph(new Run(
                    new RunProperties(new FontSize { Val = "18" }),
                    new Text(SubHeading(d)))));

                foreach (var sec in d.Sections)
                {
                    body.Append(new Paragraph(new Run(
                        new RunProperties(new Bold(), new FontSize { Val = "26" }),
                        new Text(sec.Title))));

                    var table = new Table(new TableProperties(
                        new TableBorders(
                            new TopBorder { Val = BorderValues.Single, Size = 4 },
                            new BottomBorder { Val = BorderValues.Single, Size = 4 },
                            new LeftBorder { Val = BorderValues.Single, Size = 4 },
                            new RightBorder { Val = BorderValues.Single, Size = 4 },
                            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

                    foreach (var row in sec.Rows)
                        table.Append(new TableRow(Cell(row.Label, false), Cell(row.Value, true)));

                    body.Append(table);
                    body.Append(new Paragraph(new Run(new Text(" ") { Space = SpaceProcessingModeValues.Preserve })));
                }

                main.Document.Save();
            }

            return ms.ToArray();
        }

        // ── XLSX ─────────────────────────────────────────────
        public static byte[] ToXlsx(ReportExportData d)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Calculation Report");

            ws.Column(1).Width = 34;
            ws.Column(2).Width = 40;

            ws.Cell(1, 1).Value = Heading(d);
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 16;
            ws.Cell(2, 1).Value = SubHeading(d);

            int row = 4;
            foreach (var sec in d.Sections)
            {
                ws.Cell(row, 1).Value = sec.Title;
                ws.Range(row, 1, row, 2).Style.Font.Bold = true;
                ws.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.LightGray;
                row++;

                foreach (var r in sec.Rows)
                {
                    ws.Cell(row, 1).Value = r.Label;
                    ws.Cell(row, 2).Value = r.Value;
                    ws.Cell(row, 2).Style.Font.Bold = true;
                    row++;
                }

                row++;
            }

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }
    }
}