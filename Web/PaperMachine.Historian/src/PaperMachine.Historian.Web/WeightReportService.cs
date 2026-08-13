using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PaperMachine.Historian.Application;

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
            AddPdfCell(header.Cells[index], label, true, Colors.White);
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

    private static void AddPdfCell(Cell cell, string text, bool bold = false, Color? color = null)
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
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            AddZipText(archive, "[Content_Types].xml", ContentTypesXml);
            AddZipText(archive, "_rels/.rels", RootRelationshipsXml);
            AddZipText(archive, "docProps/core.xml", CorePropertiesXml(data));
            AddZipText(archive, "docProps/app.xml", AppPropertiesXml);
            AddZipText(archive, "xl/workbook.xml", WorkbookXml);
            AddZipText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml);
            AddZipText(archive, "xl/styles.xml", StylesXml);
            AddZipText(archive, "xl/worksheets/sheet1.xml", WorksheetXml(data));
        }
        return output.ToArray();
    }

    private static string WorksheetXml(WeightReportData data)
    {
        var builder = new StringBuilder(64_000);
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        builder.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"7\" topLeftCell=\"A8\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        builder.Append("<cols><col min=\"1\" max=\"1\" width=\"20\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"12\" customWidth=\"1\"/><col min=\"3\" max=\"5\" width=\"16\" customWidth=\"1\"/><col min=\"6\" max=\"7\" width=\"13\" customWidth=\"1\"/><col min=\"8\" max=\"10\" width=\"16\" customWidth=\"1\"/><col min=\"11\" max=\"12\" width=\"18\" customWidth=\"1\"/><col min=\"13\" max=\"15\" width=\"22\" customWidth=\"1\"/></cols><sheetData>");
        AddExcelRow(builder, 1, [("Relatório de pesos capturados", 1, false)]);
        AddExcelRow(builder, 2, [($"{data.MachineName} | Período: {FormatDateTime(data.Start)} até {FormatDateTime(data.End)}", 2, false)]);
        AddExcelRow(builder, 3, [($"Emitido em {FormatDateTime(data.GeneratedAt)} por {data.RequestedBy}" + (string.IsNullOrWhiteSpace(data.Search) ? "" : $" | Busca: {data.Search}"), 2, false)]);
        AddExcelRow(builder, 4, [($"Capturas: {data.Summary.Count:N0} | Peso total: {FormatNumber(data.Summary.TotalWeightKg, 2)} kg | Média: {FormatWeight(data.Summary.AverageWeightKg)} | Corrigidos: {data.Summary.CorrectedCount:N0} | Fora do padrão: {data.Summary.OutsideStandardCount:N0}", 3, false)]);
        var headers = new[] { "Data e hora", "Evento PLC", "Jumbo", "OP", "Produto", "Gramatura (g/m²)", "Largura (mm)", "Peso capturado (kg)", "Peso corrigido (kg)", "Peso considerado (kg)", "Intervalo (min)", "Situação do intervalo", "Status", "Corrigido por", "Motivo da correção" };
        AddExcelRow(builder, 7, headers.Select(value => (value, 4, false)).ToArray());
        var rowNumber = 8;
        foreach (var item in data.Rows)
        {
            builder.Append($"<row r=\"{rowNumber}\">");
            AddExcelNumberCell(builder, "A", rowNumber, item.Capture.CapturedAtUtc.ToLocalTime().DateTime.ToOADate(), 5);
            AddExcelNumberCell(builder, "B", rowNumber, item.Capture.PlcEventCounter, 0);
            AddExcelTextCell(builder, "C", rowNumber, item.Capture.ProductionExternalRunId ?? "", 0);
            AddExcelTextCell(builder, "D", rowNumber, item.Capture.ProductionOrderCode ?? "", 0);
            AddExcelTextCell(builder, "E", rowNumber, item.Capture.ProductCode ?? "", 0);
            AddOptionalNumberCell(builder, "F", rowNumber, item.Capture.GrammageGsm, 6);
            AddOptionalNumberCell(builder, "G", rowNumber, item.Capture.ProductionWidthMm, 6);
            AddExcelNumberCell(builder, "H", rowNumber, item.Capture.WeightKg, 7);
            AddOptionalNumberCell(builder, "I", rowNumber, item.Capture.CorrectedWeightKg, 7);
            AddExcelNumberCell(builder, "J", rowNumber, item.EffectiveWeightKg, 7);
            AddOptionalNumberCell(builder, "K", rowNumber, item.Interval?.TotalMinutes, 6);
            AddExcelTextCell(builder, "L", rowNumber, item.OutsideStandardInterval ? "Fora do padrão" : item.Interval.HasValue ? "Dentro do padrão" : "Primeira no período", item.OutsideStandardInterval ? 8 : 0);
            AddExcelTextCell(builder, "M", rowNumber, item.Capture.CaptureStatus == 20 ? "Capturado" : $"Status {item.Capture.CaptureStatus}", 0);
            AddExcelTextCell(builder, "N", rowNumber, item.Capture.CorrectedBy ?? "", 0);
            AddExcelTextCell(builder, "O", rowNumber, item.Capture.CorrectionReason ?? "", 0);
            builder.Append("</row>");
            rowNumber++;
        }
        builder.Append("</sheetData>");
        builder.Append("<mergeCells count=\"4\"><mergeCell ref=\"A1:O1\"/><mergeCell ref=\"A2:O2\"/><mergeCell ref=\"A3:O3\"/><mergeCell ref=\"A4:O4\"/></mergeCells>");
        builder.Append($"<autoFilter ref=\"A7:O{Math.Max(7, rowNumber - 1)}\"/><sheetProtection sheet=\"0\" objects=\"0\" scenarios=\"0\"/><pageMargins left=\"0.25\" right=\"0.25\" top=\"0.5\" bottom=\"0.5\" header=\"0.2\" footer=\"0.2\"/></worksheet>");
        return builder.ToString();
    }

    private static void AddExcelRow(StringBuilder builder, int row, IReadOnlyList<(string Text, int Style, bool Number)> cells)
    {
        builder.Append($"<row r=\"{row}\">");
        for (var index = 0; index < cells.Count; index++)
            AddExcelTextCell(builder, ColumnName(index + 1), row, cells[index].Text, cells[index].Style);
        builder.Append("</row>");
    }

    private static void AddExcelTextCell(StringBuilder builder, string column, int row, string value, int style) =>
        builder.Append($"<c r=\"{column}{row}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(value)}</t></is></c>");

    private static void AddExcelNumberCell(StringBuilder builder, string column, int row, double value, int style) =>
        builder.Append($"<c r=\"{column}{row}\" s=\"{style}\"><v>{value.ToString("R", CultureInfo.InvariantCulture)}</v></c>");

    private static void AddOptionalNumberCell(StringBuilder builder, string column, int row, double? value, int style)
    {
        if (value.HasValue)
            AddExcelNumberCell(builder, column, row, value.Value, style);
        else
            AddExcelTextCell(builder, column, row, "", style);
    }

    private static string ColumnName(int index) => ((char)('A' + index - 1)).ToString();
    private static string Xml(string value) => SecurityElement.Escape(new string(value.Where(character => character is '\t' or '\n' or '\r' || character >= ' ').ToArray())) ?? "";
    private static void AddZipText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

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

    private const string ContentTypesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/><Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/></Types>
        """;
    private const string RootRelationshipsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/></Relationships>
        """;
    private const string AppPropertiesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Application>CPNTeck Paper Machine Historian</Application></Properties>
        """;
    private const string WorkbookXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Pesos capturados" sheetId="1" r:id="rId1"/></sheets></workbook>
        """;
    private const string WorkbookRelationshipsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
        """;
    private const string StylesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="3"><numFmt numFmtId="164" formatCode="dd/mm/yyyy hh:mm:ss"/><numFmt numFmtId="165" formatCode="0.00"/><numFmt numFmtId="166" formatCode="0.0"/></numFmts><fonts count="4"><font><sz val="10"/><name val="Calibri"/></font><font><b/><sz val="18"/><color rgb="FF182B44"/><name val="Calibri"/></font><font><sz val="10"/><color rgb="FF65778C"/><name val="Calibri"/></font><font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts><fills count="5"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF182B44"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE8F1FC"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFE3E3"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color rgb="FFD4DEE8"/></left><right style="thin"><color rgb="FFD4DEE8"/></right><top style="thin"><color rgb="FFD4DEE8"/></top><bottom style="thin"><color rgb="FFD4DEE8"/></bottom><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="9"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="0" fillId="3" borderId="0" xfId="0"/><xf numFmtId="0" fontId="3" fillId="2" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf><xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0"/><xf numFmtId="166" fontId="0" fillId="0" borderId="1" xfId="0"/><xf numFmtId="165" fontId="0" fillId="0" borderId="1" xfId="0"/><xf numFmtId="0" fontId="0" fillId="4" borderId="1" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
        """;

    private static string CorePropertiesXml(WeightReportData data) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>Relatório de pesos capturados</dc:title><dc:creator>{Xml(data.RequestedBy)}</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">{data.GeneratedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>
        """;
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
