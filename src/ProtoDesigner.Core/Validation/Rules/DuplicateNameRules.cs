using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

public sealed class DuplicateBusNameRule : IValidationRule
{
    public string Code => DiagnosticCodes.DuplicateBusName;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var group in ctx.Project.Buses.GroupBy(b => b.Name, StringComparer.Ordinal))
        {
            if (group.Count() <= 1) continue;
            foreach (var bus in group.Skip(1))
                yield return new Diagnostic(Code, Severity.Error,
                    $"Two buses named '{group.Key}'. Bus names must be unique within a project.",
                    EntityPath.ForBus(bus));
        }
    }
}

public sealed class DuplicateMessageNameRule : IValidationRule
{
    public string Code => DiagnosticCodes.DuplicateMessageName;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
        {
            foreach (var group in bus.Messages.GroupBy(m => m.Name, StringComparer.Ordinal))
            {
                if (group.Count() <= 1) continue;
                foreach (var message in group.Skip(1))
                    yield return new Diagnostic(Code, Severity.Error,
                        $"Two messages named '{group.Key}' on bus '{bus.Name}'.",
                        EntityPath.ForMessage(bus, message));
            }
        }
    }
}

public sealed class DuplicateTypeNameRule : IValidationRule
{
    public string Code => DiagnosticCodes.DuplicateTypeName;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var group in ctx.Project.Types.All.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            if (group.Count() <= 1) continue;
            foreach (var type in group.Skip(1))
                yield return new Diagnostic(Code, Severity.Warning,
                    $"Two types named '{group.Key}'. Names collide in generated code even though ids differ.",
                    EntityPath.ForType(type));
        }
    }
}

public sealed class DuplicateWireIdRule : IValidationRule
{
    public string Code => DiagnosticCodes.DuplicateWireId;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
        {
            var withIds = bus.Messages.Where(m => m.WireId.HasValue);
            foreach (var group in withIds.GroupBy(m => m.WireId!.Value))
            {
                if (group.Count() <= 1) continue;
                foreach (var message in group.Skip(1))
                    yield return new Diagnostic(Code, Severity.Error,
                        $"Wire id {group.Key} is used by more than one message on bus '{bus.Name}'.",
                        EntityPath.ForMessage(bus, message));
            }
        }
    }
}

/// <summary>
/// Catches duplicate names in messages and inside struct definitions. The engine also enforces this at
/// layout time; the rule surfaces it before the engine runs, with a friendlier message and location.
/// </summary>
public sealed class DuplicateFieldNameRule : IValidationRule
{
    public string Code => DiagnosticCodes.DuplicateFieldName;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        var findings = new List<Diagnostic>();

        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
                CheckFields(ctx, bus, message, message.Fields, parent: null, findings);

        foreach (var type in ctx.Project.Types.All.OfType<StructType>())
            CheckStructOnly(type, findings);

        return findings;
    }

    private void CheckFields(ValidationContext ctx, Bus bus, Message message, IReadOnlyList<FieldBinding> fields, string? parent, List<Diagnostic> sink)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var path = parent is null ? field.Name : $"{parent}.{field.Name}";
            if (!seen.Add(field.Name))
                sink.Add(new Diagnostic(Code, Severity.Error,
                    parent is null
                        ? $"Message '{message.Name}' has more than one field named '{field.Name}'."
                        : $"Struct at '{parent}' has more than one member named '{field.Name}'.",
                    EntityPath.ForField(bus, message, path)));

            if (ctx.Project.Types.TryGet(field.TypeId, out var type) && type is StructType inner)
                CheckFields(ctx, bus, message, inner.Fields, path, sink);
        }
    }

    private void CheckStructOnly(StructType type, List<Diagnostic> sink)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in type.Fields)
        {
            if (!seen.Add(field.Name))
                sink.Add(new Diagnostic(Code, Severity.Error,
                    $"Struct '{type.Name}' has more than one member named '{field.Name}'.",
                    EntityPath.ForTypeField(type, field.Name)));
        }
    }
}
