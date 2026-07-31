namespace ProtoDesigner.Core.Model;

/// <summary>Identity of a <see cref="TypeDefinition"/>. Stable for the lifetime of the type; names are display-only.</summary>
public readonly record struct TypeId(Guid Value)
{
    public static TypeId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identity of a <see cref="FieldBinding"/>. Unique per binding, not per field definition.</summary>
public readonly record struct FieldId(Guid Value)
{
    public static FieldId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct BusId(Guid Value)
{
    public static BusId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identity of a <see cref="Module"/> on a bus. Routes reference this, so renaming a module is safe.</summary>
public readonly record struct ModuleId(Guid Value)
{
    public static ModuleId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}
