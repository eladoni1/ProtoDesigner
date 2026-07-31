using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

/// <summary>
/// Rejects recursive type graphs. The engine already refuses to lay them out; this rule reports the cycle
/// with a stable code before the engine ever runs so the UI can surface it inline.
/// </summary>
public sealed class RecursiveTypeRule : IValidationRule
{
    public string Code => DiagnosticCodes.RecursiveType;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        var stack = new Stack<TypeId>();
        var onStack = new HashSet<TypeId>();
        var done = new HashSet<TypeId>();
        var findings = new List<Diagnostic>();

        foreach (var type in ctx.Project.Types.All)
            Walk(ctx.Project, type.Id, stack, onStack, done, findings);

        return findings.DistinctBy(d => d.Target.TypeName);
    }

    private static void Walk(Project project, TypeId id, Stack<TypeId> stack, HashSet<TypeId> onStack, HashSet<TypeId> done, List<Diagnostic> sink)
    {
        if (done.Contains(id)) return;

        if (!onStack.Add(id))
        {
            if (project.Types.TryGet(id, out var offender) && offender is not null)
                sink.Add(new Diagnostic(DiagnosticCodes.RecursiveType, Severity.Error,
                    $"Type '{offender.Name}' contains itself, directly or through another type. A fixed layout cannot be recursive.",
                    EntityPath.ForType(offender)));
            return;
        }

        stack.Push(id);

        if (project.Types.TryGet(id, out var type) && type is not null)
        {
            switch (type)
            {
                case StructType s:
                    foreach (var f in s.Fields) Walk(project, f.TypeId, stack, onStack, done, sink);
                    break;
                case ArrayType a:
                    Walk(project, a.ElementTypeId, stack, onStack, done, sink);
                    break;
            }
        }

        stack.Pop();
        onStack.Remove(id);
        done.Add(id);
    }
}

/// <summary>Field references a TypeId that isn't in the library.</summary>
public sealed class UnknownTypeReferenceRule : IValidationRule
{
    public string Code => DiagnosticCodes.UnknownTypeReference;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
                foreach (var field in message.Fields)
                    if (!ctx.Project.Types.Contains(field.TypeId))
                        yield return new Diagnostic(Code, Severity.Error,
                            $"Field '{field.Name}' references type {field.TypeId} which is not in the type library.",
                            EntityPath.ForField(bus, message, field.Name));

        foreach (var type in ctx.Project.Types.All)
        {
            switch (type)
            {
                case StructType s:
                    foreach (var f in s.Fields)
                        if (!ctx.Project.Types.Contains(f.TypeId))
                            yield return new Diagnostic(Code, Severity.Error,
                                $"Struct '{type.Name}' member '{f.Name}' references type {f.TypeId} which is not in the type library.",
                                EntityPath.ForTypeField(type, f.Name));
                    break;
                case ArrayType a:
                    if (!ctx.Project.Types.Contains(a.ElementTypeId))
                        yield return new Diagnostic(Code, Severity.Error,
                            $"Array '{type.Name}' element type {a.ElementTypeId} is not in the type library.",
                            EntityPath.ForType(type));
                    break;
            }
        }
    }
}

/// <summary>An enum without members has no representable values and cannot have its width computed.</summary>
public sealed class EnumWithoutMembersRule : IValidationRule
{
    public string Code => DiagnosticCodes.EnumWithoutMembers;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var e in ctx.Project.Types.All.OfType<EnumType>())
            if (e.Members.Count == 0)
                yield return new Diagnostic(Code, Severity.Error,
                    $"Enum '{e.Name}' declares no members. Add at least one member or delete the enum.",
                    EntityPath.ForType(e));
    }
}

/// <summary>
/// Two members of the same enum share the same integer value — an alias, as in C's
/// <c>enum { SUCCESS = 0, NO_ERROR = 0 }</c>.
/// </summary>
/// <remarks>
/// Reported at <see cref="Severity.Info"/>, not as a warning: aliases are a deliberate and common idiom,
/// so flagging them as suspect would train the user to ignore the diagnostics pane. It is still worth
/// stating, because decoding an aliased value can only ever yield one of the names.
/// </remarks>
public sealed class DuplicateEnumMemberRule : IValidationRule
{
    public string Code => DiagnosticCodes.DuplicateEnumMember;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var e in ctx.Project.Types.All.OfType<EnumType>())
        {
            foreach (var group in e.Members.GroupBy(m => m.Value))
            {
                if (group.Count() <= 1) continue;
                var names = string.Join(", ", group.Select(m => $"'{m.Name}'"));
                yield return new Diagnostic(Code, Severity.Info,
                    $"Enum '{e.Name}' members {names} are aliases for the value {group.Key}. "
                    + $"Decoding {group.Key} yields '{group.First().Name}'.",
                    EntityPath.ForType(e));
            }
        }
    }
}
