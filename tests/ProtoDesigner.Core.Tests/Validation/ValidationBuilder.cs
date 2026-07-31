using ProtoDesigner.Core.Validation.Rules;

namespace ProtoDesigner.Core.Tests.Validation;

/// <summary>
/// Terse builders on top of the layout <see cref="ModelBuilder"/>, tuned for validation tests. Each test
/// starts with a fresh project + bus; add types and messages fluently, then assert against the diagnostics.
/// </summary>
internal sealed class ValidationBuilder
{
    public ValidationBuilder(Transport transport = Transport.Ethernet)
    {
        Project = new Project("TestProject");
        Bus = new Bus("TestBus", transport);
        Project.Buses.Add(Bus);
    }

    public Project Project { get; }

    public Bus Bus { get; }

    public T AddType<T>(T type) where T : TypeDefinition
    {
        Project.Types.Add(type);
        return type;
    }

    public ParameterType Prim(string name, PrimitiveKind kind, NumericRange? range = null) =>
        AddType(new ParameterType(TypeId.New(), name, kind, range));

    public EnumType Enum(string name, PrimitiveKind underlying, params (string Name, long Value)[] members)
    {
        var e = AddType(new EnumType(TypeId.New(), name, underlying));
        foreach (var (n, v) in members) e.With(n, v);
        return e;
    }

    public StructType Struct(string name, params FieldBinding[] fields) =>
        AddType(new StructType(TypeId.New(), name)).With(fields);

    public ArrayType Array(string name, TypeDefinition element, ArrayLength length) =>
        AddType(new ArrayType(TypeId.New(), name, element.Id, length));

    public Message NewMessage(string name, params FieldBinding[] fields)
    {
        var m = new Message(name);
        foreach (var f in fields) m.Fields.Add(f);
        Bus.Messages.Add(m);
        return m;
    }

    public static FieldBinding F(string name, TypeDefinition type, FieldEncoding? encoding = null) =>
        new(name, type.Id, encoding);

    public IReadOnlyList<Diagnostic> Run() => new Validator().Validate(Project);

    public IReadOnlyList<Diagnostic> Run(IValidationRule rule) => new Validator(new[] { rule }).Validate(Project);
}

internal static class DiagnosticAssert
{
    /// <summary>Asserts that the given code appears at least once.</summary>
    public static Diagnostic Has(this IReadOnlyList<Diagnostic> diagnostics, string code)
    {
        var found = diagnostics.FirstOrDefault(d => d.Code == code);
        Assert.NotNull(found);
        return found!;
    }

    /// <summary>Asserts that the given code does not appear.</summary>
    public static void HasNo(this IReadOnlyList<Diagnostic> diagnostics, string code)
    {
        var found = diagnostics.FirstOrDefault(d => d.Code == code);
        Assert.Null(found);
    }

    /// <summary>Asserts the diagnostic set contains no Errors.</summary>
    public static void NoErrors(this IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == Severity.Error).ToArray();
        Assert.True(errors.Length == 0, "Expected no Errors, got:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }
}
