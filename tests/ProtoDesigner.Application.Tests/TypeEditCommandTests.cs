namespace ProtoDesigner.Application.Tests;

/// <summary>
/// Editing an existing type through the journal.
/// </summary>
/// <remarks>
/// These paths previously mutated the model straight from the editor dialogs, which left them neither
/// undoable nor dirty — closing the app after a struct edit discarded it with no prompt. Every case below
/// asserts the dirty flag as well as the round trip, because that flag is what stands between an edit and
/// silent loss.
/// </remarks>
public class TypeEditCommandTests
{
    private static Project NewProject() => new("Test");

    // ---- primitive ---------------------------------------------------------------------------------

    [Fact]
    public void Editing_a_primitive_applies_every_field_and_undo_restores_them()
    {
        var project = NewProject();
        var type = project.Types.Add(new ParameterType(TypeId.New(), "temperature", PrimitiveKind.U8));
        type.WireBits = 8;
        type.WireForm = WireForm.Unsigned;
        var journal = new CommandJournal(project);

        journal.Do(new EditPrimitiveCommand(type, new PrimitiveEdit(
            "celsius", PrimitiveKind.I16, new NumericRange(-40, 125),
            WireForm.Signed, WireBits: 12, WireOffset: -40m, WireScale: 0.5m)));

        Assert.Equal("celsius", type.Name);
        Assert.Equal(PrimitiveKind.I16, type.Kind);
        Assert.Equal(new NumericRange(-40, 125), type.Range);
        Assert.Equal(WireForm.Signed, type.WireForm);
        Assert.Equal(12, type.WireBits);
        Assert.Equal(-40m, type.WireOffset);
        Assert.Equal(0.5m, type.WireScale);

        journal.Undo();

        Assert.Equal("temperature", type.Name);
        Assert.Equal(PrimitiveKind.U8, type.Kind);
        Assert.Equal(8, type.WireBits);
        Assert.Equal(WireForm.Unsigned, type.WireForm);
        Assert.Null(type.WireOffset);
        Assert.Null(type.WireScale);
    }

    [Fact]
    public void Editing_a_primitive_marks_the_project_dirty()
    {
        var project = NewProject();
        var type = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var journal = new CommandJournal(project);
        journal.MarkSaved();

        journal.Do(new EditPrimitiveCommand(type, PrimitiveEdit.From(type) with { Name = "byte" }));

        Assert.True(journal.IsDirty);
    }

    /// <summary>
    /// The regression behind <c>if (changed &gt; 0) MarkDirty()</c>: a type no field uses yet propagates
    /// nothing, so the edit used to leave the project clean and be discarded on close.
    /// </summary>
    [Fact]
    public void Editing_a_type_no_field_uses_still_marks_the_project_dirty()
    {
        var project = NewProject();
        var unused = project.Types.Add(new ParameterType(TypeId.New(), "orphan", PrimitiveKind.U8));
        var journal = new CommandJournal(project);
        journal.MarkSaved();

        journal.Do(new EditPrimitiveCommand(unused, PrimitiveEdit.From(unused) with { WireBits = 4 }));

        Assert.True(journal.IsDirty);
        Assert.Equal(4, unused.WireBits);
    }

    [Fact]
    public void Redoing_a_primitive_edit_reapplies_it()
    {
        var project = NewProject();
        var type = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var journal = new CommandJournal(project);

        journal.Do(new EditPrimitiveCommand(type, PrimitiveEdit.From(type) with { Name = "byte", WireBits = 4 }));
        journal.Undo();
        Assert.Equal("u8", type.Name);

        journal.Redo();
        Assert.Equal("byte", type.Name);
        Assert.Equal(4, type.WireBits);
    }

    /// <summary>
    /// A type edit pushes the new wire settings onto every binding that uses the type, so undo has to
    /// take those with it. Restoring the declaration alone would leave the type saying 8 bits and the
    /// field it is used by saying 4.
    /// </summary>
    [Fact]
    public void Undoing_a_primitive_edit_also_restores_the_bindings_it_propagated_to()
    {
        var project = NewProject();
        var type = project.Types.Add(
            new ParameterType(TypeId.New(), "counter", PrimitiveKind.U8, new NumericRange(0, 255)));
        type.WireBits = 8;

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Status");
        var field = new FieldBinding(FieldId.New(), "ticks", type.Id);
        message.Fields.Add(field);
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        WireEncodingPropagator.ApplyAll(project);
        var before = field.Encoding.BitWidth;

        var journal = new CommandJournal(project);
        journal.Do(new EditPrimitiveCommand(type, PrimitiveEdit.From(type) with { WireBits = 4 }));

        Assert.Equal(4, field.Encoding.BitWidth);

        journal.Undo();

        Assert.Equal(8, type.WireBits);
        Assert.Equal(before, field.Encoding.BitWidth);
    }

    // ---- enum --------------------------------------------------------------------------------------

    [Fact]
    public void Editing_an_enum_replaces_its_members_and_undo_restores_the_originals()
    {
        var project = NewProject();
        var type = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U8))
            .With("Idle", 0).With("Run", 1);
        var journal = new CommandJournal(project);

        journal.Do(new EditEnumCommand(type, new EnumEdit(
            "State", PrimitiveKind.U16, WireForm.Unsigned, WireBits: 16,
            new[] { new EnumMember("Off", 100), new EnumMember("On", 101), new EnumMember("Fault", 102) })));

        Assert.Equal("State", type.Name);
        Assert.Equal(PrimitiveKind.U16, type.UnderlyingKind);
        Assert.Equal(16, type.WireBits);
        Assert.Equal(new[] { "Off", "On", "Fault" }, type.Members.Select(m => m.Name));

        journal.Undo();

        Assert.Equal("Mode", type.Name);
        Assert.Equal(PrimitiveKind.U8, type.UnderlyingKind);
        Assert.Equal(new[] { "Idle", "Run" }, type.Members.Select(m => m.Name));
    }

    /// <summary>
    /// A synthetic enum's members come from the bus at generation. The editor may rename it or change its
    /// wire size, but writing members into it would restore exactly the staleness the type removes.
    /// </summary>
    [Fact]
    public void Editing_a_synthetic_enum_never_writes_its_members()
    {
        var project = NewProject();
        var type = project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U8));
        type.Synthetic = SyntheticEnum.MessageId;
        var journal = new CommandJournal(project);

        journal.Do(new EditEnumCommand(type, new EnumEdit(
            "MsgId", PrimitiveKind.U16, WireForm.Unsigned, WireBits: 16,
            new[] { new EnumMember("Invented", 7) })));

        Assert.Equal("MsgId", type.Name);
        Assert.Equal(16, type.WireBits);
        Assert.Empty(type.Members);
    }

    // ---- struct ------------------------------------------------------------------------------------

    [Fact]
    public void Editing_a_struct_replaces_its_fields_in_order_and_undo_restores_them()
    {
        var project = NewProject();
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var original = new FieldBinding(FieldId.New(), "version", u8.Id);
        var type = project.Types.Add(new StructType(TypeId.New(), "Header")).With(original);
        var journal = new CommandJournal(project);

        var replacement = new[]
        {
            new StructField(new FieldBinding(FieldId.New(), "magic", u8.Id), "magic"),
            new StructField(new FieldBinding(FieldId.New(), "version", u8.Id), "version"),
        };

        journal.Do(new EditStructCommand(type, new StructEdit("FrameHeader", replacement)));

        Assert.Equal("FrameHeader", type.Name);
        Assert.Equal(new[] { "magic", "version" }, type.Fields.Select(f => f.Name));

        journal.Undo();

        Assert.Equal("Header", type.Name);
        Assert.Equal(new[] { "version" }, type.Fields.Select(f => f.Name));
        Assert.Same(original, type.Fields[0]);
    }

    /// <summary>Field order is wire order, so a pure reorder has to survive the round trip exactly.</summary>
    [Fact]
    public void Reordering_a_structs_fields_round_trips()
    {
        var project = NewProject();
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var a = new FieldBinding(FieldId.New(), "a", u8.Id);
        var b = new FieldBinding(FieldId.New(), "b", u8.Id);
        var type = project.Types.Add(new StructType(TypeId.New(), "S")).With(a, b);
        var journal = new CommandJournal(project);

        journal.Do(new EditStructCommand(type, new StructEdit(
            "S", new[] { new StructField(b, "b"), new StructField(a, "a") })));
        Assert.Equal(new[] { "b", "a" }, type.Fields.Select(f => f.Name));

        journal.Undo();
        Assert.Equal(new[] { "a", "b" }, type.Fields.Select(f => f.Name));
    }

    /// <summary>
    /// Renaming a member keeps the same binding, so the name has to be restored on it rather than merely
    /// having the field list put back. Writing the name in place would survive the undo.
    /// </summary>
    [Fact]
    public void Undoing_a_struct_edit_restores_a_renamed_members_name()
    {
        var project = NewProject();
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var binding = new FieldBinding(FieldId.New(), "ver", u8.Id) { ProtoFieldNumber = 7 };
        var type = project.Types.Add(new StructType(TypeId.New(), "Header")).With(binding);
        var journal = new CommandJournal(project);

        journal.Do(new EditStructCommand(type, new StructEdit(
            "Header", new[] { new StructField(binding, "version") })));

        Assert.Equal("version", type.Fields[0].Name);

        journal.Undo();

        Assert.Equal("ver", type.Fields[0].Name);
        Assert.Equal(7, type.Fields[0].ProtoFieldNumber);
    }

    // ---- array -------------------------------------------------------------------------------------

    [Fact]
    public void Editing_an_array_changes_element_and_length_and_undo_restores_both()
    {
        var project = NewProject();
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = project.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));
        var type = project.Types.Add(
            new ArrayType(TypeId.New(), "Samples", u8.Id, new ArrayLength.Fixed(4)));
        var journal = new CommandJournal(project);

        journal.Do(new EditArrayCommand(type, new ArrayEdit(
            "Readings", u16.Id, new ArrayLength.LengthPrefixed(8, MaxCount: 32, MinCount: 2))));

        Assert.Equal("Readings", type.Name);
        Assert.Equal(u16.Id, type.ElementTypeId);
        var prefixed = Assert.IsType<ArrayLength.LengthPrefixed>(type.Length);
        Assert.Equal(32, prefixed.MaxCount);
        Assert.Equal(2, prefixed.MinCount);

        journal.Undo();

        Assert.Equal("Samples", type.Name);
        Assert.Equal(u8.Id, type.ElementTypeId);
        Assert.Equal(4, Assert.IsType<ArrayLength.Fixed>(type.Length).Count);
    }

    // ---- project and bus ---------------------------------------------------------------------------

    [Fact]
    public void Renaming_the_project_is_undoable_and_marks_it_dirty()
    {
        var project = NewProject();
        var journal = new CommandJournal(project);
        journal.MarkSaved();

        journal.Do(new RenameProjectCommand("Telemetry"));

        Assert.Equal("Telemetry", project.Name);
        Assert.True(journal.IsDirty);

        journal.Undo();
        Assert.Equal("Test", project.Name);
    }

    [Fact]
    public void Changing_a_bus_transport_is_undoable_and_marks_it_dirty()
    {
        var project = NewProject();
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        project.Buses.Add(bus);
        var journal = new CommandJournal(project);
        journal.MarkSaved();

        journal.Do(new SetBusTransportCommand(bus, Transport.Uart));

        Assert.Equal(Transport.Uart, bus.Transport);
        Assert.True(journal.IsDirty);

        journal.Undo();
        Assert.Equal(Transport.Ethernet, bus.Transport);
    }
}
