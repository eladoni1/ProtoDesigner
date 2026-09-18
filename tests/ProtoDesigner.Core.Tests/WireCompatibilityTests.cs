using ProtoDesigner.Core.Compatibility;

namespace ProtoDesigner.Core.Tests;

/// <summary>
/// Comparing two versions of a protocol for what it does to a peer already in the field.
/// </summary>
/// <remarks>
/// Every case builds the same project twice from fixed ids — the way two checkouts of one file would — and
/// changes exactly one thing in the second. That is what makes the assertions meaningful: identity is
/// shared, so a finding is about the change rather than about two unrelated models.
/// </remarks>
public class WireCompatibilityTests
{
    private static readonly BusId TheBus = new(Guid.Parse("b0000000-0000-0000-0000-000000000001"));
    private static readonly MessageId TheMessage = new(Guid.Parse("d0000000-0000-0000-0000-000000000001"));
    private static readonly MessageId OtherMessage = new(Guid.Parse("d0000000-0000-0000-0000-000000000002"));
    private static readonly TypeId U8 = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
    private static readonly TypeId U16 = new(Guid.Parse("70000000-0000-0000-0000-000000000002"));
    private static readonly FieldId First = new(Guid.Parse("f0000000-0000-0000-0000-000000000001"));
    private static readonly FieldId Second = new(Guid.Parse("f0000000-0000-0000-0000-000000000002"));

    /// <summary>A two-field message: a byte then a 16-bit word, little-endian, byte aligned.</summary>
    private static Project Build()
    {
        var project = new Project("Telemetry");
        project.Types.Add(new ParameterType(U8, "u8", PrimitiveKind.U8));
        project.Types.Add(new ParameterType(U16, "u16", PrimitiveKind.U16));

        var message = new Message(TheMessage, "Status") { WireId = 42 };
        message.Fields.Add(new FieldBinding(First, "mode", U8));
        message.Fields.Add(new FieldBinding(Second, "voltage", U16));

        var bus = new Bus(TheBus, "Main", Transport.Ethernet);
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return project;
    }

    private static Message MessageOf(Project project) => project.Buses[0].Messages[0];

    private static IReadOnlyList<Diagnostic> Breaking(IReadOnlyList<Diagnostic> report) =>
        report.Where(d => d.Severity == Severity.Warning).ToList();

    // ---- the safe half of the table -----------------------------------------------------------------

    [Fact]
    public void An_unchanged_project_breaks_nothing()
    {
        var report = WireCompatibility.Compare(Build(), Build());

        Assert.Empty(report);
    }

    /// <summary>
    /// The claim this whole tool rests on: identity is an id, so a rename costs nothing. If this ever goes
    /// red, renaming has become a wire change and rule 3 has been broken somewhere upstream.
    /// </summary>
    [Fact]
    public void Renaming_a_field_is_safe()
    {
        var current = Build();
        MessageOf(current).Fields[0].Name = "operatingMode";

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Empty(Breaking(report));
        Assert.Contains(report, d => d.Code == DiagnosticCodes.RenamedSafely);
    }

    [Fact]
    public void Renaming_a_message_is_safe()
    {
        var current = Build();
        MessageOf(current).Name = "Health";

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Empty(Breaking(report));
        Assert.Contains(report, d => d.Code == DiagnosticCodes.RenamedSafely);
    }

    [Fact]
    public void Renaming_a_bus_is_safe()
    {
        var current = Build();
        current.Buses[0].Name = "Primary";

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Empty(Breaking(report));
    }

    [Fact]
    public void Adding_a_message_is_safe_because_nobody_decodes_an_unknown_wire_id()
    {
        var current = Build();
        var added = new Message(OtherMessage, "Debug") { WireId = 99 };
        added.Fields.Add(new FieldBinding(FieldId.New(), "code", U8));
        current.Buses[0].Messages.Add(added);

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Empty(Breaking(report));
        Assert.Contains(report, d => d.Code == DiagnosticCodes.MessageAdded && d.Severity == Severity.Info);
    }

    // ---- the breaking half --------------------------------------------------------------------------

    [Fact]
    public void Reordering_fields_moves_everything_after_the_first_change()
    {
        var current = Build();
        var fields = MessageOf(current).Fields;
        (fields[0], fields[1]) = (fields[1], fields[0]);

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Contains(report, d => d.Code == DiagnosticCodes.FieldMoved);
        Assert.All(Breaking(report), d => Assert.Equal(Severity.Warning, d.Severity));
    }

    [Fact]
    public void Narrowing_a_field_is_breaking()
    {
        var current = Build();
        MessageOf(current).Fields[1].Encoding.BitWidth = 12;

        var report = WireCompatibility.Compare(Build(), current);

        var resized = Assert.Single(report, d => d.Code == DiagnosticCodes.FieldResized);
        Assert.Equal(Severity.Warning, resized.Severity);
        Assert.Contains("16", resized.Message, StringComparison.Ordinal);
        Assert.Contains("12", resized.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Widening_a_field_is_breaking_too_because_everything_after_it_shifts()
    {
        var current = Build();
        MessageOf(current).Fields[0].Encoding.BitWidth = 16;

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Contains(report, d => d.Code == DiagnosticCodes.FieldResized);
        Assert.Contains(report, d => d.Code == DiagnosticCodes.FieldMoved);
    }

    [Fact]
    public void Adding_a_field_to_an_existing_message_is_breaking()
    {
        var current = Build();
        MessageOf(current).Fields.Add(new FieldBinding(FieldId.New(), "extra", U8));

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Contains(report, d => d.Code == DiagnosticCodes.FieldAdded && d.Severity == Severity.Warning);
    }

    [Fact]
    public void Removing_a_field_is_breaking()
    {
        var current = Build();
        MessageOf(current).Fields.RemoveAt(0);

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Contains(report, d => d.Code == DiagnosticCodes.FieldRemoved);
    }

    [Fact]
    public void Changing_a_wire_id_stops_a_receiver_recognising_the_message()
    {
        var current = Build();
        MessageOf(current).WireId = 43;

        var report = WireCompatibility.Compare(Build(), current);

        var changed = Assert.Single(report, d => d.Code == DiagnosticCodes.WireIdChanged);
        Assert.Equal(Severity.Warning, changed.Severity);
    }

    [Fact]
    public void Removing_a_message_is_breaking()
    {
        var current = Build();
        current.Buses[0].Messages.Clear();

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Contains(report, d => d.Code == DiagnosticCodes.MessageRemoved);
    }

    [Fact]
    public void Removing_a_whole_bus_reports_every_message_it_carried()
    {
        var current = Build();
        current.Buses.Clear();

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Contains(report, d => d.Code == DiagnosticCodes.MessageRemoved);
    }

    [Fact]
    public void Changing_byte_order_is_breaking()
    {
        var current = Build();
        MessageOf(current).Fields[1].Encoding.Endianness = Endianness.Big;

        var report = WireCompatibility.Compare(Build(), current);

        var changed = Assert.Single(report, d => d.Code == DiagnosticCodes.FieldEncodingChanged);
        Assert.Contains("byte order", changed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_a_transform_means_the_same_bytes_stand_for_a_different_value()
    {
        var current = Build();
        MessageOf(current).Fields[1].Encoding.Transform = new ScalarTransform(-40m, 1m);

        var report = WireCompatibility.Compare(Build(), current);

        Assert.Contains(report, d => d.Code == DiagnosticCodes.FieldEncodingChanged);
    }

    // ---- the identity-chain corner ------------------------------------------------------------------

    /// <summary>
    /// One struct used twice contributes its members twice, reached by the same inner binding id. Keying
    /// on that id alone would collapse the two into one entry and hide a change to whichever lost.
    /// </summary>
    [Fact]
    public void A_struct_used_twice_is_compared_per_occurrence()
    {
        static Project BuildWithPair(int secondWidth)
        {
            var project = new Project("Telemetry");
            project.Types.Add(new ParameterType(U8, "u8", PrimitiveKind.U8));

            var inner = new FieldId(Guid.Parse("f0000000-0000-0000-0000-0000000000aa"));
            var header = project.Types.Add(new StructType(
                new TypeId(Guid.Parse("70000000-0000-0000-0000-0000000000aa")), "Header"));
            header.Fields.Add(new FieldBinding(inner, "version", U8));

            var message = new Message(TheMessage, "Status") { WireId = 42 };
            message.Fields.Add(new FieldBinding(First, "from", header.Id));
            message.Fields.Add(new FieldBinding(Second, "to", header.Id,
                new FieldEncoding()));
            message.Fields.Add(new FieldBinding(
                new FieldId(Guid.Parse("f0000000-0000-0000-0000-0000000000bb")), "tail", U8)
            {
                Encoding = new FieldEncoding { BitWidth = secondWidth },
            });

            var bus = new Bus(TheBus, "Main", Transport.Ethernet);
            bus.Messages.Add(message);
            project.Buses.Add(bus);
            return project;
        }

        var report = WireCompatibility.Compare(BuildWithPair(8), BuildWithPair(8));
        Assert.Empty(report);

        // Both occurrences of the struct are present and distinct, so the trailing field is the only change.
        var changed = WireCompatibility.Compare(BuildWithPair(8), BuildWithPair(4));
        Assert.Contains(changed, d => d.Code == DiagnosticCodes.FieldResized);
    }
}
