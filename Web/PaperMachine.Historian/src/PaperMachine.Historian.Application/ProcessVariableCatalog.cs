using System.Text.Json;

namespace PaperMachine.Historian.Application;

public static class ProcessVariableCatalog
{
    public static bool IsSupportedField(string fieldName, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return true;
        if (value.ValueKind != JsonValueKind.Number)
            return false;

        return IsSupportedFieldName(fieldName);
    }

    public static bool IsSupportedFieldName(string fieldName) =>
        fieldName.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Torque", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Pressure", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("MMH2O", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Position", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Temperature", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Level", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Vacuum", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Flow", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Setpoint", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("CtrlOutput", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Ratio", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Current", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Voltage", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Frequency", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Diameter", StringComparison.OrdinalIgnoreCase) ||
        fieldName.EndsWith("State", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("FaultCode", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("EventCounter", StringComparison.OrdinalIgnoreCase) ||
        TelemetryCatalog.BooleanFields.Contains(fieldName, StringComparer.Ordinal);

    public static ProcessVariableDefinition Describe(string fieldName)
    {
        if (fieldName.EndsWith("State", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("FaultCode", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("EventCounter", StringComparison.OrdinalIgnoreCase))
        {
            return new(fieldName, "Estado", "código");
        }
        if (fieldName.Contains("Torque", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Torque", "%");
        if (fieldName.Contains("Speed", StringComparison.OrdinalIgnoreCase))
        {
            var isPump = fieldName.Contains("Pump", StringComparison.OrdinalIgnoreCase);
            return new(fieldName, isPump ? "Bombas" : "Velocidade", isPump ? "%" : "m/min");
        }
        if (fieldName.Contains("MMH2O", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Headbox", "mmH₂O");
        if (fieldName.Contains("Pressure", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Pressão", "bar");
        if (fieldName.Contains("Position", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("Diameter", StringComparison.OrdinalIgnoreCase))
        {
            return new(fieldName, "Posição", "mm");
        }
        if (fieldName.Contains("Temperature", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Temperatura", "°C");
        if (fieldName.Contains("Flow", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Vazão", "unidade PLC");
        if (fieldName.Contains("Current", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Elétrica", "A");
        if (fieldName.Contains("Voltage", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Elétrica", "V");
        if (fieldName.Contains("Frequency", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Elétrica", "Hz");
        if (fieldName.Contains("Vacuum", StringComparison.OrdinalIgnoreCase))
            return new(fieldName, "Vácuo", "unidade PLC");
        if (fieldName.Contains("Level", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("CtrlOutput", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("Setpoint", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("Ratio", StringComparison.OrdinalIgnoreCase))
        {
            return new(fieldName, "Processo", "%");
        }
        if (TelemetryCatalog.BooleanFields.Contains(fieldName, StringComparer.Ordinal))
            return new(fieldName, "Estado", "0/1");
        return new(fieldName, "Processo", "unidade PLC");
    }
}

public sealed record ProcessVariableDefinition(
    string FieldName,
    string Category,
    string Unit);
