using ProtoDesigner.Core.Model;
using ProtoDesigner.Core.Validation;
using ProtoDesigner.Core.Validation.Rules;

namespace ProtoDesigner.Application;

/// <summary>Whether one message can be exported as <c>.proto</c>, and what stops it if not.</summary>
/// <param name="Message">The message asked about.</param>
/// <param name="Blockers">Errors preventing export. Empty when <see cref="IsEligible"/> is true.</param>
/// <param name="Notes">
/// Warnings that do not prevent export but change the result — a dropped endianness, say. Worth showing
/// next to the message rather than discovering in the generated file.
/// </param>
public sealed record ProtoEligibility(
    Message Message,
    IReadOnlyList<Diagnostic> Blockers,
    IReadOnlyList<Diagnostic> Notes)
{
    public bool IsEligible => Blockers.Count == 0;

    /// <summary>A one-line reason for the UI, or null when the message is fine.</summary>
    public string? Reason => Blockers.Count == 0 ? null : Blockers[0].Message;
}

/// <summary>
/// Answers "which of these messages could be exported as protobuf?".
/// </summary>
/// <remarks>
/// <para>
/// The gate exists in exactly one place, here, and both the editor's message list and the generator's
/// scope selection read it. Duplicating the check between UI and codegen is how a tool ends up offering
/// something it then refuses, or worse, generating something it should have refused.
/// </para>
/// <para>
/// It runs <see cref="ProtobufRules"/> only — never the shipped catalogue — because a message being
/// unexportable says nothing about whether the protocol is correct. A bit-packed message is exactly what
/// this tool is for, and it must keep generating C without complaint.
/// </para>
/// </remarks>
public static class ProtobufCompatibility
{
    /// <summary>Eligibility for every message on a bus, in the bus's own order.</summary>
    public static IReadOnlyList<ProtoEligibility> ForBus(Project project, Bus bus)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bus);

        // One validator run covers the whole project; findings are then bucketed per message. Running it
        // per message would recompute every layout once per message.
        var findings = new Validator(ProtobufRules.All).Validate(project);

        return bus.Messages.Select(message =>
        {
            var mine = findings.Where(d => Mentions(project, d, bus, message)).ToList();
            return new ProtoEligibility(
                message,
                mine.Where(d => d.Severity == Severity.Error).ToList(),
                mine.Where(d => d.Severity != Severity.Error).ToList());
        }).ToList();
    }

    /// <summary>The messages on a bus that can be exported.</summary>
    public static IReadOnlyList<Message> EligibleMessages(Project project, Bus bus) =>
        ForBus(project, bus).Where(e => e.IsEligible).Select(e => e.Message).ToList();

    /// <summary>
    /// Every scope narrowed to the messages that can actually be exported, dropping any bus left with
    /// nothing. Feeds straight into <see cref="CodeGenerationService"/>.
    /// </summary>
    public static IReadOnlyList<GenerationScope> Narrow(
        Project project, IReadOnlyList<GenerationScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(scopes);

        var narrowed = new List<GenerationScope>();

        foreach (var scope in scopes)
        {
            var eligible = ForBus(project, scope.Bus)
                .Where(e => e.IsEligible)
                .Select(e => e.Message.Id)
                .ToHashSet();

            var kept = scope.Messages.Where(m => eligible.Contains(m.Id)).ToList();
            if (kept.Count > 0) narrowed.Add(new GenerationScope(scope.Bus, kept));
        }

        return narrowed;
    }

    /// <summary>
    /// Whether a finding belongs to this message.
    /// </summary>
    /// <remarks>
    /// Matched on the diagnostic's target rather than by re-running the rules per message, which would
    /// recompute every layout once per message. Struct-level findings — a duplicate field number inside a
    /// shared struct — carry a type path rather than a message path, so they are attributed to every
    /// message that reaches that struct: a shared struct that cannot be exported blocks each of its
    /// users, which is the truthful answer rather than a convenient one.
    /// </remarks>
    private static bool Mentions(Project project, Diagnostic diagnostic, Bus bus, Message message)
    {
        var target = diagnostic.Target;

        if (target.BusName is { } busName && target.MessageName is { } messageName)
            return busName == bus.Name && messageName == message.Name;

        if (target.TypeName is { } typeName)
            return Reaches(project, message, typeName);

        return false;
    }

    /// <summary>Whether a message reaches a named type through its fields, transitively.</summary>
    private static bool Reaches(Project project, Message message, string typeName)
    {
        var seen = new HashSet<TypeId>();
        return message.Fields.Any(f => Walk(f.TypeId));

        bool Walk(TypeId id)
        {
            if (!seen.Add(id)) return false;
            if (!project.Types.TryGet(id, out var type) || type is null) return false;
            if (string.Equals(type.Name, typeName, StringComparison.Ordinal)) return true;

            return type switch
            {
                StructType s => s.Fields.Any(f => Walk(f.TypeId)),
                ArrayType a => Walk(a.ElementTypeId),
                _ => false,
            };
        }
    }
}
