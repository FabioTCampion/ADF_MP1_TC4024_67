using System.Text.Json;
using System.Text.Json.Nodes;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace PaperMachine.Historian.Infrastructure.Ads;

public sealed class AdsPaperMachineReader : IPaperMachineReader
{
    private readonly AdsClient _client = new();
    private readonly AdsOptions _options;
    private readonly HistorianOptions _historianOptions;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private IReadOnlyDictionary<string, ISymbol>? _rootSymbols;

    public AdsPaperMachineReader(AdsOptions options, HistorianOptions historianOptions)
    {
        options.Validate();
        historianOptions.Validate();
        _options = options;
        _historianOptions = historianOptions;
    }

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
            return;

        using var timeout = CreateTimeout(cancellationToken);
        await _client.ConnectAsync(new AmsNetId(_options.AmsNetId), _options.Port, timeout.Token);
        LoadRootSymbols();
    }

    public async Task<PaperMachineSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
            throw new InvalidOperationException("ADS client is not connected.");

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            LoadRootSymbols();
            using var timeout = CreateTimeout(cancellationToken);
            var status = await ReadFlatStructureAsync(_options.StatusRoot, timeout.Token);
            var commands = await ReadFlatStructureAsync(_options.CommandsRoot, timeout.Token);
            var alarms = await ReadFlatStructureAsync(_options.AlarmsRoot, timeout.Token);

            return new PaperMachineSnapshot(
                DateTimeOffset.UtcNow,
                status,
                commands,
                alarms,
                _historianOptions.MappingVersion);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _rootSymbols = null;
        if (_client.IsConnected)
            _client.Disconnect();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _operationLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<JsonElement> ReadFlatStructureAsync(string configuredRoot, CancellationToken cancellationToken)
    {
        var symbol = _rootSymbols![configuredRoot];
        if (symbol is not IValueSymbol valueSymbol)
            throw new InvalidOperationException($"ADS structure symbol '{symbol.InstancePath}' is not readable.");

        var result = await valueSymbol.ReadValueAsync(cancellationToken);
        if (result.Failed)
            throw new InvalidOperationException(
                $"ADS read failed for '{symbol.InstancePath}' with error '{result.ErrorCode}'.");
        if (result.Value is not IDynamicValue dynamicValue)
            throw new InvalidOperationException(
                $"ADS structure '{symbol.InstancePath}' did not return a dynamic structure value.");

        var payload = new JsonObject();
        foreach (var child in symbol.SubSymbols)
        {
            var memberName = GetMemberName(symbol.InstancePath, child.InstancePath);
            if (!dynamicValue.TryGetMemberValue(memberName, out var memberValue))
                throw new InvalidOperationException(
                    $"ADS member '{child.InstancePath}' was not present in the structure value.");
            payload[memberName] = ToJsonValue(memberValue, child.InstancePath);
        }

        return JsonSerializer.SerializeToElement(payload);
    }

    private void LoadRootSymbols()
    {
        if (_rootSymbols is not null)
            return;

        var loader = SymbolLoaderFactory.Create(_client, SymbolLoaderSettings.DefaultDynamic);
        var symbols = loader.Symbols.SelectMany(Flatten).ToArray();
        _rootSymbols = new Dictionary<string, ISymbol>(StringComparer.OrdinalIgnoreCase)
        {
            [_options.StatusRoot] = FindRootSymbol(symbols, _options.StatusRoot),
            [_options.CommandsRoot] = FindRootSymbol(symbols, _options.CommandsRoot),
            [_options.AlarmsRoot] = FindRootSymbol(symbols, _options.AlarmsRoot)
        };
    }

    private static ISymbol FindRootSymbol(IEnumerable<ISymbol> symbols, string configuredRoot)
    {
        var normalized = configuredRoot.Trim();
        var exact = symbols.FirstOrDefault(symbol =>
            string.Equals(symbol.InstancePath, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var suffix = normalized.TrimStart('.');
        var candidates = symbols
            .Where(symbol =>
                string.Equals(symbol.InstancePath, suffix, StringComparison.OrdinalIgnoreCase) ||
                symbol.InstancePath.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException($"ADS structure symbol '{configuredRoot}' was not found."),
            _ => throw new InvalidOperationException(
                $"ADS structure root '{configuredRoot}' is ambiguous: " +
                string.Join(", ", candidates.Select(item => item.InstancePath)))
        };
    }

    private static IEnumerable<ISymbol> Flatten(ISymbol symbol)
    {
        yield return symbol;
        foreach (var child in symbol.SubSymbols)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }

    private static string GetMemberName(string rootPath, string memberPath)
    {
        if (memberPath.StartsWith($"{rootPath}.", StringComparison.OrdinalIgnoreCase))
            return memberPath[(rootPath.Length + 1)..];
        var separator = memberPath.LastIndexOf('.');
        return separator >= 0 ? memberPath[(separator + 1)..] : memberPath;
    }

    private static JsonNode? ToJsonValue(object? value, string path) => value switch
    {
        null => null,
        bool item => JsonValue.Create(item),
        byte item => JsonValue.Create(item),
        sbyte item => JsonValue.Create(item),
        short item => JsonValue.Create(item),
        ushort item => JsonValue.Create(item),
        int item => JsonValue.Create(item),
        uint item => JsonValue.Create(item),
        long item => JsonValue.Create(item),
        ulong item => JsonValue.Create(item),
        float item when float.IsFinite(item) => JsonValue.Create(item),
        double item when double.IsFinite(item) => JsonValue.Create(item),
        decimal item => JsonValue.Create(item),
        string item => JsonValue.Create(item),
        char item => JsonValue.Create(item.ToString()),
        DateTime item => JsonValue.Create(item),
        DateTimeOffset item => JsonValue.Create(item),
        _ => throw new InvalidOperationException(
            $"ADS member '{path}' has unsupported value type '{value.GetType().FullName}'.")
    };

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeoutMilliseconds);
        return timeout;
    }
}
