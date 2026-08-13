using System.Globalization;
using System.Reflection;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PaperMachine.Historian.Application;
using Color = MigraDoc.DocumentObjectModel.Color;

namespace PaperMachine.Historian.Web;

public interface IWeightReportService
{
    Task<GeneratedReport> GeneratePdfAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? search,
        string requestedBy,
        CancellationToken cancellationToken);

    Task<GeneratedReport> GenerateExcelAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? search,
        string requestedBy,
        CancellationToken cancellationToken);
}

public sealed class WeightReportService(
    IHistorianRepository repository,
    ReportingOptions options,
    TimeProvider timeProvider,
    ILogger<WeightReportService> logger) : IWeightReportService
{
    private const int MaximumRows = 5_000;
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Color Navy = Color.FromRgb(24, 43, 68);
    private static readonly Color Blue = Color.FromRgb(38, 107, 200);
    private static readonly Color LightBlue = Color.FromRgb(232, 241, 252);
    private static readonly Color LightGray = Color.FromRgb(242, 245, 248);
    private static readonly Color MediumGray = Color.FromRgb(101, 119, 140);

    public async Task<GeneratedReport> GeneratePdfAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? search,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        var data = await LoadAsync(start, end, search, requestedBy, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var content = RenderPdf(data);
        logger.LogInformation(
            "Weight PDF generated. RequestedBy={RequestedBy}, Start={Start}, End={End}, Rows={Rows}, Bytes={Bytes}.",
            data.RequestedBy, start, end, data.Rows.Count, content.Length);
        return new GeneratedReport(
            content,
            $"Relatorio-Pesos-{start.ToLocalTime():yyyy-MM-dd}-a-{end.ToLocalTime():yyyy-MM-dd}.pdf");
    }

    public async Task<GeneratedReport> GenerateExcelAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? search,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        var data = await LoadAsync(start, end, search, requestedBy, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var content = RenderExcel(data);
        logger.LogInformation(
            "Weight Excel generated. RequestedBy={RequestedBy}, Start={Start}, End={End}, Rows={Rows}, Bytes={Bytes}.",
            data.RequestedBy, start, end, data.Rows.Count, content.Length);
        return new GeneratedReport(
            content,
            $"Pesos-Capturados-{start.ToLocalTime():yyyy-MM-dd}-a-{end.ToLocalTime():yyyy-MM-dd}.xlsx");
    }

    private async Task<WeightReportData> LoadAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? search,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        ValidateRequest(start, end, search);
        var captures = await repository.GetJumboWeightCapturesAsync(
            start.ToUniversalTime(),
            end.ToUniversalTime(),
            MaximumRows,
            cancellationToken);
        var normalizedSearch = search?.Trim();
        var filtered = string.IsNullOrWhiteSpace(normalizedSearch)
            ? captures
            : captures.Where(item => Matches(item, normalizedSearch)).ToArray();
        var rows = BuildRows(filtered);
        var weights = rows.Select(item => item.EffectiveWeightKg).ToArray();
        var summary = new WeightReportSummary(
            rows.Count,
            weights.Sum(),
            weights.Length == 0 ? null : weights.Average(),
            weights.Length == 0 ? null : weights.Min(),
            weights.Length == 0 ? null : weights.Max(),
            rows.Count(item => item.Capture.CorrectedWeightKg.HasValue),
            rows.Where(item => item.Interval.HasValue).Select(item => item.Interval!.Value.TotalMilliseconds)
                .DefaultIfEmpty().Average(),
            rows.Count(item => item.OutsideStandardInterval));
        if (!rows.Any(item => item.Interval.HasValue))
            summary = summary with { AverageIntervalMilliseconds = null };
        return new WeightReportData(
            options.MachineName.Trim(),
            start.ToLocalTime(),
            end.ToLocalTime(),
            timeProvider.GetUtcNow().ToLocalTime(),
            string.IsNullOrWhiteSpace(requestedBy) ? "Usuário não identificado" : requestedBy.Trim(),
            normalizedSearch,
            rows,
            summary);
    }

    private static IReadOnlyList<WeightReportRow> BuildRows(
        IReadOnlyList<JumboWeightCaptureRow> captures)
    {
        var ordered = captures.OrderByDescending(item => item.CapturedAtUtc).ThenByDescending(item => item.Id).ToArray();
        var intervals = new Dictionary<long, TimeSpan>();
        for (var index = 0; index < ordered.Length - 1; index++)
        {
            var interval = ordered[index].CapturedAtUtc - ordered[index + 1].CapturedAtUtc;
            if (interval > TimeSpan.Zero)
                intervals[ordered[index].Id] = interval;
        }
        var intervalValues = intervals.Values.Select(item => item.TotalMilliseconds).Order().ToArray();
        double? median = intervalValues.Length switch
        {
            0 => null,
            _ when intervalValues.Length % 2 == 1 => intervalValues[intervalValues.Length / 2],
            _ => (intervalValues[intervalValues.Length / 2 - 1] + intervalValues[intervalValues.Length / 2]) / 2
        };
        var tolerance = median.HasValue ? Math.Max(median.Value * .5, TimeSpan.FromMinutes(15).TotalMilliseconds) : 0;
        return ordered.Select(item =>
        {
            intervals.TryGetValue(item.Id, out var interval);
            var hasInterval = intervals.ContainsKey(item.Id);
            var outside = intervalValues.Length >= 3 && median.HasValue && hasInterval &&
                Math.Abs(interval.TotalMilliseconds - median.Value) > tolerance;
            return new WeightReportRow(
                item,
                item.CorrectedWeightKg ?? item.WeightKg,
                hasInterval ? interval : null,
                outside);
        }).ToArray();
    }

    private static bool Matches(JumboWeightCaptureRow item, string search) =>
        new[]
        {
            item.ProductionExternalRunId,
            item.ProductionOrderCode,
            item.ProductCode,
            item.PlcEventCounter.ToString(CultureInfo.InvariantCulture)
        }.Any(value => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true);

    private static void ValidateRequest(DateTimeOffset start, DateTimeOffset end, string? search)
    {
        if (start == default || end == default)
            throw new ArgumentException("Informe a data inicial e a data final.");
        if (end <= start)
            throw new ArgumentException("A data final deve ser posterior à data inicial.");
        if (end - start > TimeSpan.FromDays(366))
            throw new ArgumentException("O período máximo para relatórios de pesos é de 366 dias.");
        if (search?.Length > 120)
            throw new ArgumentException("A busca deve possuir no máximo 120 caracteres.");
    }

    private static byte[] RenderPdf(WeightReportData data)
    {
        EmbeddedReportFontResolver.EnsureRegistered();
        var document = new Document();
        document.Info.Title = "Relatório de pesos capturados";
        document.Info.Author = "CPNTeck Paper Machine Historian";
        ConfigurePdfStyles(document);
        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.1);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.7);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.1);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.1);
        AddPdfHeader(section, data);
        AddPdfSummary(section, data.Summary);
        AddPdfTable(section, data.Rows);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = Unit.FromPoint(7);
        footer.Format.Font.Color = MediumGray;
        footer.AddText("CPNTeck - Paper Machine Historian");
        footer.AddTab();
        footer.AddText("Página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();
        footer.Format.TabStops.AddTabStop(Unit.FromCentimeter(25.5), TabAlignment.Right);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        return output.ToArray();
    }

    private static void ConfigurePdfStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedReportFontResolver.FamilyName;
        normal.Font.Size = Unit.FromPoint(7.2);
        normal.Font.Color = Navy;
    }

    private static void AddPdfHeader(Section section, WeightReportData data)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn(Unit.FromCentimeter(4.5));
        table.AddColumn(Unit.FromCentimeter(14.5));
        table.AddColumn(Unit.FromCentimeter(8.2));
        var row = table.AddRow();
        var logo = row.Cells[0].AddImage(GetEmbeddedImageDataUri("PaperMachine.Reporting.CPNTeck-Logo.png"));
        logo.Width = Unit.FromCentimeter(3.6);
        logo.LockAspectRatio = true;
        var title = row.Cells[1].AddParagraph();
        title.AddFormattedText("RELATÓRIO DE PESOS CAPTURADOS", TextFormat.Bold);
        title.Format.Font.Size = Unit.FromPoint(15);
        title.Format.Font.Color = Navy;
        title.AddLineBreak();
        title.AddText(data.MachineName);
        var metadata = row.Cells[2].AddParagraph();
        metadata.Format.Alignment = ParagraphAlignment.Right;
        metadata.Format.Font.Size = Unit.FromPoint(7.2);
        metadata.AddText($"Período: {FormatDateTime(data.Start)} até {FormatDateTime(data.End)}");
        metadata.AddLineBreak();
        metadata.AddText($"Emitido em: {FormatDateTime(data.GeneratedAt)}");
        metadata.AddLineBreak();
        metadata.AddText($"Solicitado por: {data.RequestedBy}");
        if (!string.IsNullOrWhiteSpace(data.Search))
        {
            metadata.AddLineBreak();
            metadata.AddText($"Busca: {data.Search}");
        }
        var separator = section.AddParagraph();
        separator.Format.Borders.Bottom.Width = Unit.FromPoint(1.2);
        separator.Format.Borders.Bottom.Color = Blue;
        separator.Format.SpaceAfter = Unit.FromPoint(7);
    }

    private static void AddPdfSummary(Section section, WeightReportSummary summary)
    {
        var values = new[]
        {
            ("Capturas", summary.Count.ToString("N0", Portuguese)),
            ("Peso acumulado", $"{FormatNumber(summary.TotalWeightKg, 1)} kg"),
            ("Peso médio", FormatWeight(summary.AverageWeightKg)),
            ("Menor peso", FormatWeight(summary.MinimumWeightKg)),
            ("Maior peso", FormatWeight(summary.MaximumWeightKg)),
            ("Corrigidos", summary.CorrectedCount.ToString("N0", Portuguese)),
            ("Intervalo médio", FormatInterval(summary.AverageIntervalMilliseconds)),
            ("Fora do padrão", summary.OutsideStandardCount.ToString("N0", Portuguese))
        };
        var table = section.AddTable();
        table.Borders.Width = Unit.FromPoint(.35);
        table.Borders.Color = Color.FromRgb(206, 216, 226);
        for (var index = 0; index < values.Length; index++)
            table.AddColumn(Unit.FromCentimeter(3.4));
        var row = table.AddRow();
        for (var index = 0; index < values.Length; index++)
        {
            row.Cells[index].Shading.Color = index % 2 == 0 ? LightBlue : LightGray;
            var paragraph = row.Cells[index].AddParagraph();
            paragraph.Format.LeftIndent = Unit.FromMillimeter(1.4);
            paragraph.Format.SpaceBefore = Unit.FromPoint(4);
            paragraph.Format.SpaceAfter = Unit.FromPoint(4);
            paragraph.AddFormattedText(values[index].Item1.ToUpper(Portuguese), TextFormat.Bold);
            paragraph.Format.Font.Size = Unit.FromPoint(6);
            paragraph.Format.Font.Color = MediumGray;
            paragraph.AddLineBreak();
            var value = paragraph.AddFormattedText(values[index].Item2, TextFormat.Bold);
            value.Font.Size = Unit.FromPoint(9.5);
            value.Font.Color = Navy;
        }
    }

    private static void AddPdfTable(Section section, IReadOnlyList<WeightReportRow> rows)
    {
        var heading = section.AddParagraph("Pesagens do período");
        heading.Format.Font.Size = Unit.FromPoint(13);
        heading.Format.Font.Bold = true;
        heading.Format.SpaceBefore = Unit.FromPoint(9);
        heading.Format.SpaceAfter = Unit.FromPoint(5);
        if (rows.Count == 0)
        {
            section.AddParagraph("Nenhuma pesagem encontrada para os filtros selecionados.");
            return;
        }
        var table = section.AddTable();
        table.Borders.Width = Unit.FromPoint(.3);
        table.Borders.Color = Color.FromRgb(205, 215, 225);
        foreach (var width in new[] { 3.1, 1.7, 2.1, 2.2, 3.2, 2.0, 2.7, 2.7, 2.7, 2.7 })
            table.AddColumn(Unit.FromCentimeter(width));
        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Navy;
        foreach (var (label, index) in new[] { "Data e hora", "Evento", "Jumbo", "OP", "Produto", "Gramatura", "Capturado", "Considerado", "Intervalo", "Situação" }.Select((value, index) => (value, index)))
            AddPdfCell(
                header.Cells[index],
                label,
                true,
                MigraDoc.DocumentObjectModel.Colors.White);
        foreach (var item in rows)
        {
            var row = table.AddRow();
            if (item.OutsideStandardInterval)
                row.Shading.Color = Color.FromRgb(255, 236, 236);
            AddPdfCell(row.Cells[0], FormatDateTime(item.Capture.CapturedAtUtc.ToLocalTime()));
            AddPdfCell(row.Cells[1], item.Capture.PlcEventCounter.ToString(Portuguese));
            AddPdfCell(row.Cells[2], item.Capture.ProductionExternalRunId ?? "—");
            AddPdfCell(row.Cells[3], item.Capture.ProductionOrderCode ?? "—");
            AddPdfCell(row.Cells[4], item.Capture.ProductCode ?? "Sem produto");
            AddPdfCell(row.Cells[5], item.Capture.GrammageGsm.HasValue ? $"{FormatNumber(item.Capture.GrammageGsm.Value, 1)} g/m²" : "—");
            AddPdfCell(row.Cells[6], $"{FormatNumber(item.Capture.WeightKg, 2)} kg");
            AddPdfCell(row.Cells[7], $"{FormatNumber(item.EffectiveWeightKg, 2)} kg" + (item.Capture.CorrectedWeightKg.HasValue ? " *" : ""));
            AddPdfCell(row.Cells[8], FormatInterval(item.Interval?.TotalMilliseconds));
            AddPdfCell(row.Cells[9], item.OutsideStandardInterval ? "Fora do padrão" : item.Capture.CorrectedWeightKg.HasValue ? "Corrigido" : "Capturado");
        }
        var note = section.AddParagraph("* Valor corrigido por supervisor. Os cálculos consideram o peso corrigido quando disponível.");
        note.Format.Font.Size = Unit.FromPoint(6.5);
        note.Format.Font.Color = MediumGray;
        note.Format.SpaceBefore = Unit.FromPoint(4);
    }

    private static void AddPdfCell(
        MigraDoc.DocumentObjectModel.Tables.Cell cell,
        string text,
        bool bold = false,
        Color? color = null)
    {
        var paragraph = cell.AddParagraph(text);
        paragraph.Format.LeftIndent = Unit.FromMillimeter(.8);
        paragraph.Format.RightIndent = Unit.FromMillimeter(.8);
        paragraph.Format.SpaceBefore = Unit.FromMillimeter(.55);
        paragraph.Format.SpaceAfter = Unit.FromMillimeter(.55);
        paragraph.Format.Font.Size = Unit.FromPoint(6.2);
        paragraph.Format.Font.Bold = bold;
        if (color.HasValue)
            paragraph.Format.Font.Color = color.Value;
    }

    private static byte[] RenderExcel(WeightReportData data)
    {
        using var output = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(
                   output,
                   SpreadsheetDocumentType.Workbook,
                   autoSave: true))
        {
            document.PackageProperties.Title = "Relatório de pesos capturados";
            document.PackageProperties.Creator = data.RequestedBy;
            document.PackageProperties.Created = data.GeneratedAt.UtcDateTime;

            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = CreateExcelStylesheet();
            stylesPart.Stylesheet.Save();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = CreateExcelWorksheet(data);
            worksheetPart.Worksheet.Save();

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1U,
                Name = "Pesos capturados"
            });
            workbookPart.Workbook.Save();
        }
        return output.ToArray();
    }

    private static Worksheet CreateExcelWorksheet(WeightReportData data)
    {
        var view = new SheetView { WorkbookViewId = 0U };
        view.Append(new Pane
        {
            VerticalSplit = 7D,
            TopLeftCell = "A8",
            ActivePane = PaneValues.BottomLeft,
            State = PaneStateValues.Frozen
        });
        var sheetViews = new SheetViews(view);
        var columns = new DocumentFormat.OpenXml.Spreadsheet.Columns(
            CreateExcelColumn(1, 1, 20),
            CreateExcelColumn(2, 2, 12),
            CreateExcelColumn(3, 5, 16),
            CreateExcelColumn(6, 7, 13),
            CreateExcelColumn(8, 10, 16),
            CreateExcelColumn(11, 12, 18),
            CreateExcelColumn(13, 15, 22));
        var sheetData = new SheetData();
        sheetData.Append(CreateExcelRow(1, [CreateExcelTextCell("A1", "Relatório de pesos capturados", 1)]));
        sheetData.Append(CreateExcelRow(2, [CreateExcelTextCell(
            "A2",
            $"{data.MachineName} | Período: {FormatDateTime(data.Start)} até {FormatDateTime(data.End)}",
            2)]));
        sheetData.Append(CreateExcelRow(3, [CreateExcelTextCell(
            "A3",
            $"Emitido em {FormatDateTime(data.GeneratedAt)} por {data.RequestedBy}" +
            (string.IsNullOrWhiteSpace(data.Search) ? "" : $" | Busca: {data.Search}"),
            2)]));
        sheetData.Append(CreateExcelRow(4, [CreateExcelTextCell(
            "A4",
            $"Capturas: {data.Summary.Count:N0} | Peso total: {FormatNumber(data.Summary.TotalWeightKg, 2)} kg | " +
            $"Média: {FormatWeight(data.Summary.AverageWeightKg)} | Corrigidos: {data.Summary.CorrectedCount:N0} | " +
            $"Fora do padrão: {data.Summary.OutsideStandardCount:N0}",
            3)]));

        var headers = new[]
        {
            "Data e hora", "Evento PLC", "Jumbo", "OP", "Produto", "Gramatura (g/m²)",
            "Largura (mm)", "Peso capturado (kg)", "Peso corrigido (kg)",
            "Peso considerado (kg)", "Intervalo (min)", "Situação do intervalo", "Status",
            "Corrigido por", "Motivo da correção"
        };
        sheetData.Append(CreateExcelRow(
            7,
            headers.Select((value, index) =>
                CreateExcelTextCell($"{ExcelColumnName(index + 1)}7", value, 4))));

        uint rowNumber = 8;
        foreach (var item in data.Rows)
        {
            sheetData.Append(CreateExcelRow(rowNumber,
            [
                CreateExcelNumberCell($"A{rowNumber}", item.Capture.CapturedAtUtc.ToLocalTime().DateTime.ToOADate(), 5),
                CreateExcelNumberCell($"B{rowNumber}", item.Capture.PlcEventCounter, 0),
                CreateExcelTextCell($"C{rowNumber}", item.Capture.ProductionExternalRunId ?? "", 0),
                CreateExcelTextCell($"D{rowNumber}", item.Capture.ProductionOrderCode ?? "", 0),
                CreateExcelTextCell($"E{rowNumber}", item.Capture.ProductCode ?? "", 0),
                CreateOptionalExcelNumberCell($"F{rowNumber}", item.Capture.GrammageGsm, 6),
                CreateOptionalExcelNumberCell($"G{rowNumber}", item.Capture.ProductionWidthMm, 6),
                CreateExcelNumberCell($"H{rowNumber}", item.Capture.WeightKg, 7),
                CreateOptionalExcelNumberCell($"I{rowNumber}", item.Capture.CorrectedWeightKg, 7),
                CreateExcelNumberCell($"J{rowNumber}", item.EffectiveWeightKg, 7),
                CreateOptionalExcelNumberCell($"K{rowNumber}", item.Interval?.TotalMinutes, 6),
                CreateExcelTextCell(
                    $"L{rowNumber}",
                    item.OutsideStandardInterval
                        ? "Fora do padrão"
                        : item.Interval.HasValue ? "Dentro do padrão" : "Primeira no período",
                    item.OutsideStandardInterval ? 8U : 0U),
                CreateExcelTextCell(
                    $"M{rowNumber}",
                    item.Capture.CaptureStatus == 20 ? "Capturado" : $"Status {item.Capture.CaptureStatus}",
                    0),
                CreateExcelTextCell($"N{rowNumber}", item.Capture.CorrectedBy ?? "", 0),
                CreateExcelTextCell($"O{rowNumber}", item.Capture.CorrectionReason ?? "", 0)
            ]));
            rowNumber++;
        }

        var lastRow = Math.Max(7U, rowNumber - 1);
        var autoFilter = new AutoFilter { Reference = $"A7:O{lastRow}" };
        var mergeCells = new MergeCells(
            new MergeCell { Reference = "A1:O1" },
            new MergeCell { Reference = "A2:O2" },
            new MergeCell { Reference = "A3:O3" },
            new MergeCell { Reference = "A4:O4" });
        var margins = new PageMargins
        {
            Left = .25D,
            Right = .25D,
            Top = .5D,
            Bottom = .5D,
            Header = .2D,
            Footer = .2D
        };

        return new Worksheet(sheetViews, columns, sheetData, autoFilter, mergeCells, margins);
    }

    private static Stylesheet CreateExcelStylesheet()
    {
        var numberingFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164U, FormatCode = "dd/mm/yyyy hh:mm:ss" },
            new NumberingFormat { NumberFormatId = 165U, FormatCode = "0.00" },
            new NumberingFormat { NumberFormatId = 166U, FormatCode = "0.0" })
        { Count = 3U };
        var fonts = new Fonts(
            CreateExcelFont(10),
            CreateExcelFont(18, bold: true, color: "FF182B44"),
            CreateExcelFont(10, color: "FF65778C"),
            CreateExcelFont(10, bold: true, color: "FFFFFFFF"))
        { Count = 4U };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            CreateExcelFill("FF182B44"),
            CreateExcelFill("FFE8F1FC"),
            CreateExcelFill("FFFFE3E3"))
        { Count = 5U };
        var borders = new DocumentFormat.OpenXml.Spreadsheet.Borders(
            new DocumentFormat.OpenXml.Spreadsheet.Border(),
            new DocumentFormat.OpenXml.Spreadsheet.Border(
                CreateExcelLeftBorder(),
                CreateExcelRightBorder(),
                CreateExcelTopBorder(),
                CreateExcelBottomBorder(),
                new DiagonalBorder()))
        { Count = 2U };
        var cellStyleFormats = new CellStyleFormats(new CellFormat()) { Count = 1U };
        var cellFormats = new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1U },
            new CellFormat { FontId = 2U },
            new CellFormat { FillId = 3U },
            new CellFormat
            {
                FontId = 3U,
                FillId = 2U,
                BorderId = 1U,
                ApplyAlignment = true,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Center,
                    Vertical = VerticalAlignmentValues.Center,
                    WrapText = true
                }
            },
            CreateExcelNumberCellFormat(164U),
            CreateExcelNumberCellFormat(166U),
            CreateExcelNumberCellFormat(165U),
            new CellFormat { FillId = 4U, BorderId = 1U })
        { Count = 9U };
        var cellStyles = new CellStyles(
            new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U })
        { Count = 1U };
        return new Stylesheet(
            numberingFormats,
            fonts,
            fills,
            borders,
            cellStyleFormats,
            cellFormats,
            cellStyles);
    }

    private static DocumentFormat.OpenXml.Spreadsheet.Font CreateExcelFont(
        double size,
        bool bold = false,
        string? color = null)
    {
        var font = new DocumentFormat.OpenXml.Spreadsheet.Font();
        if (bold) font.Append(new Bold());
        font.Append(new FontSize { Val = size });
        if (color is not null) font.Append(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = color });
        font.Append(new FontName { Val = "Calibri" });
        return font;
    }

    private static Fill CreateExcelFill(string color) =>
        new(new PatternFill(
            new ForegroundColor { Rgb = color },
            new BackgroundColor { Indexed = 64U })
        { PatternType = PatternValues.Solid });

    private static LeftBorder CreateExcelLeftBorder() =>
        new(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFD4DEE8" })
        { Style = BorderStyleValues.Thin };
    private static RightBorder CreateExcelRightBorder() =>
        new(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFD4DEE8" })
        { Style = BorderStyleValues.Thin };
    private static TopBorder CreateExcelTopBorder() =>
        new(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFD4DEE8" })
        { Style = BorderStyleValues.Thin };
    private static BottomBorder CreateExcelBottomBorder() =>
        new(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFD4DEE8" })
        { Style = BorderStyleValues.Thin };

    private static CellFormat CreateExcelNumberCellFormat(uint numberFormatId) =>
        new()
        {
            NumberFormatId = numberFormatId,
            BorderId = 1U,
            ApplyNumberFormat = true
        };

    private static DocumentFormat.OpenXml.Spreadsheet.Column CreateExcelColumn(
        uint min,
        uint max,
        double width) =>
        new() { Min = min, Max = max, Width = width, CustomWidth = true };

    private static DocumentFormat.OpenXml.Spreadsheet.Row CreateExcelRow(
        uint rowIndex,
        IEnumerable<DocumentFormat.OpenXml.Spreadsheet.Cell> cells)
    {
        var row = new DocumentFormat.OpenXml.Spreadsheet.Row { RowIndex = rowIndex };
        row.Append(cells);
        return row;
    }

    private static DocumentFormat.OpenXml.Spreadsheet.Cell CreateExcelTextCell(
        string reference,
        string value,
        uint styleIndex) =>
        new()
        {
            CellReference = reference,
            StyleIndex = styleIndex,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(
                new DocumentFormat.OpenXml.Spreadsheet.Text(value)
                { Space = SpaceProcessingModeValues.Preserve })
        };

    private static DocumentFormat.OpenXml.Spreadsheet.Cell CreateExcelNumberCell(
        string reference,
        double value,
        uint styleIndex) =>
        new()
        {
            CellReference = reference,
            StyleIndex = styleIndex,
            DataType = CellValues.Number,
            CellValue = new CellValue(value.ToString("R", CultureInfo.InvariantCulture))
        };

    private static DocumentFormat.OpenXml.Spreadsheet.Cell CreateOptionalExcelNumberCell(
        string reference,
        double? value,
        uint styleIndex) =>
        value.HasValue
            ? CreateExcelNumberCell(reference, value.Value, styleIndex)
            : CreateExcelTextCell(reference, "", styleIndex);

    private static string ExcelColumnName(int index) =>
        ((char)('A' + index - 1)).ToString();

    private static string GetEmbeddedImageDataUri(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"O recurso incorporado de relatório '{resourceName}' não foi encontrado.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return $"base64:{Convert.ToBase64String(memory.ToArray())}";
    }

    private static string FormatDateTime(DateTimeOffset value) => value.ToString("dd/MM/yyyy HH:mm", Portuguese);
    private static string FormatNumber(double value, int decimals) => value.ToString($"N{decimals}", Portuguese);
    private static string FormatWeight(double? value) => value.HasValue ? $"{FormatNumber(value.Value, 2)} kg" : "—";
    private static string FormatInterval(double? milliseconds)
    {
        if (!milliseconds.HasValue) return "—";
        var minutes = (int)Math.Round(milliseconds.Value / 60_000);
        if (minutes >= 1_440) return $"{minutes / 1_440}d {(minutes % 1_440) / 60}h";
        if (minutes >= 60) return $"{minutes / 60}h {minutes % 60:00}min";
        return $"{minutes} min";
    }

}

internal sealed record WeightReportData(
    string MachineName,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset GeneratedAt,
    string RequestedBy,
    string? Search,
    IReadOnlyList<WeightReportRow> Rows,
    WeightReportSummary Summary);

internal sealed record WeightReportRow(
    JumboWeightCaptureRow Capture,
    double EffectiveWeightKg,
    TimeSpan? Interval,
    bool OutsideStandardInterval);

internal sealed record WeightReportSummary(
    int Count,
    double TotalWeightKg,
    double? AverageWeightKg,
    double? MinimumWeightKg,
    double? MaximumWeightKg,
    int CorrectedCount,
    double? AverageIntervalMilliseconds,
    int OutsideStandardCount);
