using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation;

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Human-readable and stable machine-readable pointer to the entity a diagnostic targets. Deep-linkable so the
/// UI can jump to the offending field, and stable so a test can assert against it.
/// </summary>
public sealed record EntityPath(
    string? BusName = null,
    string? MessageName = null,
    string? TypeName = null,
    string? FieldPath = null)
{
    public static EntityPath Project { get; } = new();

    public static EntityPath ForBus(Bus bus) => new(BusName: bus.Name);

    public static EntityPath ForMessage(Bus bus, Message message) =>
        new(BusName: bus.Name, MessageName: message.Name);

    public static EntityPath ForField(Bus bus, Message message, string fieldPath) =>
        new(BusName: bus.Name, MessageName: message.Name, FieldPath: fieldPath);

    public static EntityPath ForType(TypeDefinition type) => new(TypeName: type.Name);

    public static EntityPath ForTypeField(TypeDefinition type, string fieldPath) =>
        new(TypeName: type.Name, FieldPath: fieldPath);

    public override string ToString()
    {
        var parts = new List<string>();
        if (BusName is not null) parts.Add($"bus '{BusName}'");
        if (MessageName is not null) parts.Add($"message '{MessageName}'");
        if (TypeName is not null) parts.Add($"type '{TypeName}'");
        if (FieldPath is not null) parts.Add($"field '{FieldPath}'");
        return parts.Count == 0 ? "project" : string.Join(" / ", parts);
    }
}

/// <summary>Optional programmatic fix. The UI decides whether to present it.</summary>
public sealed record QuickFix(string Description, Action<Project> Apply);

/// <summary>
/// A validation finding. <see cref="Code"/> is stable across releases; once shipped it never changes meaning
/// and never gets renumbered — that's how tests assert, suppressions target, and users search for help.
/// </summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    EntityPath Target,
    QuickFix? Fix = null)
{
    public override string ToString() => $"{Code} [{Severity}] {Target}: {Message}";
}
