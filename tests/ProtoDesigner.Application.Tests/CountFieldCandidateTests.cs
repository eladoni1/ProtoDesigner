using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Application.Tests;

/// <summary>
/// Which fields the array editor may offer as a dynamic array's count.
/// </summary>
/// <remarks>
/// The whole point of this list living in Application rather than in the dialog is that it has to agree
/// with <see cref="LayoutEngine"/> and <c>DynamicArrayRule</c>. Offering a field the engine then throws on
/// is worse than offering nothing, so every acceptance case here is also asserted to lay out, and every
/// rejection is one the engine or the validator would have caught.
/// </remarks>
public class CountFieldCandidateTests
{
    private const int Capacity = 20;

    private sealed record Fixture(Project Project, Bus Bus, Message Message, ArrayType Array);

    /// <summary>
    /// A message of `before` fields, then the array, then `after` fields. The builder hands back the
    /// project so a test can assert on the layout as well as the candidate list.
    /// </summary>
    private static Fixture Build(
        Func<Project, IEnumerable<FieldBinding>> before,
        Func<Project, IEnumerable<FieldBinding>>? after = null)
    {
        var project = new Project("Counts");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var array = project.Types.Add(new ArrayType(TypeId.New(), "Payload", u8.Id,
            new ArrayLength.LengthPrefixed(8, Capacity)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "M") { WireId = 1 };

        foreach (var f in before(project)) message.Fields.Add(f);
        message.Fields.Add(new FieldBinding(FieldId.New(), "payload", array.Id));
        foreach (var f in after?.Invoke(project) ?? []) message.Fields.Add(f);

        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return new Fixture(project, bus, message, array);
    }

    private static ParameterType Prim(Project p, string name, PrimitiveKind kind) =>
        p.Types.Add(new ParameterType(TypeId.New(), name, kind));

    private static IReadOnlyList<CountFieldCandidate> Candidates(Fixture f) =>
        CountFieldCandidates.For(f.Project, f.Array.Id, Capacity);

    // ---- what is offered -------------------------------------------------------------------------

    [Fact]
    public void An_unsigned_integer_before_the_array_is_offered()
    {
        var f = Build(p => [new FieldBinding(FieldId.New(), "count", Prim(p, "u8", PrimitiveKind.U8).Id)]);

        var candidate = Assert.Single(Candidates(f));
        Assert.Equal("count", candidate.Path);
        Assert.Equal(8, candidate.WireBits);
        Assert.True(candidate.IsWideEnough);
    }

    [Fact]
    public void A_field_after_the_array_is_not_offered()
    {
        // The engine throws on a forward reference, so offering one would be offering a broken layout.
        var f = Build(
            _ => [],
            p => [new FieldBinding(FieldId.New(), "trailingCount", Prim(p, "u8", PrimitiveKind.U8).Id)]);

        Assert.Empty(Candidates(f));
    }

    [Fact]
    public void A_signed_field_is_not_offered()
    {
        var f = Build(p => [new FieldBinding(FieldId.New(), "count", Prim(p, "i16", PrimitiveKind.I16).Id)]);

        Assert.Empty(Candidates(f));
    }

    [Fact]
    public void A_float_field_is_not_offered()
    {
        var f = Build(p => [new FieldBinding(FieldId.New(), "count", Prim(p, "f32", PrimitiveKind.F32).Id)]);

        Assert.Empty(Candidates(f));
    }

    [Fact]
    public void A_field_inside_an_earlier_struct_is_offered_by_its_dotted_path()
    {
        // Matches the engine, which walks a struct's members into its "seen" set as it lays the struct out.
        var f = Build(p =>
        {
            var u8 = Prim(p, "u8", PrimitiveKind.U8);
            var header = p.Types.Add(new StructType(TypeId.New(), "Header")
                .With(new FieldBinding(FieldId.New(), "count", u8.Id)));
            return [new FieldBinding(FieldId.New(), "header", header.Id)];
        });

        var candidate = Assert.Single(Candidates(f));
        Assert.Equal("header.count", candidate.Path);
    }

    [Fact]
    public void A_field_inside_a_later_struct_is_not_offered()
    {
        var f = Build(_ => [], p =>
        {
            var u8 = Prim(p, "u8b", PrimitiveKind.U8);
            var footer = p.Types.Add(new StructType(TypeId.New(), "Footer")
                .With(new FieldBinding(FieldId.New(), "count", u8.Id)));
            return [new FieldBinding(FieldId.New(), "footer", footer.Id)];
        });

        Assert.Empty(Candidates(f));
    }

    // ---- width ------------------------------------------------------------------------------------

    [Fact]
    public void A_field_too_narrow_for_the_capacity_is_offered_but_flagged()
    {
        // Offered on purpose: narrowing the array and widening the field are both reasonable fixes, and
        // hiding the field would leave the user hunting for one that does not exist. The shortfall is
        // visible instead, and the validator reports it if they save anyway.
        var f = Build(p =>
        [
            new FieldBinding(FieldId.New(), "count", Prim(p, "u8", PrimitiveKind.U8).Id)
            {
                Encoding = FieldEncoding.Sized(4),
            },
        ]);

        var candidate = Assert.Single(Candidates(f));
        Assert.Equal(4, candidate.WireBits);
        Assert.Equal(15ul, candidate.MaxCountable);
        Assert.False(candidate.IsWideEnough);
    }

    [Fact]
    public void The_binding_width_wins_over_the_types_natural_width()
    {
        var f = Build(p =>
        [
            new FieldBinding(FieldId.New(), "count", Prim(p, "u32", PrimitiveKind.U32).Id)
            {
                Encoding = FieldEncoding.Sized(8),
            },
        ]);

        Assert.Equal(8, Assert.Single(Candidates(f)).WireBits);
    }

    // ---- usages -----------------------------------------------------------------------------------

    [Fact]
    public void An_array_used_by_two_messages_reports_both_usages()
    {
        // This is what forces the dialog to warn: ArrayLength.CountFromField holds one FieldId, but the
        // type is project-wide, so pointing it at a field in one message leaves the other unlayoutable.
        var f = Build(p => [new FieldBinding(FieldId.New(), "count", Prim(p, "u8", PrimitiveKind.U8).Id)]);

        var second = new Message(MessageId.New(), "Second") { WireId = 2 };
        second.Fields.Add(new FieldBinding(FieldId.New(), "payload", f.Array.Id));
        f.Bus.Messages.Add(second);

        var usages = CountFieldCandidates.UsagesOf(f.Project, f.Array.Id);

        Assert.Equal(2, usages.Count);
        Assert.Contains(usages, u => u.Message.Name == "M");
        Assert.Contains(usages, u => u.Message.Name == "Second");
    }

    [Fact]
    public void An_unused_array_offers_nothing()
    {
        var project = new Project("Unused");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var array = project.Types.Add(new ArrayType(TypeId.New(), "Payload", u8.Id,
            new ArrayLength.LengthPrefixed(8, Capacity)));

        Assert.Empty(CountFieldCandidates.UsagesOf(project, array.Id));
        Assert.Empty(CountFieldCandidates.For(project, array.Id, Capacity));
    }

    // ---- the promise the list makes ---------------------------------------------------------------

    [Fact]
    public void Every_offered_candidate_actually_lays_out_and_validates()
    {
        // The contract: pick anything from this list and the engine accepts it. Without this the list is
        // just a guess that happens to agree with the engine today.
        var f = Build(p =>
        {
            var u8 = Prim(p, "u8", PrimitiveKind.U8);
            var u16 = Prim(p, "u16", PrimitiveKind.U16);
            var header = p.Types.Add(new StructType(TypeId.New(), "Header")
                .With(new FieldBinding(FieldId.New(), "seq", u8.Id)));

            return
            [
                new FieldBinding(FieldId.New(), "header", header.Id),
                new FieldBinding(FieldId.New(), "count", u8.Id),
                new FieldBinding(FieldId.New(), "wide", u16.Id),
            ];
        });

        var candidates = Candidates(f);
        Assert.Equal(3, candidates.Count);

        foreach (var candidate in candidates)
        {
            f.Array.Length = new ArrayLength.CountFromField(candidate.Field.Id, Capacity);

            var layout = new LayoutEngine().Compute(f.Project, f.Bus, f.Message);
            Assert.True(layout.HasVariableRegions);

            var errors = new Validator().Validate(f.Project)
                .Where(d => d.Severity == Severity.Error)
                .ToList();
            Assert.True(errors.Count == 0, $"'{candidate.Path}' produced {string.Join("; ", errors.Select(e => e.Code))}");
        }
    }

    [Fact]
    public void Choosing_a_count_field_removes_the_length_prefix_from_the_wire()
    {
        // The user-visible point of the whole feature: no more `payload.__length` in the byte map.
        var f = Build(p => [new FieldBinding(FieldId.New(), "count", Prim(p, "u8", PrimitiveKind.U8).Id)]);

        var before = new LayoutEngine().Compute(f.Project, f.Bus, f.Message);
        Assert.Single(before.LengthPrefixes());

        f.Array.Length = new ArrayLength.CountFromField(Candidates(f)[0].Field.Id, Capacity);

        var after = new LayoutEngine().Compute(f.Project, f.Bus, f.Message);
        Assert.Empty(after.LengthPrefixes());
    }
}
