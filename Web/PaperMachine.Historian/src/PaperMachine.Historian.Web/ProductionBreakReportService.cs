using System.Globalization;
using System.Reflection;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

public sealed record GeneratedReport(byte[] Content, string FileName);

public interface IProductionBreakReportService
{
    Task<GeneratedReport> GenerateAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        double productiveSpeedMpm,
        string requestedBy,
        CancellationToken cancellationToken);
}

public sealed class ProductionBreakReportService(
    IHistorianRepository repository,
    ReportingOptions options,
    TimeProvider timeProvider,
    ILogger<ProductionBreakReportService> logger) : IProductionBreakReportService
{
    private static readonly CultureInfo Portuguese =
        CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Color Navy = Color.FromRgb(24, 43, 68);
    private static readonly Color Blue = Color.FromRgb(38, 107, 200);
    private static readonly Color Green = Color.FromRgb(31, 137, 99);
    private static readonly Color Red = Color.FromRgb(184, 70, 68);
    private static readonly Color MediumGray = Color.FromRgb(101, 119, 140);
    private static readonly Color LightGray = Color.FromRgb(242, 245, 248);
    private static readonly Color LightBlue = Color.FromRgb(232, 241, 252);

    public async Task<GeneratedReport> GenerateAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        double productiveSpeedMpm,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        ValidateRequest(start, end, productiveSpeedMpm);
        var startUtc = start.ToUniversalTime();
        var endUtc = end.ToUniversalTime();

        var samplesTask = repository.GetMachineProductivitySamplesAsync(
            startUtc,
            endUtc,
            cancellationToken);
        var breaksTask = repository.GetPaperBreakEventsAsync(
            startUtc,
            endUtc,
            options.MaximumBreaks + 1,
            cancellationToken);
        await Task.WhenAll(samplesTask, breaksTask);

        var breaks = await breaksTask;
        if (breaks.Count > options.MaximumBreaks)
            throw new ReportLimitExceededException(
                $"O período possui mais de {options.MaximumBreaks:N0} quebras. Reduza o intervalo do relatório.");

        var productivity = MachineProductivityAnalyzer.Analyze(
            await samplesTask,
            startUtc,
            endUtc,
            productiveSpeedMpm);
        var generatedAt = timeProvider.GetUtcNow();
        var localStart = start.ToLocalTime();
        var localEnd = end.ToLocalTime();
        var analysis = AnalyzeBreaks(
            breaks.OrderBy(item => item.StartedAtUtc).ToArray(),
            productivity,
            endUtc,
            generatedAt);
        var data = new ProductionBreakReportData(
            options.MachineName.Trim(),
            localStart,
            localEnd,
            generatedAt.ToLocalTime(),
            string.IsNullOrWhiteSpace(requestedBy)
                ? "Usuário não identificado"
                : requestedBy.Trim(),
            productiveSpeedMpm,
            productivity,
            analysis);

        cancellationToken.ThrowIfCancellationRequested();
        var content = Render(data);
        var fileName =
            $"Relatorio-Producao-Quebras-{localStart:yyyy-MM-dd}-a-{localEnd:yyyy-MM-dd}.pdf";
        logger.LogInformation(
            "Production and breaks PDF generated. RequestedBy={RequestedBy}, Start={Start}, End={End}, Breaks={Breaks}, Bytes={Bytes}.",
            data.RequestedBy,
            start,
            end,
            breaks.Count,
            content.Length);
        return new GeneratedReport(content, fileName);
    }

    private void ValidateRequest(
        DateTimeOffset start,
        DateTimeOffset end,
        double productiveSpeedMpm)
    {
        if (start == default || end == default)
            throw new ArgumentException("Informe a data inicial e a data final.");
        if (end <= start)
            throw new ArgumentException("A data final deve ser posterior à data inicial.");
        if (end - start > TimeSpan.FromDays(options.MaximumRangeDays))
            throw new ArgumentException(
                $"O período máximo para relatórios é de {options.MaximumRangeDays} dias.");
        if (!double.IsFinite(productiveSpeedMpm) || productiveSpeedMpm is < 0 or > 500)
            throw new ArgumentException(
                "A velocidade mínima produtiva deve estar entre 0 e 500 m/min.");
    }

    private static BreakReportAnalysis AnalyzeBreaks(
        IReadOnlyList<PaperBreakEventRow> breaks,
        MachineProductivityAnalysis productivity,
        DateTimeOffset periodEndUtc,
        DateTimeOffset generatedAtUtc)
    {
        var effectiveNow = generatedAtUtc < periodEndUtc ? generatedAtUtc : periodEndUtc;
        var analyzedBreaks = breaks
            .Select(item =>
            {
                var effectiveEnd = item.EndedAtUtc ?? effectiveNow;
                if (effectiveEnd > periodEndUtc)
                    effectiveEnd = periodEndUtc;
                if (effectiveEnd < item.StartedAtUtc)
                    effectiveEnd = item.StartedAtUtc;
                var duration = item.DurationMilliseconds.HasValue
                    ? item.DurationMilliseconds.Value / 60_000d
                    : (effectiveEnd - item.StartedAtUtc).TotalMinutes;
                return new ReportBreak(item, Math.Max(0, duration));
            })
            .ToArray();

        var hourlyBreaks = new int[24];
        foreach (var item in analyzedBreaks)
            hourlyBreaks[item.Event.StartedAtUtc.ToLocalTime().Hour]++;

        var runsEndingInBreak = productivity.ProductiveRuns
            .Where(run => analyzedBreaks.Any(item =>
                Math.Abs((run.EndUtc - item.Event.StartedAtUtc).TotalMilliseconds) <=
                productivity.SampleInterval.TotalMilliseconds))
            .ToArray();
        var mttr = Average(analyzedBreaks.Select(item => item.DurationMinutes));
        double? mtbf = analyzedBreaks.Length > 0
            ? productivity.ProductiveMinutes / analyzedBreaks.Length
            : null;
        var mttf = Average(runsEndingInBreak.Select(run => run.DurationMinutes));
        var breaksPerOperatingHour = productivity.CoveredMinutes > 0
            ? analyzedBreaks.Length / (productivity.CoveredMinutes / 60)
            : 0;

        var totalBreakMinutes = analyzedBreaks.Sum(item => item.DurationMinutes);
        var paretoGroups = analyzedBreaks
            .GroupBy(
                item => new
                {
                    Category = string.IsNullOrWhiteSpace(item.Event.CauseCategory)
                        ? "Pendente"
                        : item.Event.CauseCategory.Trim(),
                    Cause = string.IsNullOrWhiteSpace(item.Event.CauseDescription)
                        ? "Causa pendente de análise"
                        : item.Event.CauseDescription.Trim()
                })
            .Select(group => new
            {
                group.Key.Category,
                group.Key.Cause,
                Count = group.Count(),
                DurationMinutes = group.Sum(item => item.DurationMinutes)
            })
            .OrderByDescending(item => item.DurationMinutes)
            .ThenByDescending(item => item.Count)
            .ToArray();
        var cumulative = 0d;
        var pareto = new List<BreakParetoRow>(paretoGroups.Length);
        foreach (var group in paretoGroups)
        {
            cumulative += group.DurationMinutes;
            pareto.Add(new BreakParetoRow(
                group.Cause,
                group.Category,
                group.Count,
                group.DurationMinutes,
                totalBreakMinutes > 0 ? cumulative / totalBreakMinutes * 100 : 0));
        }

        return new BreakReportAnalysis(
            analyzedBreaks,
            pareto,
            hourlyBreaks,
            mttr,
            mtbf,
            mttf,
            runsEndingInBreak.Length,
            breaksPerOperatingHour,
            analyzedBreaks.Length > 0
                ? analyzedBreaks.Max(item => item.DurationMinutes)
                : null,
            analyzedBreaks.LastOrDefault()?.DurationMinutes);
    }

    private static double? Average(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? null : array.Average();
    }

    private static byte[] Render(ProductionBreakReportData data)
    {
        EmbeddedReportFontResolver.EnsureRegistered();
        var document = BuildDocument(data);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        return output.ToArray();
    }

    private static Document BuildDocument(ProductionBreakReportData data)
    {
        var document = new Document();
        document.Info.Title = "Relatório operacional de produção e quebras";
        document.Info.Author = "CPNTeck Paper Machine Historian";
        ConfigureStyles(document);

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.1);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.FooterDistance = Unit.FromCentimeter(0.55);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.2);
        AddHeader(section, data);
        AddFooter(section);
        AddSummary(section, data);
        AddHourlyPerformance(section, data);
        AddPareto(section, data.Breaks.Pareto);
        AddBreaksTable(section, data.Breaks.Breaks);
        return document;
    }

    private static void ConfigureStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedReportFontResolver.FamilyName;
        normal.Font.Size = Unit.FromPoint(8);
        normal.Font.Color = Navy;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
        var heading = document.Styles[StyleNames.Heading1]!;
        heading.Font.Name = EmbeddedReportFontResolver.FamilyName;
        heading.Font.Size = Unit.FromPoint(14);
        heading.Font.Bold = true;
        heading.Font.Color = Navy;
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        heading.ParagraphFormat.KeepWithNext = true;
    }

    private static void AddHeader(Section section, ProductionBreakReportData data)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn(Unit.FromCentimeter(5));
        table.AddColumn(Unit.FromCentimeter(14));
        table.AddColumn(Unit.FromCentimeter(8.2));
        var row = table.AddRow();
        var logo = row.Cells[0].AddImage(
            GetEmbeddedImageDataUri("PaperMachine.Reporting.CPNTeck-Logo.png"));
        logo.Width = Unit.FromCentimeter(3.8);
        logo.LockAspectRatio = true;

        var title = row.Cells[1].AddParagraph();
        title.AddFormattedText(
            "RELATÓRIO OPERACIONAL DE PRODUÇÃO E QUEBRAS",
            TextFormat.Bold);
        title.Format.Font.Size = Unit.FromPoint(15);
        title.Format.Font.Color = Navy;
        title.AddLineBreak();
        title.AddFormattedText(data.MachineName, TextFormat.NotBold);

        var metadata = row.Cells[2].AddParagraph();
        metadata.Format.Alignment = ParagraphAlignment.Right;
        metadata.Format.Font.Size = Unit.FromPoint(7.4);
        metadata.AddText(
            $"Período: {FormatDateTime(data.Start)} até {FormatDateTime(data.End)}");
        metadata.AddLineBreak();
        metadata.AddText($"Emitido em: {FormatDateTime(data.GeneratedAt)}");
        metadata.AddLineBreak();
        metadata.AddText($"Solicitado por: {data.RequestedBy}");
        metadata.AddLineBreak();
        metadata.AddText(
            $"Regra: papel presente + {FormatNumber(data.ProductiveSpeedMpm, 1)} m/min");

        var separator = section.AddParagraph();
        separator.Format.Borders.Bottom.Width = Unit.FromPoint(1.2);
        separator.Format.Borders.Bottom.Color = Blue;
        separator.Format.SpaceAfter = Unit.FromPoint(8);
    }

    private static void AddFooter(Section section)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = Unit.FromPoint(7);
        footer.Format.Font.Color = MediumGray;
        footer.AddText("CPNTeck - Paper Machine Historian");
        footer.AddTab();
        footer.AddText("Página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();
        footer.Format.TabStops.AddTabStop(
            Unit.FromCentimeter(25.5),
            TabAlignment.Right);
    }

    private static void AddSummary(Section section, ProductionBreakReportData data)
    {
        section.AddParagraph("Resumo do período", StyleNames.Heading1);
        var productivity = data.Productivity;
        var breaks = data.Breaks;
        var values = new[]
        {
            ("Produtividade", productivity.CoveredMinutes > 0
                ? $"{FormatNumber(productivity.ProductivityPercent, 1)}%"
                : "-"),
            ("Tempo coberto", FormatDuration(productivity.CoveredMinutes)),
            ("Tempo produtivo", FormatDuration(productivity.ProductiveMinutes)),
            ("Sem produção", FormatDuration(productivity.UnproductiveMinutes)),
            ("Papel presente", productivity.CoveredMinutes > 0
                ? $"{FormatNumber(productivity.PaperPresencePercent, 1)}%"
                : "-"),
            ("Média produtiva", $"{FormatNumber(productivity.ProductiveAverageSpeed, 1)} m/min"),
            ("Média geral", $"{FormatNumber(productivity.GeneralAverageSpeed, 1)} m/min"),
            ("Velocidade máxima", $"{FormatNumber(productivity.MaximumSpeed, 1)} m/min"),
            ("Quebras", breaks.Breaks.Count.ToString(Portuguese)),
            ("Quebras por hora", FormatNumber(breaks.BreaksPerOperatingHour, 2)),
            ("MTTR", FormatDuration(breaks.MttrMinutes)),
            ("MTBF", FormatDuration(breaks.MtbfMinutes)),
            ("MTTF", FormatDuration(breaks.MttfMinutes)),
            ("Máximo sem quebra", FormatDuration(productivity.LongestProductiveRunMinutes)),
            ("Última quebra", FormatDuration(breaks.LastBreakMinutes))
        };

        var table = section.AddTable();
        table.Borders.Width = Unit.FromPoint(0.35);
        table.Borders.Color = Color.FromRgb(206, 216, 226);
        for (var index = 0; index < 5; index++)
            table.AddColumn(Unit.FromCentimeter(5.4));
        for (var rowIndex = 0; rowIndex < 3; rowIndex++)
        {
            var row = table.AddRow();
            row.Height = Unit.FromCentimeter(1.05);
            for (var columnIndex = 0; columnIndex < 5; columnIndex++)
            {
                var item = values[rowIndex * 5 + columnIndex];
                var cell = row.Cells[columnIndex];
                cell.Shading.Color = rowIndex % 2 == 0 ? LightBlue : LightGray;
                cell.VerticalAlignment = VerticalAlignment.Center;
                var paragraph = cell.AddParagraph();
                paragraph.Format.LeftIndent = Unit.FromMillimeter(1.6);
                paragraph.AddFormattedText(item.Item1.ToUpper(Portuguese), TextFormat.Bold);
                paragraph.Format.Font.Size = Unit.FromPoint(6.2);
                paragraph.Format.Font.Color = MediumGray;
                paragraph.AddLineBreak();
                var value = paragraph.AddFormattedText(item.Item2, TextFormat.Bold);
                value.Font.Size = Unit.FromPoint(10.5);
                value.Font.Color = Navy;
            }
        }

        var note = section.AddParagraph();
        note.Format.SpaceBefore = Unit.FromPoint(5);
        note.Format.Font.Size = Unit.FromPoint(7.2);
        note.Format.Font.Color = MediumGray;
        note.AddText(
            $"Maior quebra: {FormatDuration(breaks.LongestBreakMinutes)} | " +
            $"MTTF com sequência produtiva identificada: {breaks.FailuresWithOperatingRun} | " +
            $"Ciclo inferido: {FormatNumber(productivity.SampleInterval.TotalSeconds, 0)} s");
    }

    private static void AddHourlyPerformance(
        Section section,
        ProductionBreakReportData data)
    {
        section.AddParagraph("Desempenho por hora", StyleNames.Heading1);
        var table = section.AddTable();
        table.Borders.Width = Unit.FromPoint(0.35);
        table.Borders.Color = Color.FromRgb(211, 220, 229);
        table.AddColumn(Unit.FromCentimeter(2.7));
        for (var index = 0; index < 12; index++)
            table.AddColumn(Unit.FromCentimeter(2.05));

        for (var block = 0; block < 2; block++)
        {
            var hours = table.AddRow();
            hours.Shading.Color = Navy;
            AddText(hours.Cells[0], "Hora", ParagraphAlignment.Left, true, Colors.White);
            for (var column = 0; column < 12; column++)
            {
                var hour = block * 12 + column;
                AddText(
                    hours.Cells[column + 1],
                    $"{hour:00}h",
                    ParagraphAlignment.Center,
                    true,
                    Colors.White);
            }

            AddHourlyRow(
                table,
                "Produtividade",
                block,
                hour => $"{FormatNumber(data.Productivity.HourlyProductivity[hour], 1)}%");
            AddHourlyRow(
                table,
                "Quebras",
                block,
                hour => data.Breaks.HourlyBreaks[hour].ToString(Portuguese));
            AddHourlyRow(
                table,
                "Sem produção",
                block,
                hour => $"{FormatNumber(data.Productivity.HourlyUnproductiveMinutes[hour], 1)} min");
        }
    }

    private static void AddHourlyRow(
        Table table,
        string label,
        int block,
        Func<int, string> valueFactory)
    {
        var row = table.AddRow();
        AddText(row.Cells[0], label, bold: true);
        for (var column = 0; column < 12; column++)
        {
            var hour = block * 12 + column;
            AddText(
                row.Cells[column + 1],
                valueFactory(hour),
                ParagraphAlignment.Center);
        }
    }

    private static void AddPareto(
        Section section,
        IReadOnlyList<BreakParetoRow> items)
    {
        section.AddParagraph("Pareto das causas de quebra", StyleNames.Heading1);
        if (items.Count == 0)
        {
            AddEmptyMessage(section, "Nenhuma quebra registrada no período analisado.");
            return;
        }

        var table = CreateTable(section, [10.2, 5.6, 2.5, 4.2, 4.5]);
        AddHeaderRow(
            table,
            ["Causa", "Categoria", "Ocorrências", "Tempo", "% acumulado"]);
        foreach (var item in items)
        {
            var row = table.AddRow();
            if (item.Cause == "Causa pendente de análise")
                row.Shading.Color = Color.FromRgb(255, 241, 239);
            AddText(
                row.Cells[0],
                item.Cause,
                color: item.Cause == "Causa pendente de análise" ? Red : null);
            AddText(row.Cells[1], item.Category);
            AddText(
                row.Cells[2],
                item.Count.ToString(Portuguese),
                ParagraphAlignment.Center);
            AddText(
                row.Cells[3],
                FormatDuration(item.DurationMinutes),
                ParagraphAlignment.Right);
            AddText(
                row.Cells[4],
                $"{FormatNumber(item.CumulativePercent, 1)}%",
                ParagraphAlignment.Right);
        }
    }

    private static void AddBreaksTable(
        Section section,
        IReadOnlyList<ReportBreak> breaks)
    {
        section.AddParagraph("Detalhamento das quebras", StyleNames.Heading1);
        if (breaks.Count == 0)
        {
            AddEmptyMessage(section, "Não houve quebras no período selecionado.");
            return;
        }

        var table = CreateTable(section, [2.6, 2.6, 1.8, 2.1, 4, 5.6, 3.3, 5]);
        AddHeaderRow(
            table,
            [
                "Início",
                "Fim",
                "Duração",
                "Velocidade T0",
                "Categoria",
                "Causa",
                "Análise",
                "Responsável / observação"
            ]);
        foreach (var item in breaks.OrderByDescending(row => row.Event.StartedAtUtc))
        {
            var row = table.AddRow();
            row.VerticalAlignment = VerticalAlignment.Top;
            var pending = string.IsNullOrWhiteSpace(item.Event.CauseDescription);
            if (pending)
                row.Shading.Color = Color.FromRgb(255, 241, 239);
            AddText(row.Cells[0], FormatDateTime(item.Event.StartedAtUtc.ToLocalTime()));
            AddText(
                row.Cells[1],
                item.Event.EndedAtUtc.HasValue
                    ? FormatDateTime(item.Event.EndedAtUtc.Value.ToLocalTime())
                    : "Em andamento");
            AddText(
                row.Cells[2],
                FormatDuration(item.DurationMinutes),
                ParagraphAlignment.Right);
            AddText(
                row.Cells[3],
                $"{FormatNumber(item.Event.SpeedAtStartMpm, 1)} m/min",
                ParagraphAlignment.Right);
            AddText(
                row.Cells[4],
                item.Event.CauseCategory ?? "Pendente");
            AddText(
                row.Cells[5],
                item.Event.CauseDescription ?? "Causa pendente de análise",
                color: pending ? Red : null);
            AddText(row.Cells[6], item.Event.AnalysisStatus);
            AddText(
                row.Cells[7],
                BuildAnalysisDetails(item.Event));
        }
    }

    private static string BuildAnalysisDetails(PaperBreakEventRow item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.AnalyzedBy))
            parts.Add(item.AnalyzedBy.Trim());
        if (!string.IsNullOrWhiteSpace(item.AnalysisNotes))
            parts.Add(item.AnalysisNotes.Trim());
        return parts.Count == 0 ? "-" : string.Join("\n", parts);
    }

    private static Table CreateTable(
        Section section,
        IReadOnlyList<double> widthsCentimeters)
    {
        var table = section.AddTable();
        table.Borders.Width = Unit.FromPoint(0.35);
        table.Borders.Color = Color.FromRgb(201, 211, 221);
        table.Rows.LeftIndent = Unit.Zero;
        foreach (var width in widthsCentimeters)
            table.AddColumn(Unit.FromCentimeter(width));
        return table;
    }

    private static void AddHeaderRow(Table table, IReadOnlyList<string> labels)
    {
        var row = table.AddRow();
        row.HeadingFormat = true;
        row.Shading.Color = Navy;
        row.VerticalAlignment = VerticalAlignment.Center;
        for (var index = 0; index < labels.Count; index++)
            AddText(
                row.Cells[index],
                labels[index],
                ParagraphAlignment.Left,
                true,
                Colors.White);
    }

    private static void AddText(
        Cell cell,
        string text,
        ParagraphAlignment alignment = ParagraphAlignment.Left,
        bool bold = false,
        Color? color = null)
    {
        var paragraph = cell.AddParagraph();
        paragraph.Format.Alignment = alignment;
        paragraph.Format.LeftIndent = Unit.FromMillimeter(1);
        paragraph.Format.RightIndent = Unit.FromMillimeter(1);
        paragraph.Format.SpaceBefore = Unit.FromMillimeter(0.65);
        paragraph.Format.SpaceAfter = Unit.FromMillimeter(0.65);
        paragraph.Format.Font.Size = Unit.FromPoint(6.5);
        paragraph.Format.Font.Bold = bold;
        if (color.HasValue)
            paragraph.Format.Font.Color = color.Value;
        paragraph.AddText(text);
    }

    private static void AddEmptyMessage(Section section, string message)
    {
        var paragraph = section.AddParagraph(message);
        paragraph.Format.Shading.Color = LightGray;
        paragraph.Format.Font.Color = MediumGray;
        paragraph.Format.SpaceBefore = Unit.FromPoint(3);
        paragraph.Format.SpaceAfter = Unit.FromPoint(6);
        paragraph.Format.LeftIndent = Unit.FromPoint(8);
    }

    private static string GetEmbeddedImageDataUri(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"O recurso incorporado de relatório '{resourceName}' não foi encontrado.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return $"base64:{Convert.ToBase64String(memory.ToArray())}";
    }

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("dd/MM/yyyy HH:mm", Portuguese);

    private static string FormatNumber(double value, int decimals) =>
        value.ToString($"N{decimals}", Portuguese);

    private static string FormatDuration(double? minutes)
    {
        if (minutes is null || !double.IsFinite(minutes.Value))
            return "-";
        if (minutes.Value < 60)
            return $"{FormatNumber(minutes.Value, 1)} min";
        var hours = (int)Math.Floor(minutes.Value / 60);
        var remaining = (int)Math.Round(minutes.Value % 60);
        if (remaining == 60)
        {
            hours++;
            remaining = 0;
        }
        return $"{hours:N0}h {remaining:00}min";
    }

    private sealed record ProductionBreakReportData(
        string MachineName,
        DateTimeOffset Start,
        DateTimeOffset End,
        DateTimeOffset GeneratedAt,
        string RequestedBy,
        double ProductiveSpeedMpm,
        MachineProductivityAnalysis Productivity,
        BreakReportAnalysis Breaks);
}

public sealed class ReportLimitExceededException(string message) : Exception(message);

public sealed record ReportBreak(
    PaperBreakEventRow Event,
    double DurationMinutes);

public sealed record BreakParetoRow(
    string Cause,
    string Category,
    int Count,
    double DurationMinutes,
    double CumulativePercent);

public sealed record BreakReportAnalysis(
    IReadOnlyList<ReportBreak> Breaks,
    IReadOnlyList<BreakParetoRow> Pareto,
    IReadOnlyList<int> HourlyBreaks,
    double? MttrMinutes,
    double? MtbfMinutes,
    double? MttfMinutes,
    int FailuresWithOperatingRun,
    double BreaksPerOperatingHour,
    double? LongestBreakMinutes,
    double? LastBreakMinutes);
