using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PaperMachine.Historian.Web;

internal static class ProductionMetricsExcelReport
{
    private const uint TitleStyle = 1U;
    private const uint MetadataStyle = 2U;
    private const uint HighlightStyle = 3U;
    private const uint HeaderStyle = 4U;
    private const uint DateStyle = 5U;
    private const uint DecimalStyle = 6U;
    private const uint PreciseDecimalStyle = 7U;
    private const uint IntegerStyle = 8U;
    private const uint PercentageStyle = 9U;
    private const uint TextStyle = 10U;

    public static byte[] Render(ProductionBreakReportData data)
    {
        using var output = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(
                   output,
                   SpreadsheetDocumentType.Workbook,
                   autoSave: true))
        {
            document.PackageProperties.Title = "Métricas da máquina";
            document.PackageProperties.Subject = "Produção, desempenho horário, quebras e Pareto";
            document.PackageProperties.Creator = data.RequestedBy;
            document.PackageProperties.Created = data.GeneratedAt.UtcDateTime;

            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = CreateStylesheet();
            stylesPart.Stylesheet.Save();

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            AddSheet(workbookPart, sheets, 1U, "Resumo", CreateSummaryWorksheet(data));
            AddSheet(
                workbookPart,
                sheets,
                2U,
                "Desempenho horário",
                CreateHourlyWorksheet(data));
            AddSheet(workbookPart, sheets, 3U, "Quebras", CreateBreaksWorksheet(data));
            AddSheet(workbookPart, sheets, 4U, "Pareto", CreateParetoWorksheet(data));
            workbookPart.Workbook.Save();
        }

        return output.ToArray();
    }

    private static void AddSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        uint sheetId,
        string name,
        Worksheet worksheet)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = worksheet;
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name
        });
    }

    private static Worksheet CreateSummaryWorksheet(ProductionBreakReportData data)
    {
        var sheetData = CreateHeading(data, "Métricas da máquina");
        sheetData.Append(Row(5,
        [
            TextCell("A5", "Indicador", HeaderStyle),
            TextCell("B5", "Valor", HeaderStyle),
            TextCell("C5", "Unidade", HeaderStyle)
        ]));

        var productivity = data.Productivity;
        var breaks = data.Breaks;
        var values = new (string Label, double? Value, string Unit, uint Style)[]
        {
            ("Produtividade", productivity.ProductivityPercent / 100D, "%", PercentageStyle),
            ("Tempo coberto", productivity.CoveredMinutes, "min", DecimalStyle),
            ("Tempo produtivo", productivity.ProductiveMinutes, "min", DecimalStyle),
            ("Tempo sem produção", productivity.UnproductiveMinutes, "min", DecimalStyle),
            ("Papel confirmado", productivity.PaperPresencePercent / 100D, "%", PercentageStyle),
            ("Velocidade média produtiva", productivity.ProductiveAverageSpeed, "m/min", DecimalStyle),
            ("Velocidade média geral", productivity.GeneralAverageSpeed, "m/min", DecimalStyle),
            ("Velocidade máxima", productivity.MaximumSpeed, "m/min", DecimalStyle),
            ("Quebras confirmadas", breaks.Breaks.Count, "quebras", IntegerStyle),
            ("Quebras por hora de operação", breaks.BreaksPerOperatingHour, "quebras/h", PreciseDecimalStyle),
            ("MTTR — tempo médio de reparo", breaks.MttrMinutes, "min", DecimalStyle),
            ("MTBF — tempo médio entre falhas", breaks.MtbfMinutes, "min", DecimalStyle),
            ("MTTF — tempo médio até a falha", breaks.MttfMinutes, "min", DecimalStyle),
            ("Maior período produtivo sem quebra", productivity.LongestProductiveRunMinutes, "min", DecimalStyle),
            ("Duração da última quebra", breaks.LastBreakMinutes, "min", DecimalStyle)
        };

        uint rowIndex = 6;
        foreach (var value in values)
        {
            sheetData.Append(Row(rowIndex,
            [
                TextCell($"A{rowIndex}", value.Label, TextStyle),
                OptionalNumberCell($"B{rowIndex}", value.Value, value.Style),
                TextCell($"C{rowIndex}", value.Unit, TextStyle)
            ]));
            rowIndex++;
        }

        return CreateWorksheet(
            sheetData,
            new Columns(Column(1, 1, 39), Column(2, 2, 18), Column(3, 3, 15)),
            $"A5:C{rowIndex - 1}",
            ["A1:C1", "A2:C2", "A3:C3"]);
    }

    private static Worksheet CreateHourlyWorksheet(ProductionBreakReportData data)
    {
        var sheetData = CreateHeading(data, "Desempenho horário");
        sheetData.Append(Row(5,
        [
            TextCell("A5", "Hora", HeaderStyle),
            TextCell("B5", "Produtividade", HeaderStyle),
            TextCell("C5", "Quebras", HeaderStyle),
            TextCell("D5", "Tempo sem produção (min)", HeaderStyle)
        ]));

        for (var hour = 0; hour < 24; hour++)
        {
            var rowIndex = (uint)(hour + 6);
            sheetData.Append(Row(rowIndex,
            [
                TextCell($"A{rowIndex}", $"{hour:00}h", TextStyle),
                NumberCell(
                    $"B{rowIndex}",
                    ValueAt(data.Productivity.HourlyProductivity, hour) / 100D,
                    PercentageStyle),
                NumberCell(
                    $"C{rowIndex}",
                    ValueAt(data.Breaks.HourlyBreaks, hour),
                    IntegerStyle),
                NumberCell(
                    $"D{rowIndex}",
                    ValueAt(data.Productivity.HourlyUnproductiveMinutes, hour),
                    DecimalStyle)
            ]));
        }

        return CreateWorksheet(
            sheetData,
            new Columns(
                Column(1, 1, 12),
                Column(2, 3, 17),
                Column(4, 4, 27)),
            "A5:D29",
            ["A1:D1", "A2:D2", "A3:D3"]);
    }

    private static Worksheet CreateBreaksWorksheet(ProductionBreakReportData data)
    {
        var sheetData = CreateHeading(data, "Quebras confirmadas");
        var headers = new[]
        {
            "Início", "Fim", "Duração (min)", "Velocidade inicial (m/min)",
            "Velocidade final (m/min)", "Categoria", "Causa", "Status",
            "Observações", "Analisado por", "Analisado em", "Ativa ao iniciar"
        };
        sheetData.Append(Row(
            5,
            headers.Select((header, index) =>
                TextCell($"{ColumnName(index + 1)}5", header, HeaderStyle))));

        uint rowIndex = 6;
        foreach (var item in data.Breaks.Breaks.OrderBy(value => value.Event.StartedAtUtc))
        {
            var itemEvent = item.Event;
            sheetData.Append(Row(rowIndex,
            [
                DateCell($"A{rowIndex}", itemEvent.StartedAtUtc),
                OptionalDateCell($"B{rowIndex}", itemEvent.EndedAtUtc),
                NumberCell($"C{rowIndex}", item.DurationMinutes, DecimalStyle),
                NumberCell($"D{rowIndex}", itemEvent.SpeedAtStartMpm, DecimalStyle),
                OptionalNumberCell($"E{rowIndex}", itemEvent.SpeedAtEndMpm, DecimalStyle),
                TextCell($"F{rowIndex}", itemEvent.CauseCategory ?? "", TextStyle),
                TextCell($"G{rowIndex}", itemEvent.CauseDescription ?? "", TextStyle),
                TextCell($"H{rowIndex}", itemEvent.AnalysisStatus, TextStyle),
                TextCell($"I{rowIndex}", itemEvent.AnalysisNotes ?? "", TextStyle),
                TextCell($"J{rowIndex}", itemEvent.AnalyzedBy ?? "", TextStyle),
                OptionalDateCell($"K{rowIndex}", itemEvent.AnalyzedAtUtc),
                TextCell($"L{rowIndex}", itemEvent.ActiveAtStartup ? "Sim" : "Não", TextStyle)
            ]));
            rowIndex++;
        }

        var lastRow = Math.Max(5U, rowIndex - 1);
        return CreateWorksheet(
            sheetData,
            new Columns(
                Column(1, 2, 20),
                Column(3, 5, 22),
                Column(6, 6, 18),
                Column(7, 7, 32),
                Column(8, 8, 17),
                Column(9, 9, 36),
                Column(10, 10, 20),
                Column(11, 11, 20),
                Column(12, 12, 16)),
            $"A5:L{lastRow}",
            ["A1:L1", "A2:L2", "A3:L3"]);
    }

    private static Worksheet CreateParetoWorksheet(ProductionBreakReportData data)
    {
        var sheetData = CreateHeading(data, "Pareto de causas");
        sheetData.Append(Row(5,
        [
            TextCell("A5", "Categoria", HeaderStyle),
            TextCell("B5", "Causa", HeaderStyle),
            TextCell("C5", "Quantidade", HeaderStyle),
            TextCell("D5", "Duração (min)", HeaderStyle),
            TextCell("E5", "Percentual acumulado", HeaderStyle)
        ]));

        uint rowIndex = 6;
        foreach (var item in data.Breaks.Pareto)
        {
            sheetData.Append(Row(rowIndex,
            [
                TextCell($"A{rowIndex}", item.Category, TextStyle),
                TextCell($"B{rowIndex}", item.Cause, TextStyle),
                NumberCell($"C{rowIndex}", item.Count, IntegerStyle),
                NumberCell($"D{rowIndex}", item.DurationMinutes, DecimalStyle),
                NumberCell($"E{rowIndex}", item.CumulativePercent / 100D, PercentageStyle)
            ]));
            rowIndex++;
        }

        var lastRow = Math.Max(5U, rowIndex - 1);
        return CreateWorksheet(
            sheetData,
            new Columns(
                Column(1, 1, 20),
                Column(2, 2, 38),
                Column(3, 4, 18),
                Column(5, 5, 23)),
            $"A5:E{lastRow}",
            ["A1:E1", "A2:E2", "A3:E3"]);
    }

    private static SheetData CreateHeading(
        ProductionBreakReportData data,
        string title)
    {
        var sheetData = new SheetData();
        sheetData.Append(Row(1, [TextCell("A1", title, TitleStyle)]));
        sheetData.Append(Row(2, [TextCell(
            "A2",
            $"{data.MachineName} | Período: {FormatDateTime(data.Start)} até {FormatDateTime(data.End)} | Regra produtiva: ≥ {data.ProductiveSpeedMpm:N1} m/min",
            MetadataStyle)]));
        sheetData.Append(Row(3, [TextCell(
            "A3",
            $"Emitido em {FormatDateTime(data.GeneratedAt)} por {data.RequestedBy} | Quebra confirmada: ≥ {data.MinimumBreakDuration.TotalMinutes:N1} min",
            HighlightStyle)]));
        return sheetData;
    }

    private static Worksheet CreateWorksheet(
        SheetData sheetData,
        Columns columns,
        string filterReference,
        IReadOnlyList<string> mergedReferences)
    {
        var view = new SheetView { WorkbookViewId = 0U };
        view.Append(new Pane
        {
            VerticalSplit = 5D,
            TopLeftCell = "A6",
            ActivePane = PaneValues.BottomLeft,
            State = PaneStateValues.Frozen
        });
        var sheetViews = new SheetViews(view);
        var autoFilter = new AutoFilter { Reference = filterReference };
        var mergeCells = new MergeCells(
            mergedReferences.Select(reference => new MergeCell { Reference = reference }));
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

    private static Stylesheet CreateStylesheet()
    {
        var numberingFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164U, FormatCode = "dd/mm/yyyy hh:mm:ss" },
            new NumberingFormat { NumberFormatId = 165U, FormatCode = "0.0" },
            new NumberingFormat { NumberFormatId = 166U, FormatCode = "0.00" },
            new NumberingFormat { NumberFormatId = 167U, FormatCode = "0.0%" })
        { Count = 4U };
        var fonts = new Fonts(
            Font(10),
            Font(18, bold: true, color: "FF182B44"),
            Font(10, color: "FF65778C"),
            Font(10, bold: true, color: "FFFFFFFF"),
            Font(10, bold: true, color: "FF182B44"))
        { Count = 5U };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            SolidFill("FF182B44"),
            SolidFill("FFE8F1FC"))
        { Count = 4U };
        var borders = new Borders(
            new Border(),
            new Border(
                BorderSide<LeftBorder>(),
                BorderSide<RightBorder>(),
                BorderSide<TopBorder>(),
                BorderSide<BottomBorder>(),
                new DiagonalBorder()))
        { Count = 2U };
        var cellStyleFormats = new CellStyleFormats(new CellFormat()) { Count = 1U };
        var cellFormats = new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1U },
            new CellFormat { FontId = 2U },
            new CellFormat { FontId = 4U, FillId = 3U },
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
            NumberFormat(164U),
            NumberFormat(165U),
            NumberFormat(166U),
            NumberFormat(0U),
            NumberFormat(167U),
            new CellFormat { BorderId = 1U })
        { Count = 11U };
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

    private static DocumentFormat.OpenXml.Spreadsheet.Font Font(
        double size,
        bool bold = false,
        string? color = null)
    {
        var font = new DocumentFormat.OpenXml.Spreadsheet.Font();
        if (bold) font.Append(new Bold());
        font.Append(new FontSize { Val = size });
        if (color is not null)
            font.Append(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = color });
        font.Append(new FontName { Val = "Calibri" });
        return font;
    }

    private static Fill SolidFill(string color) =>
        new(new PatternFill(
            new ForegroundColor { Rgb = color },
            new BackgroundColor { Indexed = 64U })
        { PatternType = PatternValues.Solid });

    private static T BorderSide<T>() where T : BorderPropertiesType, new() =>
        new()
        {
            Style = BorderStyleValues.Thin,
            Color = new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFD4DEE8" }
        };

    private static CellFormat NumberFormat(uint numberFormatId) =>
        new()
        {
            NumberFormatId = numberFormatId,
            BorderId = 1U,
            ApplyNumberFormat = numberFormatId != 0U
        };

    private static Column Column(uint min, uint max, double width) =>
        new() { Min = min, Max = max, Width = width, CustomWidth = true };

    private static Row Row(uint rowIndex, IEnumerable<Cell> cells)
    {
        var row = new Row { RowIndex = rowIndex };
        row.Append(cells);
        return row;
    }

    private static Cell TextCell(string reference, string value, uint styleIndex) =>
        new()
        {
            CellReference = reference,
            StyleIndex = styleIndex,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(
                new Text(value) { Space = SpaceProcessingModeValues.Preserve })
        };

    private static Cell NumberCell(string reference, double value, uint styleIndex) =>
        new()
        {
            CellReference = reference,
            StyleIndex = styleIndex,
            DataType = CellValues.Number,
            CellValue = new CellValue(value.ToString("R", CultureInfo.InvariantCulture))
        };

    private static Cell OptionalNumberCell(
        string reference,
        double? value,
        uint styleIndex) =>
        value.HasValue && double.IsFinite(value.Value)
            ? NumberCell(reference, value.Value, styleIndex)
            : TextCell(reference, "", TextStyle);

    private static Cell DateCell(string reference, DateTimeOffset value) =>
        NumberCell(reference, value.ToLocalTime().DateTime.ToOADate(), DateStyle);

    private static Cell OptionalDateCell(string reference, DateTimeOffset? value) =>
        value.HasValue
            ? DateCell(reference, value.Value)
            : TextCell(reference, "", TextStyle);

    private static double ValueAt(IReadOnlyList<double> values, int index) =>
        index < values.Count && double.IsFinite(values[index]) ? values[index] : 0D;

    private static int ValueAt(IReadOnlyList<int> values, int index) =>
        index < values.Count ? values[index] : 0;

    private static string ColumnName(int index)
    {
        var result = string.Empty;
        while (index > 0)
        {
            index--;
            result = (char)('A' + index % 26) + result;
            index /= 26;
        }
        return result;
    }

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
}
