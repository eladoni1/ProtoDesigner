using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application.Commands;

// Editing an existing type mutates it in place, so an undoable edit needs the whole editable state
// captured before the change. Each record below is that state for one kind: Apply keeps the old one and
// writes the new, Undo writes the old one back. Creating a type is already journalled by AddTypeCommand.

/// <summary>
/// The binding encodings a propagation overwrote.
/// </summary>
/// <remarks>
/// A type's wire settings are pushed onto every field that uses it, so a type edit changes bindings the
/// user never selected. Those writes have to travel with the edit: undoing the type alone would restore
/// the declaration while leaving every occurrence at the width the undone edit chose, which is a model
/// that says two different things about the same field.
/// </remarks>
internal sealed class PropagatedEncodings
{
    private readonly List<(FieldBinding Binding, FieldEncoding Previous)> _saved = new();

    public void Propagate(Project project, TypeDefinition type)
    {
        _saved.Clear();
        foreach (var binding in WireEncodingPropagator.AllBindings(project))
        {
            if (binding.TypeId != type.Id) continue;
            _saved.Add((binding, binding.Encoding.Clone()));
        }

        WireEncodingPropagator.Apply(project, type);
    }

    public void Restore()
    {
        foreach (var (binding, previous) in _saved) binding.Encoding = previous;
    }
}

/// <summary>The editable state of a <see cref="ParameterType"/>.</summary>
public sealed record PrimitiveEdit(
    string Name,
    PrimitiveKind Kind,
    NumericRange? Range,
    WireForm WireForm,
    int? WireBits,
    decimal? WireOffset,
    decimal? WireScale)
{
    public static PrimitiveEdit From(ParameterType type) => new(
        type.Name, type.Kind, type.Range, type.WireForm, type.WireBits, type.WireOffset, type.WireScale);

    public void ApplyTo(ParameterType type)
    {
        type.Name = Name;
        type.Kind = Kind;
        type.Range = Range;
        type.WireForm = WireForm;
        type.WireBits = WireBits;
        type.WireOffset = WireOffset;
        type.WireScale = WireScale;
    }
}

/// <summary>The editable state of an <see cref="EnumType"/>.</summary>
public sealed record EnumEdit(
    string Name,
    PrimitiveKind UnderlyingKind,
    WireForm WireForm,
    int? WireBits,
    IReadOnlyList<EnumMember> Members)
{
    public static EnumEdit From(EnumType type) => new(
        type.Name, type.UnderlyingKind, type.WireForm, type.WireBits, type.Members.ToList());

    public void ApplyTo(EnumType type)
    {
        type.Name = Name;
        type.UnderlyingKind = UnderlyingKind;
        type.WireForm = WireForm;
        type.WireBits = WireBits;

        // A synthetic enum's members are filled in per bus at generation, so the editor never owns them.
        if (type.Synthetic != SyntheticEnum.None) return;

        type.Members.Clear();
        foreach (var member in Members) type.Members.Add(member);
    }
}

/// <summary>
/// One member of a struct as the editor holds it: the binding itself, plus the name it should carry.
/// </summary>
/// <remarks>
/// The name travels beside the binding rather than being written into it, because a rename made in place
/// would survive an undo that restored the field list. The binding object is reused rather than rebuilt
/// so that everything else it carries — its id, its encoding, its protobuf field number — is kept by
/// construction instead of by remembering to copy it.
/// </remarks>
public sealed record StructField(FieldBinding Binding, string Name);

/// <summary>The editable state of a <see cref="StructType"/>. Field order is wire order, so it is kept.</summary>
public sealed record StructEdit(string Name, IReadOnlyList<StructField> Fields)
{
    public static StructEdit From(StructType type) =>
        new(type.Name, type.Fields.Select(f => new StructField(f, f.Name)).ToList());

    public void ApplyTo(StructType type)
    {
        type.Name = Name;
        type.Fields.Clear();
        foreach (var field in Fields)
        {
            field.Binding.Name = field.Name;
            type.Fields.Add(field.Binding);
        }
    }
}

/// <summary>The editable state of an <see cref="ArrayType"/>.</summary>
public sealed record ArrayEdit(string Name, TypeId ElementTypeId, ArrayLength Length)
{
    public static ArrayEdit From(ArrayType type) => new(type.Name, type.ElementTypeId, type.Length);

    public void ApplyTo(ArrayType type)
    {
        type.Name = Name;
        type.ElementTypeId = ElementTypeId;
        type.Length = Length;
    }
}

public sealed class EditPrimitiveCommand : IEditCommand
{
    private readonly ParameterType _type;
    private readonly PrimitiveEdit _next;
    private readonly PropagatedEncodings _propagated = new();
    private PrimitiveEdit? _previous;

    public EditPrimitiveCommand(ParameterType type, PrimitiveEdit next)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public string Describe() => $"Edit type '{_next.Name}'";

    public void Apply(Project project)
    {
        _previous = PrimitiveEdit.From(_type);
        _next.ApplyTo(_type);
        _propagated.Propagate(project, _type);
    }

    public void Undo(Project _)
    {
        _previous?.ApplyTo(_type);
        _propagated.Restore();
    }
}

public sealed class EditEnumCommand : IEditCommand
{
    private readonly EnumType _type;
    private readonly EnumEdit _next;
    private readonly PropagatedEncodings _propagated = new();
    private EnumEdit? _previous;

    public EditEnumCommand(EnumType type, EnumEdit next)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public string Describe() => $"Edit enum '{_next.Name}'";

    public void Apply(Project project)
    {
        _previous = EnumEdit.From(_type);
        _next.ApplyTo(_type);
        _propagated.Propagate(project, _type);
    }

    public void Undo(Project _)
    {
        _previous?.ApplyTo(_type);
        _propagated.Restore();
    }
}

public sealed class EditStructCommand : IEditCommand
{
    private readonly StructType _type;
    private readonly StructEdit _next;
    private StructEdit? _previous;

    public EditStructCommand(StructType type, StructEdit next)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public string Describe() => $"Edit struct '{_next.Name}'";

    public void Apply(Project _)
    {
        _previous = StructEdit.From(_type);
        _next.ApplyTo(_type);
    }

    public void Undo(Project _) => _previous?.ApplyTo(_type);
}

public sealed class EditArrayCommand : IEditCommand
{
    private readonly ArrayType _type;
    private readonly ArrayEdit _next;
    private ArrayEdit? _previous;

    public EditArrayCommand(ArrayType type, ArrayEdit next)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public string Describe() => $"Edit array '{_next.Name}'";

    public void Apply(Project _)
    {
        _previous = ArrayEdit.From(_type);
        _next.ApplyTo(_type);
    }

    public void Undo(Project _) => _previous?.ApplyTo(_type);
}
