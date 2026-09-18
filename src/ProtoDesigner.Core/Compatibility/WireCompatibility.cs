using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Core.Compatibility;

/// <summary>
/// Answers the question git cannot: would this change break a decoder that is already deployed?
/// </summary>
/// <remarks>
/// <para>
/// A protocol definition is a compile-time contract between separately-built binaries. Narrowing a field
/// from 16 bits to 12 does not break the editor — it breaks every flashed board, at runtime, silently,
/// because the bytes still arrive and still parse into the wrong numbers. A textual diff cannot tell that
/// apart from a rename, and a rename is free.
/// </para>
/// <para>
/// Everything here rests on the two rules that make the comparison cheap. Identity is an id, so a rename
/// is invisible to the matching and reports as safe rather than as a removal plus an addition. Layout is
/// computed, so the comparison is between two runs of <see cref="LayoutEngine"/> rather than between two
/// sets of stored offsets that could disagree with the model that produced them.
/// </para>
/// <para>
/// This needs no database and no server: the baseline is any other <see cref="Project"/>, which is as
/// easily the last git commit as the last published version.
/// </para>
/// </remarks>
public static class WireCompatibility
{
    /// <summary>
    /// Compares <paramref name="current"/> against <paramref name="baseline"/> and reports what a peer
    /// built from the baseline would make of the result.
    /// </summary>
    /// <remarks>
    /// Messages a peer never had are safe, so an added message is <see cref="Severity.Info"/>. Everything
    /// that changes bytes a peer already reads is <see cref="Severity.Warning"/> — loud, but never a
    /// refusal, because breaking the wire on purpose is what a version bump is.
    /// </remarks>
    public static IReadOnlyList<Diagnostic> Compare(Project baseline, Project current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var report = new List<Diagnostic>();
        var engine = new LayoutEngine();

        foreach (var oldBus in baseline.Buses)
        {
            var newBus = current.Buses.FirstOrDefault(b => b.Id == oldBus.Id);
            if (newBus is null)
            {
                foreach (var message in oldBus.Messages) report.Add(MessageGone(oldBus, message));
                continue;
            }

            CompareBus(engine, baseline, current, oldBus, newBus, report);
        }

        foreach (var newBus in current.Buses.Where(b => baseline.Buses.All(o => o.Id != b.Id)))
            foreach (var message in newBus.Messages)
                report.Add(MessageIsNew(newBus, message));

        return report;
    }

    private static void CompareBus(LayoutEngine engine, Project baseline, Project current,
        Bus oldBus, Bus newBus, List<Diagnostic> report)
    {
        if (!string.Equals(oldBus.Name, newBus.Name, StringComparison.Ordinal))
            report.Add(new Diagnostic(
                DiagnosticCodes.RenamedSafely, Severity.Info,
                $"Bus '{oldBus.Name}' is now called '{newBus.Name}'. Names are display and code generation "
                + "only, so nothing on the wire changed.",
                EntityPath.ForBus(newBus)));

        foreach (var oldMessage in oldBus.Messages)
        {
            var newMessage = newBus.Messages.FirstOrDefault(m => m.Id == oldMessage.Id);
            if (newMessage is null)
            {
                report.Add(MessageGone(oldBus, oldMessage));
                continue;
            }

            CompareMessage(engine, baseline, current, oldBus, newBus, oldMessage, newMessage, report);
        }

        foreach (var newMessage in newBus.Messages.Where(m => oldBus.Messages.All(o => o.Id != m.Id)))
            report.Add(MessageIsNew(newBus, newMessage));
    }

    private static void CompareMessage(LayoutEngine engine, Project baseline, Project current,
        Bus oldBus, Bus newBus, Message oldMessage, Message newMessage, List<Diagnostic> report)
    {
        var target = EntityPath.ForMessage(newBus, newMessage);

        if (!string.Equals(oldMessage.Name, newMessage.Name, StringComparison.Ordinal))
            report.Add(new Diagnostic(
                DiagnosticCodes.RenamedSafely, Severity.Info,
                $"Message '{oldMessage.Name}' is now called '{newMessage.Name}'. A receiver identifies it "
                + "by its wire id, which has not changed, so nothing on the wire changed.",
                target));

        if (oldMessage.WireId != newMessage.WireId)
            report.Add(new Diagnostic(
                DiagnosticCodes.WireIdChanged, Severity.Warning,
                $"Wire id changed from {Describe(oldMessage.WireId)} to {Describe(newMessage.WireId)}. "
                + "A deployed receiver matches on this number, so it will stop recognising the message "
                + "entirely rather than mis-decode it.",
                target));

        // A message that cannot be laid out has nothing to compare. That is the validator's finding to
        // report, not this one's, so it is passed over rather than turned into a compatibility claim.
        if (!TryLayout(engine, baseline, oldBus, oldMessage, out var oldLayout)) return;
        if (!TryLayout(engine, current, newBus, newMessage, out var newLayout)) return;

        CompareFields(oldLayout!, newLayout!, newBus, newMessage, report);
    }

    private static void CompareFields(MessageLayout oldLayout, MessageLayout newLayout,
        Bus bus, Message message, List<Diagnostic> report)
    {
        var before = ValuesByIdentity(oldLayout);
        var after = ValuesByIdentity(newLayout);

        foreach (var (key, oldNode) in before)
        {
            if (!after.TryGetValue(key, out var newNode))
            {
                report.Add(new Diagnostic(
                    DiagnosticCodes.FieldRemoved, Severity.Warning,
                    $"Field '{oldNode.Path}' is gone. A deployed peer still writes it, and every field "
                    + "after it now decodes from the wrong offset.",
                    EntityPath.ForField(bus, message, oldNode.Path)));
                continue;
            }

            CompareNode(oldNode, newNode, bus, message, report);
        }

        foreach (var (key, newNode) in after)
        {
            if (before.ContainsKey(key)) continue;

            report.Add(new Diagnostic(
                DiagnosticCodes.FieldAdded, Severity.Warning,
                $"Field '{newNode.Path}' is new. This protocol has no tags or defaults, so a deployed peer "
                + "cannot skip a field it does not know — the message is a different length and every "
                + "offset after this point has moved.",
                EntityPath.ForField(bus, message, newNode.Path)));
        }
    }

    private static void CompareNode(LayoutNode before, LayoutNode after,
        Bus bus, Message message, List<Diagnostic> report)
    {
        var target = EntityPath.ForField(bus, message, after.Path);

        if (!string.Equals(before.Path, after.Path, StringComparison.Ordinal))
            report.Add(new Diagnostic(
                DiagnosticCodes.RenamedSafely, Severity.Info,
                $"Field '{before.Path}' is now called '{after.Path}'. References are by identity, so it is "
                + "the same field in the same place.",
                target));

        if (before.RegionIndex != after.RegionIndex || before.BitOffset != after.BitOffset)
            report.Add(new Diagnostic(
                DiagnosticCodes.FieldMoved, Severity.Warning,
                $"Field '{after.Path}' moved from bit {Position(before)} to bit {Position(after)}. "
                + "A deployed peer reads the old position and will decode whatever now sits there.",
                target));

        if (before.BitWidth != after.BitWidth || before.ElementBits != after.ElementBits)
            report.Add(new Diagnostic(
                DiagnosticCodes.FieldResized, Severity.Warning,
                $"Field '{after.Path}' changed width from {before.BitWidth} to {after.BitWidth} bit(s). "
                + (after.BitWidth < before.BitWidth
                    ? "Values above the narrower range now truncate, and they do so silently — a decoder "
                      + "reports no error, it just returns the wrong number."
                    : "Everything after it has shifted by the difference."),
                target));

        if (before.ElementCount != after.ElementCount)
            report.Add(new Diagnostic(
                DiagnosticCodes.FieldResized, Severity.Warning,
                $"Array '{after.Path}' changed from {Describe(before.ElementCount)} to "
                + $"{Describe(after.ElementCount)} element(s), so the message length changed with it.",
                target));

        if (before.Endianness != after.Endianness)
            report.Add(new Diagnostic(
                DiagnosticCodes.FieldEncodingChanged, Severity.Warning,
                $"Field '{after.Path}' changed byte order from {before.Endianness} to {after.Endianness}. "
                + "The bytes still arrive and still parse; the number they mean is reversed, which is the "
                + "hardest kind of break to spot in a capture.",
                target));

        if (before.BitOrder != after.BitOrder)
            report.Add(new Diagnostic(
                DiagnosticCodes.FieldEncodingChanged, Severity.Warning,
                $"Field '{after.Path}' changed bit order from {before.BitOrder} to {after.BitOrder}.",
                target));

        if (before.Transform != after.Transform)
            report.Add(new Diagnostic(
                DiagnosticCodes.FieldEncodingChanged, Severity.Warning,
                $"Field '{after.Path}' changed its mapping from {Describe(before.Transform)} to "
                + $"{Describe(after.Transform)}. The same bytes now stand for a different value.",
                target));
    }

    /// <summary>
    /// The value nodes of a layout, keyed by the chain of field ids that reaches them.
    /// </summary>
    /// <remarks>
    /// The key is a chain rather than one id because a struct used twice in the same message contributes
    /// its members twice, with the same inner binding id each time — <c>from.version</c> and
    /// <c>to.version</c> are one binding reached by two paths. Keying on the path instead would be simpler
    /// and wrong: the path carries names, so every rename would report as a removal plus an addition, and
    /// the one change this tool can promise is free would be the loudest thing in the report.
    /// </remarks>
    private static Dictionary<string, LayoutNode> ValuesByIdentity(MessageLayout layout)
    {
        var map = new Dictionary<string, LayoutNode>(StringComparer.Ordinal);
        foreach (var node in layout.Nodes) Walk(node, "");
        return map;

        void Walk(LayoutNode node, string prefix)
        {
            // Padding and length prefixes are derived framing that no user declared, so they carry no
            // identity to match on. They are covered anyway: whatever made them move or resize is a
            // change to a real field, and that is what gets reported.
            if (node.Kind is LayoutNodeKind.Padding or LayoutNodeKind.LengthPrefix) return;

            var key = node.FieldId is { } id ? $"{prefix}/{id.Value}" : $"{prefix}/{node.Path}";

            // Containers are not reported in their own right — a struct's offset is its first member's,
            // so reporting both would say the same thing twice.
            if (node.Kind is not LayoutNodeKind.Struct) map[key] = node;

            foreach (var child in node.Children) Walk(child, key);
        }
    }

    private static bool TryLayout(LayoutEngine engine, Project project, Bus bus, Message message,
        out MessageLayout? layout)
    {
        try
        {
            layout = engine.Compute(project, bus, message);
            return true;
        }
        catch (LayoutException)
        {
            layout = null;
            return false;
        }
    }

    private static Diagnostic MessageGone(Bus bus, Message message) => new(
        DiagnosticCodes.MessageRemoved, Severity.Warning,
        $"Message '{message.Name}' is gone. A deployed peer may still send it, and nothing will decode it.",
        EntityPath.ForMessage(bus, message));

    private static Diagnostic MessageIsNew(Bus bus, Message message) => new(
        DiagnosticCodes.MessageAdded, Severity.Info,
        $"Message '{message.Name}' is new. Nobody decodes a wire id they have never seen, so adding it "
        + "cannot break an existing peer.",
        EntityPath.ForMessage(bus, message));

    private static string Position(LayoutNode node) =>
        node.RegionIndex == 0 ? node.BitOffset.ToString() : $"{node.BitOffset} of region {node.RegionIndex}";

    private static string Describe(int? value) => value?.ToString() ?? "unset";

    private static string Describe(ScalarTransform transform) => transform.IsIdentity
        ? "the value itself"
        : $"(value - {transform.Offset}) / {transform.Scale}";
}
