using System.Collections.ObjectModel;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

/// <summary>
/// The project's type library, split by kind so the Types panel can offer one tab per kind with its own
/// "Create …" action.
/// </summary>
public sealed class TypeLibraryViewModel : ObservableObject
{
    private readonly ProjectViewModel _project;

    public TypeLibraryViewModel(ProjectViewModel project)
    {
        _project = project;
        Primitives = new ObservableCollection<TypeItemViewModel>();
        Enums = new ObservableCollection<TypeItemViewModel>();
        Structs = new ObservableCollection<TypeItemViewModel>();
        Arrays = new ObservableCollection<TypeItemViewModel>();
        Rebuild();
    }

    public ObservableCollection<TypeItemViewModel> Primitives { get; }
    public ObservableCollection<TypeItemViewModel> Enums { get; }
    public ObservableCollection<TypeItemViewModel> Structs { get; }
    public ObservableCollection<TypeItemViewModel> Arrays { get; }

    public IEnumerable<TypeItemViewModel> All =>
        Primitives.Concat(Enums).Concat(Structs).Concat(Arrays);

    /// <summary>Rebuilds every bucket from the model. Cheap, and keeps the panel honest after any edit.</summary>
    public void Rebuild()
    {
        Primitives.Clear(); Enums.Clear(); Structs.Clear(); Arrays.Clear();

        foreach (var type in _project.Project.Types.All.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TypeItemViewModel(_project, type);
            switch (type)
            {
                case ParameterType: Primitives.Add(item); break;
                case EnumType: Enums.Add(item); break;
                case StructType: Structs.Add(item); break;
                case ArrayType: Arrays.Add(item); break;
            }
        }
    }

    // ---- creation -------------------------------------------------------------------------------

    /// <summary>
    /// A named primitive: host kind plus limits. The range pre-fills from the kind's full span, so a new
    /// uint8 starts at 0..255 and the user narrows it rather than inventing it.
    /// </summary>
    public ParameterType AddPrimitive(string name, PrimitiveKind kind, NumericRange? range = null)
    {
        var type = new ParameterType(TypeId.New(), UniqueName(name), kind, range ?? kind.NaturalRange());
        _project.Journal.Do(new AddTypeCommand(type));
        Rebuild();
        return type;
    }

    public EnumType AddEnum(string name, PrimitiveKind underlying = PrimitiveKind.U8)
    {
        var type = new EnumType(TypeId.New(), UniqueName(name), underlying);
        _project.Journal.Do(new AddTypeCommand(type));
        Rebuild();
        return type;
    }

    /// <summary>
    /// Adds an enum whose members come from the bus at generation rather than from the user.
    /// </summary>
    /// <remarks>
    /// 32 bits, because a message id is <c>Message.WireId</c> and the editor allows up to four bytes for
    /// one. Narrower would silently cap what a user can assign, and this type exists precisely so the id
    /// does not have to be tracked by hand.
    /// </remarks>
    public EnumType AddSyntheticEnum(SyntheticEnum kind)
    {
        var type = new EnumType(TypeId.New(), UniqueName(kind == SyntheticEnum.MessageId ? "MessageId" : "ModuleId"),
            PrimitiveKind.U32)
        {
            Synthetic = kind,
            WireBits = 32,
        };

        _project.Journal.Do(new AddTypeCommand(type));
        Rebuild();
        return type;
    }

    public StructType AddStruct(string name)
    {
        var type = new StructType(TypeId.New(), UniqueName(name));
        _project.Journal.Do(new AddTypeCommand(type));
        Rebuild();
        return type;
    }

    public ArrayType AddArray(string name, TypeId elementTypeId, int count = 4)
    {
        var type = new ArrayType(TypeId.New(), UniqueName(name), elementTypeId, new ArrayLength.Fixed(count));
        _project.Journal.Do(new AddTypeCommand(type));
        Rebuild();
        return type;
    }

    /// <summary>
    /// Copies a type under a new name. The common case is "same shape, different meaning" — a second
    /// temperature with tighter bounds — which is otherwise tedious to recreate by hand.
    /// </summary>
    public TypeDefinition? Duplicate(TypeItemViewModel item)
    {
        var name = UniqueName(item.Type.Name);
        TypeDefinition copy = item.Type switch
        {
            ParameterType p => CopyParameter(p, name),
            EnumType e => CopyEnum(e, name),
            StructType s => CopyStruct(s, name),
            ArrayType a => new ArrayType(TypeId.New(), name, a.ElementTypeId, a.Length),
            _ => null!,
        };
        if (copy is null) return null;

        copy.Description = item.Type.Description;
        _project.Journal.Do(new AddTypeCommand(copy));
        Rebuild();
        return copy;
    }

    // Duplicating is the supported way to get a second wire representation of the same shape, so the
    // copy starts from the original's settings rather than from defaults.
    private static ParameterType CopyParameter(ParameterType source, string name) =>
        new(TypeId.New(), name, source.Kind, source.Range)
        {
            WireBits = source.WireBits,
            WireForm = source.WireForm,
            WireOffset = source.WireOffset,
            WireScale = source.WireScale,
        };

    private static EnumType CopyEnum(EnumType source, string name)
    {
        var copy = new EnumType(TypeId.New(), name, source.UnderlyingKind, source.IsFlags)
        {
            WireBits = source.WireBits,
            WireOffset = source.WireOffset,
            WireScale = source.WireScale,
        };
        foreach (var m in source.Members) copy.With(m.Name, m.Value);
        return copy;
    }

    private static StructType CopyStruct(StructType source, string name)
    {
        var copy = new StructType(TypeId.New(), name);
        foreach (var f in source.Fields)
            copy.Fields.Add(new FieldBinding(f.Name, f.TypeId, f.Encoding.Clone()) { DefaultValue = f.DefaultValue });
        return copy;
    }

    /// <summary>Removes a type. Refuses when something still points at it, rather than orphaning a reference.</summary>
    public bool Remove(TypeItemViewModel item, out string? reason)
    {
        if (_project.IsTypeInUse(item.Type.Id))
        {
            reason = $"'{item.Type.Name}' is still used by a message or another type. Remove those uses first.";
            return false;
        }

        _project.Journal.Do(new RemoveTypeCommand(item.Type));
        if (ReferenceEquals(_project.SelectedType, item)) _project.SelectedType = null;
        Rebuild();
        reason = null;
        return true;
    }

    /// <summary>
    /// Seeds the plain host primitives so a project can build a message immediately.
    /// </summary>
    /// <remarks>
    /// Safe to call on an existing project: an entry is skipped when a primitive of that kind and name is
    /// already present, so it adds what is missing and leaves everything else alone.
    /// </remarks>
    public void SeedBuiltIns()
    {
        var kinds = new (PrimitiveKind Kind, string Name)[]
        {
            (PrimitiveKind.Bool, "bool"),
            (PrimitiveKind.Char, "char"),
            // Same kind as u8 and the same uint8_t on the wire — this is the C spelling, offered so a
            // field can say "this is a byte of text" where that is what it means.
            (PrimitiveKind.U8, "unsigned char"),
            (PrimitiveKind.U8, "u8"),   (PrimitiveKind.I8, "i8"),
            (PrimitiveKind.U16, "u16"), (PrimitiveKind.I16, "i16"),
            (PrimitiveKind.U32, "u32"), (PrimitiveKind.I32, "i32"),
            (PrimitiveKind.U64, "u64"), (PrimitiveKind.I64, "i64"),
            (PrimitiveKind.F32, "float"), (PrimitiveKind.F64, "double"),
        };

        foreach (var (kind, name) in kinds)
        {
            var exists = _project.Project.Types.All
                .OfType<ParameterType>()
                .Any(p => p.Kind == kind && p.Name == name);
            if (!exists) AddPrimitive(name, kind, kind.NaturalRange());
        }

        // The bus's own identities are always available, never created by hand. Their members come from
        // the bus at generation, so seeding them costs nothing and removes the step where a user has to
        // know they exist before they can use one.
        foreach (var kind in new[] { SyntheticEnum.MessageId, SyntheticEnum.ModuleId })
            if (!_project.Project.Types.All.OfType<EnumType>().Any(e => e.Synthetic == kind))
                AddSyntheticEnum(kind);
    }

    private string UniqueName(string preferred)
    {
        var baseName = string.IsNullOrWhiteSpace(preferred) ? "Type" : preferred;
        var candidate = baseName;
        var i = 2;
        while (_project.Project.Types.All.Any(t => string.Equals(t.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{baseName}{i++}";
        return candidate;
    }
}

/// <summary>One row in the Types panel.</summary>
public sealed class TypeItemViewModel : ObservableObject
{
    private readonly ProjectViewModel _project;

    public TypeItemViewModel(ProjectViewModel project, TypeDefinition type)
    {
        _project = project;
        Type = type;
    }

    public TypeDefinition Type { get; }

    public string Name
    {
        get => Type.Name;
        set
        {
            if (Type.Name == value || string.IsNullOrWhiteSpace(value)) return;
            _project.Journal.Do(new RenameTypeCommand(Type, value));
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailLabel));
        }
    }

    public string Kind => Type switch
    {
        ParameterType => "Primitive",
        EnumType => "Enum",
        StructType => "Struct",
        ArrayType => "Array",
        _ => "Unknown",
    };

    /// <summary>The one-line summary under the name: host size and limits, which is what makes a type usable.</summary>
    public string DetailLabel => Type switch
    {
        ParameterType p => p.Range is { } r
            ? $"{p.Kind} · {p.Kind.NaturalBits() / 8} B · {Trim(r.Min)} .. {Trim(r.Max)}"
            : $"{p.Kind} · {p.Kind.NaturalBits() / 8} B · unbounded",
        EnumType e => $"{e.UnderlyingKind} · {e.Members.Count} member(s)",
        StructType s => $"{s.Fields.Count} field(s)",
        ArrayType a => $"{ElementName(a)} × {DescribeLength(a.Length)}",
        _ => string.Empty,
    };

    private string ElementName(ArrayType a) => _project.TypeName(a.ElementTypeId);

    private static string DescribeLength(ArrayLength length)
    {
        // The bounds come from MinimumCount rather than a hardcoded 0: a declared floor is the difference
        // between "might be empty" and "always carries something", and this row is where a user looks.
        var span = $"{length.MinimumCount}..{length.Capacity}";

        return length switch
        {
            ArrayLength.Fixed f => $"{f.Count}",
            ArrayLength.CountFromField => $"{span} (count field)",
            ArrayLength.LengthPrefixed l => $"{span} ({l.PrefixBits}-bit prefix)",
            ArrayLength.Terminated => $"{span} (sentinel)",
            ArrayLength.FillRemaining => $"{span} (fills frame)",
            _ => "?",
        };
    }

    public bool IsInUse => _project.IsTypeInUse(Type.Id);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(IsInUse));
    }

    private static string Trim(decimal value) =>
        value == decimal.Truncate(value) && Math.Abs(value) < 1e15m
            ? decimal.Truncate(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
