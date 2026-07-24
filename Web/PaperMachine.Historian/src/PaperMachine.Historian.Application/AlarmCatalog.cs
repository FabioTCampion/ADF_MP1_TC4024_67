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
        "Delta C2000 Plus User Manual — capítulo 14, Fault Codes and Descriptions";

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

        var definition = C2000PlusFaultCatalog.Resolve(code);
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

public sealed record C2000PlusFaultDefinition(
    string? Mnemonic,
    string Title,
    string Description,
    string RecommendedAction);

public static class C2000PlusFaultCatalog
{
    private static readonly IReadOnlyDictionary<ushort, C2000PlusFaultDefinition> Entries =
        BuildEntries();

    public static C2000PlusFaultDefinition Resolve(ushort code) =>
        Entries.TryGetValue(code, out var definition)
            ? definition
            : new(
                null,
                "Código C2000 Plus não cadastrado",
                $"O inversor informou o código {code} (0x{code:X4}), ainda sem descrição cadastrada nesta versão.",
                "Confirme o código no display do inversor e consulte o capítulo 14 do manual C2000 Plus antes de realizar o reset.");

    private static IReadOnlyDictionary<ushort, C2000PlusFaultDefinition> BuildEntries()
    {
        const string currentAction =
            "Verifique curto-circuito, isolamento e cabos do motor, travamento mecânico, carga e dimensionamento do inversor.";
        const string voltageAction =
            "Verifique a tensão de alimentação e do barramento CC, tempos de aceleração/desaceleração, carga regenerativa e o sistema de frenagem.";
        const string temperatureAction =
            "Verifique ventilação, filtros, ventiladores, temperatura ambiente, carga e sensores térmicos antes do reset.";
        const string feedbackAction =
            "Verifique encoder, alimentação, blindagem, aterramento, cabos e parâmetros do cartão de realimentação.";
        const string serviceAction =
            "Desenergize conforme o procedimento de segurança. Se a falha permanecer após a inspeção, encaminhe o inversor para assistência técnica.";

        var entries = new Dictionary<ushort, C2000PlusFaultDefinition>();
        Add(entries, 0, null, "Sem código de falha", "O objeto de diagnóstico não contém um código de falha ativo.", "Use o estado do alarme e a comunicação para confirmar a ocorrência.");
        Add(entries, 1, "ocA", "Sobrecorrente durante a aceleração", "A corrente de saída ultrapassou o limite durante a aceleração.", currentAction);
        Add(entries, 2, "ocd", "Sobrecorrente durante a desaceleração", "A corrente de saída ultrapassou o limite durante a desaceleração.", currentAction);
        Add(entries, 3, "ocn", "Sobrecorrente em velocidade constante", "A corrente de saída ultrapassou o limite durante operação estável.", currentAction);
        Add(entries, 4, "GFF", "Falha à terra", "Foi detectada corrente anormal entre a saída do inversor e o terra.", "Verifique isolamento do motor, cabos, caixa de ligação e aterramento antes de energizar.");
        Add(entries, 5, "occ", "Curto-circuito no módulo IGBT", "O inversor detectou curto entre braços do módulo de potência.", serviceAction);
        Add(entries, 6, "ocS", "Sobrecorrente com o drive parado", "Foi detectada sobrecorrente ou falha no circuito de medição com o inversor parado.", serviceAction);
        Add(entries, 7, "ovA", "Sobretensão durante a aceleração", "O barramento CC ultrapassou o limite durante a aceleração.", voltageAction);
        Add(entries, 8, "ovd", "Sobretensão durante a desaceleração", "O barramento CC ultrapassou o limite durante a desaceleração.", voltageAction);
        Add(entries, 9, "ovn", "Sobretensão em velocidade constante", "O barramento CC ultrapassou o limite durante operação estável.", voltageAction);
        Add(entries, 10, "ovS", "Sobretensão com o drive parado", "O barramento CC ultrapassou o limite com o inversor parado.", voltageAction);
        Add(entries, 11, "LvA", "Subtensão durante a aceleração", "A tensão do barramento CC ficou abaixo do limite durante a aceleração.", voltageAction);
        Add(entries, 12, "Lvd", "Subtensão durante a desaceleração", "A tensão do barramento CC ficou abaixo do limite durante a desaceleração.", voltageAction);
        Add(entries, 13, "Lvn", "Subtensão em velocidade constante", "A tensão do barramento CC ficou abaixo do limite durante operação estável.", voltageAction);
        Add(entries, 14, "LvS", "Subtensão com o drive parado", "A tensão do barramento CC ficou abaixo do limite com o inversor parado.", voltageAction);
        Add(entries, 15, "OrP", "Falta de fase", "O inversor detectou ausência ou forte desequilíbrio de fase.", "Verifique fusíveis, contatores, bornes, cabos e equilíbrio da alimentação trifásica.");
        Add(entries, 16, "oH1", "Superaquecimento do IGBT", "A temperatura do módulo de potência ultrapassou o limite.", temperatureAction);
        Add(entries, 17, "oH2", "Superaquecimento do dissipador", "A temperatura do dissipador ultrapassou o limite.", temperatureAction);
        Add(entries, 18, "tH1o", "Falha no sensor de temperatura do IGBT", "O circuito de medição da temperatura do IGBT apresentou falha.", serviceAction);
        Add(entries, 19, "tH2o", "Falha térmica do módulo de capacitores", "O circuito térmico ou o hardware do módulo de capacitores apresentou falha.", serviceAction);
        Add(entries, 21, "oL", "Sobrecarga do inversor", "A carga permaneceu acima da capacidade térmica configurada para o inversor.", "Verifique carga, aceleração, regime de trabalho, parâmetros do motor e dimensionamento.");
        Add(entries, 22, "EoL1", "Proteção térmica eletrônica do motor 1", "O modelo térmico eletrônico indicou sobrecarga do motor 1.", "Verifique carga, ventilação do motor, corrente nominal e parâmetros da proteção térmica.");
        Add(entries, 23, "EoL2", "Proteção térmica eletrônica do motor 2", "O modelo térmico eletrônico indicou sobrecarga do motor 2.", "Verifique carga, ventilação do motor, corrente nominal e parâmetros da proteção térmica.");
        Add(entries, 24, "oH3", "Superaquecimento do motor", "O sensor PTC ou PT100 indicou temperatura excessiva no motor.", "Aguarde o resfriamento e verifique ventilação, carga, sensor e cabeamento.");
        Add(entries, 26, "ot1", "Sobretorque 1", "O torque ultrapassou o nível de detecção configurado para a função 1.", "Verifique travamento, carga mecânica e parâmetros de detecção de sobretorque.");
        Add(entries, 27, "ot2", "Sobretorque 2", "O torque ultrapassou o nível de detecção configurado para a função 2.", "Verifique travamento, carga mecânica e parâmetros de detecção de sobretorque.");
        Add(entries, 28, "uC", "Corrente abaixo do limite", "A corrente de saída permaneceu abaixo do nível configurado.", "Verifique desacoplamento mecânico, correia, carga, motor e parâmetros de subcorrente.");
        Add(entries, 29, "LiT", "Erro de limite", "O inversor detectou uma condição inválida de limite operacional.", "Verifique limites configurados, referências e condições mecânicas do acionamento.");
        Add(entries, 30, "cF1", "Erro de gravação da EEPROM", "A memória interna não pôde ser programada.", serviceAction);
        Add(entries, 31, "cF2", "Erro de leitura da EEPROM", "A memória interna não pôde ser lida.", serviceAction);
        Add(entries, 33, "cd1", "Erro de medição da fase U", "O circuito de detecção de corrente da fase U apresentou falha.", serviceAction);
        Add(entries, 34, "cd2", "Erro de medição da fase V", "O circuito de detecção de corrente da fase V apresentou falha.", serviceAction);
        Add(entries, 35, "cd3", "Erro de medição da fase W", "O circuito de detecção de corrente da fase W apresentou falha.", serviceAction);
        Add(entries, 36, "Hd0", "Erro de hardware do limitador de corrente", "O circuito de limitação de corrente apresentou falha.", serviceAction);
        Add(entries, 37, "Hd1", "Erro de hardware de sobrecorrente", "O circuito de detecção de sobrecorrente apresentou falha.", serviceAction);
        Add(entries, 38, "Hd2", "Erro de hardware de sobretensão", "O circuito de detecção de sobretensão apresentou falha.", serviceAction);
        Add(entries, 39, "Hd3", "Erro de hardware do IGBT", "O circuito de detecção do módulo de potência apresentou falha.", serviceAction);
        Add(entries, 40, "AUE", "Erro de autoajuste", "O procedimento de identificação automática do motor não foi concluído.", "Verifique cabos, dados de placa, capacidade do motor e condições para o autoajuste.");
        Add(entries, 41, "AFE", "Perda do sinal PID", "O sinal de realimentação analógica do PID foi perdido.", "Verifique transmissor, alimentação, escala, cabos e entrada analógica configurada.");
        Add(entries, 42, "PGF1", "Erro no feedback do encoder", "O inversor detectou sinal inválido no feedback do encoder.", feedbackAction);
        Add(entries, 43, "PGF2", "Perda do feedback do encoder", "O sinal do encoder deixou de ser detectado.", feedbackAction);
        Add(entries, 44, "PGF3", "Travamento detectado pelo encoder", "O feedback não acompanhou o movimento esperado.", feedbackAction);
        Add(entries, 45, "PGF4", "Erro de escorregamento do encoder", "A diferença entre referência e feedback ultrapassou o limite.", feedbackAction);
        Add(entries, 48, "ACE", "Perda da entrada ACI", "O sinal de corrente da entrada analógica ACI foi perdido.", "Verifique transmissor, alimentação, cabos, bornes e escala da entrada ACI.");
        Add(entries, 49, "EF", "Falha externa", "Uma entrada configurada como falha externa foi acionada.", "Identifique o dispositivo ligado à entrada de falha externa e elimine a causa antes do reset.");
        Add(entries, 50, "EF1", "Parada de emergência", "Uma entrada configurada como parada de emergência foi acionada.", "Confirme a segurança da área e libere o circuito somente após identificar a causa.");
        Add(entries, 51, "bb", "Base block externo", "A saída do inversor foi bloqueada por um comando externo.", "Verifique a entrada de base block e o intertravamento responsável pelo bloqueio.");
        Add(entries, 52, "Pcod", "Senha bloqueada", "O teclado foi bloqueado após tentativas de senha incorretas.", "Siga o procedimento autorizado para desbloqueio e configuração do inversor.");
        Add(entries, 86, "UvoF", "Perda das fases UVW do encoder", "O cartão de feedback não detectou corretamente as fases UVW.", feedbackAction);
        Add(entries, 87, "oL3", "Sobrecarga em baixa frequência", "O acionamento permaneceu sobrecarregado em baixa velocidade.", "Verifique ventilação do motor, carga, torque requerido e dimensionamento.");
        Add(entries, 89, "RoPd", "Erro de detecção da posição do rotor", "A posição inicial do rotor não pôde ser determinada.", feedbackAction);
        Add(entries, 90, "FStp", "Parada forçada", "O inversor recebeu ou gerou uma condição de parada forçada.", "Verifique entradas, intertravamentos e a origem do comando de parada.");
        Add(entries, 92, "LEr", "Erro de ajuste Ld/Lq", "A identificação das indutâncias do motor não foi concluída.", "Verifique parâmetros, ligação e condições do motor antes de repetir o ajuste.");
        Add(entries, 93, "TRAP", "Erro interno da CPU", "O firmware detectou uma condição interna inesperada.", serviceAction);
        Add(entries, 101, "CGdE", "Erro de guarda CANopen", "A supervisão de comunicação CANopen expirou.", "Verifique rede, mestre, tempos de supervisão, cabos e terminação.");
        Add(entries, 102, "CHbE", "Erro de heartbeat CANopen", "O heartbeat CANopen esperado não foi recebido.", "Verifique rede, mestre, tempos de heartbeat, cabos e terminação.");
        Add(entries, 104, "CbFE", "CANopen em bus-off", "O controlador CANopen entrou em estado bus-off.", "Verifique curto, polaridade, blindagem, aterramento, terminação e taxa da rede.");
        Add(entries, 105, "CidE", "Erro de índice CANopen", "Foi solicitado um objeto ou índice CANopen inválido.", "Confirme o dicionário de objetos e a configuração do mestre.");
        Add(entries, 106, "CAdE", "Erro de endereço CANopen", "O endereço de estação CANopen é inválido ou está duplicado.", "Verifique o endereço configurado e possíveis duplicidades na rede.");
        Add(entries, 107, "CFrE", "Erro de memória CANopen", "O módulo de comunicação detectou falha interna de memória.", serviceAction);
        Add(entries, 111, "ictE", "Tempo esgotado na comunicação interna", "A comunicação interna entre módulos do inversor expirou.", serviceAction);
        Add(entries, 112, "SfLK", "Rotor bloqueado em modo sensorless", "O controle sensorless detectou que o eixo não acompanhou o comando.", "Verifique travamento, carga, parâmetros do motor e capacidade de torque.");
        Add(entries, 142, "AUE1", "Autoajuste: ausência de corrente", "Não foi detectada corrente durante a identificação do motor.", "Verifique contatores, cabos, ligação e dados de placa do motor.");
        Add(entries, 143, "AUE2", "Autoajuste: falta de fase do motor", "O inversor detectou fase ausente durante o autoajuste.", "Verifique cabos, bornes e continuidade das três fases do motor.");
        Add(entries, 144, "AUE3", "Autoajuste: erro de corrente sem carga", "A corrente sem carga não pôde ser medida corretamente.", "Verifique corrente nominal configurada, condição mecânica e funcionamento do motor.");
        Add(entries, 148, "AUE4", "Autoajuste: erro de indutância", "A indutância de dispersão do motor não pôde ser medida.", "Verifique frequência base, dados do motor, ligação e condições para o autoajuste.");
        Add(entries, 171, "oPEE", "Erro de posição excedida", "A posição ultrapassou o limite permitido pelo controle.", "Verifique referência, feedback, limites de posição e integridade mecânica.");
        return entries;
    }

    private static void Add(
        IDictionary<ushort, C2000PlusFaultDefinition> entries,
        ushort code,
        string? mnemonic,
        string title,
        string description,
        string action) =>
        entries.Add(code, new(mnemonic, title, description, action));
}
