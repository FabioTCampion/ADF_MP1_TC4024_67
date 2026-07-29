using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Application;

public static partial class AlarmCatalog
{
    public const string Version = "pt-BR-2026.07-v1";

    public static AlarmDefinition Resolve(string alarmName)
    {
        var area = ResolveArea(alarmName);
        var equipment = ResolveEquipment(alarmName);

        if (string.Equals(alarmName, "alarmIndication", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                "Indicação geral de alarme",
                "O CLP informa que existe pelo menos uma condição de alarme ativa na máquina.",
                "Abra a consulta de alarmes, identifique a ocorrência ativa e siga o procedimento do equipamento.",
                "Informativo",
                "Máquina");
        }

        if (alarmName.EndsWith("SafetyAlarm", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Circuito de segurança — {equipment}",
                "O circuito de segurança desta área não está liberado. O movimento pode permanecer bloqueado.",
                "Verifique botões de emergência, proteções, relés de segurança e a liberação da área antes de tentar novo acionamento.",
                "Crítico",
                area);
        }

        if (alarmName.EndsWith("CoolingFaultAlarm", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Falha de refrigeração — {equipment}",
                "O circuito de ventilação ou refrigeração associado ao acionamento não confirmou operação normal.",
                "Verifique ventiladores, filtros, alimentação, contatores e temperatura antes de liberar novamente o equipamento.",
                "Alto",
                area);
        }

        if (alarmName.Contains("OverrunOperatorSide", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Desalinhamento no lado operador — {equipment}",
                "A tela, feltro ou tecido ultrapassou o limite permitido no lado do operador.",
                "Reduza a condição de risco, verifique sensores e o sistema de alinhamento antes de continuar a produção.",
                "Alto",
                area);
        }

        if (alarmName.Contains("OverrunDriveSide", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Desalinhamento no lado acionamento — {equipment}",
                "A tela, feltro ou tecido ultrapassou o limite permitido no lado do acionamento.",
                "Reduza a condição de risco, verifique sensores e o sistema de alinhamento antes de continuar a produção.",
                "Alto",
                area);
        }

        if (alarmName.EndsWith("PaperBreakAlarm", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Rompimento de papel — {equipment}",
                "O sensor de presença deixou de detectar a folha durante a operação.",
                "Localize o ponto do rompimento, remova material solto e confirme que a passagem está segura antes da retomada.",
                "Médio",
                area);
        }

        if (alarmName.Contains("HighTemperature", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Temperatura elevada — {equipment}",
                "A temperatura medida ultrapassou o limite configurado para operação.",
                "Verifique circulação, nível e condição do óleo, trocador de calor e o instrumento de temperatura.",
                "Alto",
                area);
        }

        if (alarmName.Contains("MinPressure", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Pressão insuficiente — {equipment}",
                "A pressão mínima necessária não foi atingida ou não se manteve durante a operação.",
                "Verifique bombas, nível, vazamentos, filtros, válvulas e o transmissor de pressão.",
                "Alto",
                area);
        }

        if (alarmName.Contains("MaxPressure", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Pressão elevada — {equipment}",
                "A pressão do circuito ultrapassou o limite máximo configurado.",
                "Verifique obstruções, válvulas, regulagem e o instrumento de pressão antes de religar o sistema.",
                "Alto",
                area);
        }

        if (alarmName.Contains("SpeedDif", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Diferença de velocidade — {equipment}",
                "A diferença de velocidade entre acionamentos relacionados ultrapassou a tolerância configurada.",
                "Verifique referências, feedbacks, acoplamentos, carga mecânica e ajuste de sincronismo.",
                "Alto",
                area);
        }

        if (alarmName.Contains("Stretcher", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Falha no esticador — {equipment}",
                "O sistema de esticamento não confirmou a condição esperada ou o motor associado entrou em falha.",
                "Verifique fins de curso, sensores, motor, redutor e possíveis travamentos mecânicos.",
                "Alto",
                area);
        }

        if (alarmName.Contains("PickUpRollPos", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Posição incorreta do rolo pick-up — {equipment}",
                "O rolo pick-up não atingiu ou não manteve a posição esperada.",
                "Verifique sensores de posição, atuadores, pressão de comando e interferências mecânicas.",
                "Médio",
                area);
        }

        if (alarmName.Contains("ScraperOsc", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Falha na oscilação do raspador — {equipment}",
                "O mecanismo de oscilação do raspador não confirmou movimento normal.",
                "Verifique motor, redutor, sensores, acoplamento e travamento do mecanismo antes da liberação.",
                "Médio",
                area);
        }

        if (alarmName.EndsWith("FaultAlarm", StringComparison.OrdinalIgnoreCase))
        {
            return Definition(
                $"Falha — {equipment}",
                "O equipamento informou uma condição de falha e pode ter bloqueado o acionamento.",
                "Confirme o estado elétrico e mecânico. Para inversores, consulte também o código C2000 Plus registrado nesta ocorrência.",
                "Alto",
                area);
        }

        return Definition(
            $"Alarme — {equipment}",
            "O CLP detectou uma condição fora do estado normal de operação.",
            "Verifique o equipamento indicado, a condição de processo e os sinais associados antes da retomada.",
            "Médio",
            area);
    }

    private static AlarmDefinition Definition(
        string displayName,
        string description,
        string action,
        string severity,
        string area) =>
        new(displayName, description, action, severity, area, Version);

    private static string ResolveArea(string alarmName)
    {
        if (alarmName.StartsWith("forming", StringComparison.OrdinalIgnoreCase))
            return "Mesa formadora";
        if (alarmName.StartsWith("press", StringComparison.OrdinalIgnoreCase))
            return "Prensas";
        if (alarmName.Contains("drying", StringComparison.OrdinalIgnoreCase) ||
            alarmName.StartsWith("upperDrying", StringComparison.OrdinalIgnoreCase) ||
            alarmName.StartsWith("lowerDrying", StringComparison.OrdinalIgnoreCase) ||
            alarmName.StartsWith("lubrication", StringComparison.OrdinalIgnoreCase))
            return "Secagem";
        if (alarmName.StartsWith("winder", StringComparison.OrdinalIgnoreCase))
            return "Enroladeira";
        return "Preparação de massa e utilidades";
    }

    private static string ResolveEquipment(string alarmName)
    {
        var baseName = alarmName
            .Replace("CoolingFaultAlarm", "", StringComparison.OrdinalIgnoreCase)
            .Replace("OverrunOperatorSideAlarm", "", StringComparison.OrdinalIgnoreCase)
            .Replace("OverrunDriveSideAlarm", "", StringComparison.OrdinalIgnoreCase)
            .Replace("HighTemperatureAlarm", "", StringComparison.OrdinalIgnoreCase)
            .Replace("PaperBreakAlarm", "", StringComparison.OrdinalIgnoreCase)
            .Replace("SafetyAlarm", "", StringComparison.OrdinalIgnoreCase)
            .Replace("FaultAlarm", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Alarm", "", StringComparison.OrdinalIgnoreCase);

        var knownName = baseName.ToLowerInvariant() switch
        {
            "formingboardsuctionroll" => "Rolo de sucção da mesa formadora",
            "formingboardtractionroll" => "Rolo de tração da mesa formadora",
            "presssectionfirstpress" => "Primeira prensa",
            "presssectionsecondpress" => "Segunda prensa",
            "couchpitpump" => "Bomba do poço couch",
            "wirepitpump" => "Bomba do poço da tela",
            "stockpump" => "Bomba de massa",
            "mixingpump" => "Bomba de mistura",
            "winderdrive" => "Acionamento da enroladeira",
            _ => null
        };
        if (knownName is not null)
            return knownName;

        var dryingDrive = DryingDriveName().Match(baseName);
        if (dryingDrive.Success)
        {
            var level = dryingDrive.Groups["level"].Value.Equals(
                "Upper",
                StringComparison.OrdinalIgnoreCase)
                ? "superior"
                : "inferior";
            var role = dryingDrive.Groups["role"].Value.Equals(
                "Master",
                StringComparison.OrdinalIgnoreCase)
                ? "mestre"
                : $"escravo {dryingDrive.Groups["number"].Value}";
            return $"Secagem grupo {dryingDrive.Groups["group"].Value} — {role} {level}";
        }

        var words = CamelCaseWords().Matches(baseName)
            .Select(match => TranslateToken(match.Value))
            .Where(token => token.Length > 0);
        var text = string.Join(' ', words);
        return string.IsNullOrWhiteSpace(text) ? baseName : UppercaseFirst(text);
    }

    private static string TranslateToken(string token) => token.ToLowerInvariant() switch
    {
        "forming" => "mesa",
        "board" => "formadora",
        "press" => "prensa",
        "first" => "primeira",
        "second" => "segunda",
        "section" => "",
        "drying" => "secagem",
        "group" => "grupo",
        "upper" => "superior",
        "lower" => "inferior",
        "master" => "mestre",
        "slave" => "escravo",
        "suction" => "sucção",
        "traction" => "tração",
        "roll" => "rolo",
        "wire" => "tela",
        "felt" => "feltro",
        "fabric" => "tecido",
        "operator" => "operador",
        "drive" => "acionamento",
        "side" => "lado",
        "stretch" => "esticamento",
        "stretcher" => "esticador",
        "motor" => "motor",
        "infeed" => "entrada",
        "paper" => "papel",
        "break" => "rompimento",
        "pick" => "pick-up",
        "up" => "",
        "pos" => "posição",
        "baby" => "baby",
        "dryer" => "secador",
        "scraper" => "raspador",
        "osc" => "oscilação",
        "cps" => "depurador centrífugo",
        "unit" => "",
        "couch" => "couch",
        "pit" => "poço",
        "pump" => "bomba",
        "stock" => "massa",
        "mixing" => "mistura",
        "vacuum" => "vácuo",
        "low" => "baixo",
        "fan" => "exaustor",
        "lubrication" => "lubrificação",
        "tank" => "tanque",
        "circuit" => "circuito",
        "temperature" => "temperatura",
        "pressure" => "pressão",
        "min" => "mínima",
        "max" => "máxima",
        "speed" => "velocidade",
        "dif" => "diferença",
        "winder" => "enroladeira",
        _ => token
    };

    private static string UppercaseFirst(string text)
    {
        var builder = new StringBuilder(text);
        builder[0] = char.ToUpper(builder[0]);
        return builder.ToString();
    }

    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+")]
    private static partial Regex CamelCaseWords();

    [GeneratedRegex(
        @"^dryingSectionGroup(?<group>\d)(?<level>Upper|Lower)(?<role>Master|Slave(?<number>\d+))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DryingDriveName();
}

public static class DriveDiagnosticCatalog
{
    public const string Model = "Delta C2000 Plus";
    public const string ManualReference =
        "Delta CMC-EC01 EtherCAT Operation Manual — objeto 603Fh (CiA 402 Error Code)";

    private static readonly IReadOnlyDictionary<string, string> AlarmToStatusPrefix =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["formingBoardSuctionRollFaultAlarm"] = "formingBoardSuctionRoll",
            ["formingBoardTractionRollFaultAlarm"] = "formingBoardTractionRoll",
            ["couchPitPumpFaultAlarm"] = "couchPitPump",
            ["wirePitPumpFaultAlarm"] = "wirePitPump",
            ["stockPumpFaultAlarm"] = "stockPump",
            ["mixingPumpFaultAlarm"] = "mixingPump",
            ["pressSectionFirstPressFaultAlarm"] = "pressSectionFirstPress",
            ["pressSectionSecondPressFaultAlarm"] = "pressSectionSecondPress",
            ["dryingSectionGroup1UpperMasterFaultAlarm"] = "dryingSectionGroup1UpperMaster",
            ["dryingSectionGroup1UpperSlave1FaultAlarm"] = "dryingSectionGroup1UpperSlave1",
            ["dryingSectionGroup1LowerMasterFaultAlarm"] = "dryingSectionGroup1LowerMaster",
            ["dryingSectionGroup2UpperMasterFaultAlarm"] = "dryingSectionGroup2UpperMaster",
            ["dryingSectionGroup2UpperSlave1FaultAlarm"] = "dryingSectionGroup2UpperSlave1",
            ["dryingSectionGroup2LowerMasterFaultAlarm"] = "dryingSectionGroup2LowerMaster",
            ["dryingSectionGroup2LowerSlave1FaultAlarm"] = "dryingSectionGroup2LowerSlave1",
            ["dryingSectionGroup3UpperMasterFaultAlarm"] = "dryingSectionGroup3UpperMaster",
            ["dryingSectionGroup3UpperSlave1FaultAlarm"] = "dryingSectionGroup3UpperSlave1",
            ["dryingSectionGroup3LowerMasterFaultAlarm"] = "dryingSectionGroup3LowerMaster",
            ["dryingSectionGroup3LowerSlave1FaultAlarm"] = "dryingSectionGroup3LowerSlave1",
            ["winderDriveFaultAlarm"] = "winder"
        };

    public static DriveFaultContext? Resolve(string alarmName, JsonElement status)
    {
        if (!AlarmToStatusPrefix.TryGetValue(alarmName, out var prefix))
            return null;

        if (!TryReadUInt16(status, $"{prefix}FaultCode", out var code) ||
            !TryReadDouble(status, $"{prefix}FaultTorque", out var torque) ||
            !TryReadUInt32(status, $"{prefix}FaultEventCounter", out var eventCounter))
            return null;

        var definition = Cia402ErrorCodeCatalog.Resolve(code);
        return new DriveFaultContext(
            Model,
            code,
            $"0x{code:X4}",
            definition.Mnemonic,
            definition.Title,
            definition.Description,
            definition.RecommendedAction,
            torque,
            eventCounter,
            ManualReference);
    }

    private static bool TryReadUInt16(JsonElement status, string name, out ushort value)
    {
        value = 0;
        return status.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetUInt16(out value);
    }

    private static bool TryReadUInt32(JsonElement status, string name, out uint value)
    {
        value = 0;
        return status.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetUInt32(out value);
    }

    private static bool TryReadDouble(JsonElement status, string name, out double value)
    {
        value = 0;
        return status.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }
}

public sealed record Cia402ErrorCodeDefinition(
    string? Mnemonic,
    string Title,
    string Description,
    string RecommendedAction);

public static class Cia402ErrorCodeCatalog
{
    private static readonly IReadOnlyDictionary<ushort, Cia402ErrorCodeDefinition> Entries =
        BuildEntries();

    public static Cia402ErrorCodeDefinition Resolve(ushort code) =>
        Entries.TryGetValue(code, out var definition)
            ? definition
            : new(
                null,
                "Código CiA 402 não cadastrado",
                $"O objeto EtherCAT 603Fh informou 0x{code:X4} ({code}), ainda sem descrição específica cadastrada.",
                "Registre o código hexadecimal, consulte o histórico interno do C2000 Plus e confirme a condição antes de realizar o reset.");

    private static IReadOnlyDictionary<ushort, Cia402ErrorCodeDefinition> BuildEntries()
    {
        const string currentAction =
            "Verifique curto-circuito, fuga à terra, cabos e isolamento do motor, travamento mecânico, carga e dimensionamento do inversor.";
        const string voltageAction =
            "Verifique tensão e equilíbrio da alimentação, fusíveis, contatores, bornes, barramento CC, rampas, regeneração e frenagem.";
        const string temperatureAction =
            "Verifique ventilação, filtros, ventiladores, temperatura ambiente, carga e sensores térmicos antes do reset.";
        const string communicationAction =
            "Verifique o mestre EtherCAT, estado da rede, cabos, conectores, sincronismo, watchdog e configuração dos objetos de comunicação.";
        const string serviceAction =
            "Desenergize conforme o procedimento de segurança. Se a falha permanecer após a inspeção, encaminhe o inversor para assistência técnica.";

        var entries = new Dictionary<ushort, Cia402ErrorCodeDefinition>();
        Add(entries, 0x0000, "Sem erro", "O objeto 603Fh não contém um erro ativo.", "Use também o estado do inversor e o contador de eventos para confirmar a ocorrência.");
        Add(entries, 0x1000, "Erro genérico", "O inversor informou uma falha genérica sem uma classe mais específica.", serviceAction);

        Add(entries, 0x2000, "Falha de corrente", "Foi detectada uma condição anormal de corrente.", currentAction);
        Add(entries, 0x2100, "Falha de corrente na entrada", "Foi detectada corrente anormal no lado de entrada.", currentAction);
        Add(entries, 0x2110, "Sobrecorrente na entrada", "A corrente de entrada ultrapassou o limite permitido.", currentAction);
        Add(entries, 0x2120, "Subcorrente na entrada", "A corrente de entrada ficou abaixo do limite esperado.", currentAction);
        Add(entries, 0x2130, "Falha de fase na entrada", "Foi detectada perda ou desequilíbrio de fase na entrada.", voltageAction);
        Add(entries, 0x2200, "Falha de corrente interna", "Foi detectada uma condição anormal de corrente dentro do inversor.", serviceAction);
        Add(entries, 0x2210, "Sobrecorrente interna", "A corrente interna ultrapassou o limite permitido.", serviceAction);
        Add(entries, 0x2220, "Subcorrente interna", "A corrente interna ficou abaixo do limite esperado.", serviceAction);
        Add(entries, 0x2230, "Falha de fase de corrente interna", "O circuito interno detectou uma condição de fase anormal.", serviceAction);
        Add(entries, 0x2300, "Falha de corrente na saída", "Foi detectada uma condição anormal na corrente de saída para o motor.", currentAction);
        Add(entries, 0x2310, "Sobrecorrente contínua na saída", "A corrente de saída permaneceu acima do limite permitido.", currentAction);
        Add(entries, 0x2320, "Curto-circuito ou fuga à terra", "Foi detectado curto-circuito ou corrente de fuga na saída.", currentAction);
        Add(entries, 0x2330, "Nível de carga excessivo", "A carga do acionamento ultrapassou o nível permitido.", currentAction);

        Add(entries, 0x3000, "Falha de tensão", "Foi detectada uma condição anormal de tensão.", voltageAction);
        Add(entries, 0x3100, "Falha de tensão da rede", "A tensão de alimentação ficou fora da condição esperada.", voltageAction);
        Add(entries, 0x3110, "Sobretensão da rede", "A tensão de alimentação ultrapassou o limite permitido.", voltageAction);
        Add(entries, 0x3120, "Subtensão da rede", "A tensão de alimentação ficou abaixo do limite permitido.", voltageAction);
        Add(entries, 0x3130, "Falta de fase na rede", "Foi detectada ausência ou forte desequilíbrio de fase na alimentação.", voltageAction);
        Add(entries, 0x3200, "Falha de tensão no barramento CC", "A tensão do circuito intermediário CC ficou fora da condição esperada.", voltageAction);
        Add(entries, 0x3210, "Sobretensão no barramento CC", "A tensão do circuito intermediário CC ultrapassou o limite permitido.", voltageAction);
        Add(
            entries,
            0x3220,
            "Subtensão no barramento CC",
            "O objeto 603Fh informou que a tensão do circuito intermediário CC ficou abaixo do limite permitido.",
            "Verifique alimentação, falta ou desequilíbrio de fase, contatores, fusíveis, bornes e quedas de tensão; confira o histórico interno do C2000 Plus para identificar a etapa da operação.");
        Add(entries, 0x3230, "Falha de carga no barramento CC", "Foi detectada uma condição anormal de carga no circuito intermediário CC.", voltageAction);
        Add(entries, 0x3300, "Falha de tensão na saída", "A tensão de saída para o motor ficou fora da condição esperada.", voltageAction);
        Add(entries, 0x3310, "Sobretensão na saída", "A tensão de saída ultrapassou o limite permitido.", voltageAction);
        Add(entries, 0x3320, "Subtensão na saída", "A tensão de saída ficou abaixo do limite permitido.", voltageAction);
        Add(entries, 0x3330, "Falta de fase na saída", "Foi detectada ausência ou condição anormal de fase na saída.", currentAction);

        Add(entries, 0x4000, "Falha de temperatura", "Foi detectada uma condição térmica anormal.", temperatureAction);
        Add(entries, 0x4100, "Temperatura ambiente", "A temperatura ambiente ficou fora da faixa permitida.", temperatureAction);
        Add(entries, 0x4110, "Temperatura ambiente excessiva", "A temperatura ambiente ultrapassou o limite permitido.", temperatureAction);
        Add(entries, 0x4120, "Temperatura ambiente baixa", "A temperatura ambiente ficou abaixo do limite permitido.", temperatureAction);
        Add(entries, 0x4200, "Temperatura do equipamento", "A temperatura interna do equipamento ficou fora da faixa permitida.", temperatureAction);
        Add(entries, 0x4210, "Equipamento superaquecido", "A temperatura interna do equipamento ultrapassou o limite permitido.", temperatureAction);
        Add(entries, 0x4220, "Temperatura interna baixa", "A temperatura interna do equipamento ficou abaixo do limite permitido.", temperatureAction);
        Add(entries, 0x4300, "Temperatura do acionamento", "A temperatura do acionamento ficou fora da faixa permitida.", temperatureAction);
        Add(entries, 0x4310, "Acionamento superaquecido", "A temperatura do acionamento ultrapassou o limite permitido.", temperatureAction);
        Add(entries, 0x4320, "Temperatura baixa no acionamento", "A temperatura do acionamento ficou abaixo do limite permitido.", temperatureAction);
        Add(entries, 0x4400, "Temperatura da alimentação", "A temperatura do estágio de alimentação ficou fora da faixa permitida.", temperatureAction);
        Add(entries, 0x4410, "Alimentação superaquecida", "A temperatura do estágio de alimentação ultrapassou o limite permitido.", temperatureAction);
        Add(entries, 0x4420, "Temperatura baixa na alimentação", "A temperatura do estágio de alimentação ficou abaixo do limite permitido.", temperatureAction);
        Add(entries, 0x4500, "Temperatura do drive", "A temperatura do drive ficou fora da faixa permitida.", temperatureAction);
        Add(entries, 0x4510, "Drive superaquecido", "A temperatura do drive ultrapassou o limite permitido.", temperatureAction);
        Add(entries, 0x4520, "Temperatura baixa no drive", "A temperatura do drive ficou abaixo do limite permitido.", temperatureAction);

        Add(entries, 0x5000, "Falha de hardware", "O inversor detectou uma falha de hardware.", serviceAction);
        Add(entries, 0x5100, "Falha na fonte interna", "A fonte interna do inversor ficou fora da condição esperada.", serviceAction);
        Add(entries, 0x5110, "Baixa tensão na fonte interna", "A tensão da fonte interna ficou abaixo do limite permitido.", serviceAction);
        Add(entries, 0x5120, "Alta tensão na fonte interna", "A tensão da fonte interna ultrapassou o limite permitido.", serviceAction);
        Add(entries, 0x5200, "Falha no controle", "O hardware de controle detectou uma condição anormal.", serviceAction);
        Add(entries, 0x5210, "Falha no circuito de medição", "O circuito de medição apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x5220, "Falha no circuito de processamento", "O circuito de processamento apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x5300, "Falha na unidade de operação", "A interface ou unidade de operação apresentou uma falha.", serviceAction);
        Add(entries, 0x5400, "Falha no estágio de potência", "O estágio de potência apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x5410, "Falha no estágio de saída", "O estágio de saída apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x5420, "Falha no chopper de frenagem", "O circuito chopper de frenagem apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x5430, "Falha no estágio de entrada", "O estágio de entrada apresentou uma condição anormal.", serviceAction);

        Add(entries, 0x6000, "Falha de software", "O inversor detectou uma falha de software.", serviceAction);
        Add(entries, 0x6100, "Falha de software interno", "O software interno detectou uma condição anormal.", serviceAction);
        Add(entries, 0x6200, "Falha de software de aplicação", "A aplicação ou parametrização detectou uma condição anormal.", "Verifique parâmetros e sequência de operação; se persistir, registre o evento e acione a assistência técnica.");
        Add(entries, 0x6300, "Erro no conjunto de parâmetros", "Foi detectada inconsistência em dados ou parâmetros armazenados.", "Valide o conjunto de parâmetros e restaure uma cópia homologada antes de operar.");

        Add(entries, 0x7000, "Falha em módulo adicional", "Um módulo adicional do acionamento apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x7100, "Falha no módulo de potência", "O módulo de potência apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x7110, "Falha na resistência ou chopper de frenagem", "O sistema de frenagem apresentou uma condição anormal.", voltageAction);
        Add(entries, 0x7120, "Falha do motor", "Foi detectada uma condição anormal relacionada ao motor.", currentAction);
        Add(entries, 0x7200, "Falha de medição", "Um sistema de medição apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x7300, "Falha de sensor", "Um sensor do acionamento apresentou sinal ausente ou inválido.", "Verifique alimentação, cabos, conectores, blindagem, aterramento e parametrização do sensor.");
        Add(entries, 0x7310, "Falha no sensor de velocidade", "O sinal de velocidade está ausente ou inválido.", "Verifique encoder, alimentação, cabos, conectores, blindagem e parametrização.");
        Add(entries, 0x7320, "Falha no sensor de posição", "O sinal de posição está ausente ou inválido.", "Verifique encoder, alimentação, cabos, conectores, blindagem e parametrização.");
        Add(entries, 0x7400, "Falha no circuito de cálculo", "O circuito responsável pelos cálculos de controle apresentou uma condição anormal.", serviceAction);
        Add(entries, 0x7500, "Falha de comunicação", "Uma interface de comunicação apresentou uma condição anormal.", communicationAction);
        Add(entries, 0x7510, "Falha na interface serial 1", "A primeira interface serial apresentou uma condição anormal.", communicationAction);
        Add(entries, 0x7520, "Falha na interface serial 2", "A segunda interface serial apresentou uma condição anormal.", communicationAction);
        Add(entries, 0x7600, "Falha no armazenamento de dados", "O armazenamento de dados ou parâmetros apresentou uma condição anormal.", serviceAction);

        Add(entries, 0x8000, "Falha de supervisão", "Uma função de supervisão detectou uma condição anormal.", "Verifique intertravamentos, limites configurados, sinais de realimentação e sequência de operação.");
        Add(entries, 0x8100, "Falha de supervisão da comunicação", "A supervisão da comunicação detectou perda ou inconsistência de dados.", communicationAction);
        Add(entries, 0x8110, "Estouro do barramento CAN", "O controlador CAN recebeu mais dados do que conseguiu processar.", communicationAction);
        Add(entries, 0x8120, "CAN em estado passivo", "O controlador CAN entrou em estado de erro passivo.", communicationAction);
        Add(entries, 0x8130, "Falha de heartbeat ou life guard", "A mensagem de supervisão esperada não foi recebida.", communicationAction);
        Add(entries, 0x8140, "Recuperação de bus-off", "A interface CAN registrou recuperação após estado bus-off.", communicationAction);
        Add(entries, 0x8150, "Colisão de COB-ID", "Foi detectada duplicidade de identificador na rede CANopen.", communicationAction);
        Add(entries, 0x8200, "Erro de protocolo", "A pilha de comunicação detectou uma violação de protocolo.", communicationAction);
        Add(entries, 0x8210, "Comprimento incorreto de PDO", "O PDO recebido possui comprimento diferente do configurado.", communicationAction);
        Add(entries, 0x8220, "Comprimento de PDO excedido", "O PDO excedeu o comprimento permitido.", communicationAction);

        Add(entries, 0x9000, "Erro externo", "Uma entrada ou equipamento externo informou uma condição de falha.", "Identifique o intertravamento ou dispositivo externo que originou a falha antes do reset.");
        Add(entries, 0xF000, "Função adicional", "Uma função adicional do perfil informou uma condição de erro.", "Registre o código e consulte a documentação da função correspondente.");
        Add(entries, 0xFF00, "Erro específico do fabricante", "O inversor informou uma falha específica do fabricante.", "Consulte o histórico interno e o manual do Delta C2000 Plus usando o código exibido no teclado.");
        return entries;
    }

    private static void Add(
        IDictionary<ushort, Cia402ErrorCodeDefinition> entries,
        ushort code,
        string title,
        string description,
        string action) =>
        entries.Add(code, new(null, title, description, action));
}
