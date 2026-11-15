using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSToolKit.Models;

/// <summary>Root of commands.json.</summary>
public sealed class CommandCatalog
{
    public List<CommandDefinition> Commands { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CommandCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        var catalog = JsonSerializer.Deserialize<CommandCatalog>(json, JsonOptions)
                      ?? throw new InvalidDataException("commands.json is empty.");

        foreach (var cmd in catalog.Commands)
        {
            if (string.IsNullOrWhiteSpace(cmd.Name))
                throw new InvalidDataException("A command in commands.json has no name.");
            if (cmd.Script.Length == 0)
                throw new InvalidDataException($"Command '{cmd.Name}' has no script.");
            foreach (var p in cmd.Parameters)
            {
                if (!IsValidVariableName(p.Name))
                    throw new InvalidDataException(
                        $"Command '{cmd.Name}': parameter name '{p.Name}' must be letters, digits or underscores.");
            }
        }
        return catalog;
    }

    private static bool IsValidVariableName(string name) =>
        !string.IsNullOrEmpty(name) &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

public sealed class CommandDefinition
{
    /// <summary>Tree group, e.g. "System Info".</summary>
    public string Category { get; set; } = "General";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Show a confirmation prompt before running (anything that changes state).</summary>
    public bool Confirm { get; set; }

    /// <summary>Shown as a hint, e.g. "Requires the ActiveDirectory module (RSAT)".</summary>
    public string? Requires { get; set; }

    public List<CommandParameter> Parameters { get; set; } = new();

    /// <summary>PowerShell script, one line per array entry. Parameters arrive as variables ($Name).</summary>
    public string[] Script { get; set; } = Array.Empty<string>();

    [JsonIgnore]
    public string ScriptText => string.Join(Environment.NewLine, Script);
}

public enum ParameterKind
{
    Text,
    Number,
    Choice,
    Switch
}

public sealed class CommandParameter
{
    /// <summary>PowerShell variable name (no $).</summary>
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public ParameterKind Kind { get; set; } = ParameterKind.Text;
    public string? Default { get; set; }
    public bool Required { get; set; }
    public string? Hint { get; set; }

    /// <summary>Options for Kind = Choice.</summary>
    public List<string> Options { get; set; } = new();

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label!;
}
