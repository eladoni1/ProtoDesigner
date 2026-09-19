using ProtoDesigner.Application;
using ProtoDesigner.Core.Merge;
using ProtoDesigner.Persistence.Contract;
using ProtoDesigner.Persistence.Json;

namespace ProtoDesigner.Persistence.Sql.Tests;

/// <summary>The SQL implementation against the shared repository contract.</summary>
public sealed class SqlProjectRepositoryContractTests : ProjectRepositoryContract, IDisposable
{
    private readonly SqliteStore _store = new();

    protected override (IProjectRepository Repository, string Path) NewStore() =>
        (_store.Repository, $"project-{Guid.NewGuid():N}");

    public void Dispose() => _store.Dispose();
}

/// <summary>A throwaway database file, deleted with the test.</summary>
internal sealed class SqliteStore : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), $"protodesigner-sql-{Guid.NewGuid():N}.db");

    public SqliteStore() => Repository = new SqlProjectRepository($"Data Source={_file}");

    public SqlProjectRepository Repository { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_file)) File.Delete(_file);
    }
}

/// <summary>
/// The whole model through the SQL mapping, checked against the backend that already worked.
/// </summary>
/// <remarks>
/// <para>
/// A new mapping fails by <em>dropping</em> things, not by throwing: a column nobody wrote, a variant the
/// switch does not cover, a decimal rounded through a float. So rather than asserting field by field —
/// which only ever checks what the author remembered — this round-trips one rich project through both
/// backends and asks <see cref="ProjectMerge"/> whether they are the same project.
/// </para>
/// <para>
/// That is the sharpest question available, because the merge is already the thing that has to be able to
/// see every edit: <c>EntityStateCoverageTests</c> pins it against the model by reflection, so anything a
/// property carries, the merge notices. A mapping that loses a wire offset or a route shows up here as an
/// incoming change from a project nobody edited.
/// </para>
/// </remarks>
public sealed class SqlMatchesJsonTests : IDisposable
{
    private readonly SqliteStore _sql = new();
    private readonly string _jsonPath =
        Path.Combine(Path.GetTempPath(), $"protodesigner-sql-x-{Guid.NewGuid():N}.pdproj");

    public void Dispose()
    {
        _sql.Dispose();
        if (File.Exists(_jsonPath)) File.Delete(_jsonPath);
    }

    /// <summary>One of every shape the mapping has a branch for.</summary>
    private static Project Rich()
    {
        var project = new Project("Telemetry") { Options = { Endianness = Endianness.Big } };

        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8,
                                                     new NumericRange(0, 255)));
        var count = project.Types.Add(new ParameterType(TypeId.New(), "count", PrimitiveKind.U16)
        {
            // Decimals the size of the far end of the 64-bit domain: a float column would round these,
            // which is precisely why they are stored as text.
            WireBits = 12,
            WireForm = WireForm.Signed,
            WireOffset = 9007199254740993m,
            WireScale = 0.0001m,
        });

        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U8, isFlags: true)
            .With("Idle", 0).With("Active", 100));

        // Seeded into every project and its members are derived per bus, so they must never be stored.
        project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U16)
        {
            Synthetic = SyntheticEnum.MessageId,
        });

        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding("version", u8.Id), new FieldBinding("flags", mode.Id)));

        var counter = new FieldBinding("counter", count.Id);
        var samples = project.Types.Add(new ArrayType(TypeId.New(), "Samples", u8.Id,
            new ArrayLength.CountFromField(counter.Id, MaxCount: 16, MinCount: 2)));
        var trailer = project.Types.Add(new ArrayType(TypeId.New(), "Trailer", u8.Id,
            new ArrayLength.Terminated(new byte[] { 0xDE, 0xAD }, MaxCount: 8)));

        var bus = new Bus(BusId.New(), "Main", Transport.Uart) { Options = { PadToByteBoundary = false } };
        var sensor = bus.AddModule("Sensor");
        var logger = bus.AddModule("Logger");

        var message = new Message(MessageId.New(), "Status")
        {
            WireId = 7,
            Description = "the one that exercises everything",
            Options = { DefaultAlignmentBits = 16 },
        };
        message.Routes.Add(new MessageRoute(sensor.Id, logger.Id));
        message.Fields.Add(new FieldBinding("header", header.Id));
        message.Fields.Add(counter);
        message.Fields.Add(new FieldBinding("samples", samples.Id));
        message.Fields.Add(new FieldBinding("trailer", trailer.Id));
        message.Fields.Add(new FieldBinding("packed", mode.Id, new FieldEncoding
        {
            BitWidth = 4,
            AllowBitPacking = true,
            BitOrder = BitOrder.LsbFirst,
            AlignmentBits = 8,
            Transform = new ScalarTransform(100, 1),
        })
        {
            DefaultValue = 100m,
            Description = "a field with every encoding knob set",
            ProtoFieldNumber = 3,
        });

        bus.Messages.Add(message);
        bus.Messages.Add(new Message(MessageId.New(), "Empty"));   // no fields, no routes, no wire id
        project.Buses.Add(bus);
        return project;
    }

    /// <summary>Round-trips a project through a repository and hands back what came out.</summary>
    private static Project Through(IProjectRepository repository, string path, Project project)
    {
        repository.Save(project, path);
        return repository.Load(path);
    }

    /// <summary>
    /// Both backends, handed the same project, hand back the same project.
    /// </summary>
    /// <remarks>
    /// One <c>Rich()</c> instance is the baseline for both round trips, because a second call would mint
    /// fresh ids and the merge — which matches on identity — would report every entity as added. That is
    /// what this test did on its first run, and it was the test that was wrong.
    /// </remarks>
    [Fact]
    public void The_sql_backend_and_the_json_backend_return_the_same_project()
    {
        var original = Rich();
        var viaJson = Through(new JsonProjectRepository(), _jsonPath, original);
        var viaSql = Through(_sql.Repository, "rich", original);

        var result = ProjectMerge.Merge(original, viaJson, viaSql);

        Assert.Empty(result.Conflicts);
        Assert.True(result.Applied.Count == 0, "The two backends disagree: " + Join(result.Applied));
    }

    /// <summary>
    /// And the SQL backend against the project itself, so a blind spot the two share is still caught.
    /// </summary>
    /// <remarks>
    /// Read it as: take what came back from storage as the ancestor and ask what the original still has
    /// that it does not. Anything the mapping dropped arrives as an incoming change. The local side is a
    /// second, independent load so the merge has its own graph to write into.
    /// </remarks>
    [Fact]
    public void Nothing_is_lost_on_the_way_through_sql()
    {
        var original = Rich();
        _sql.Repository.Save(original, "rich");

        var result = ProjectMerge.Merge(_sql.Repository.Load("rich"), _sql.Repository.Load("rich"), original);

        Assert.Empty(result.Conflicts);
        Assert.True(result.Applied.Count == 0, "The SQL mapping lost: " + Join(result.Applied));
    }

    private static string Join(IEnumerable<string> lines) => string.Join(" | ", lines);

    /// <summary>
    /// A synthetic enum's members are filled in from the bus at generation, so storing them is the one
    /// way the type could go stale — the reason it exists is to be un-stale-able.
    /// </summary>
    [Fact]
    public void A_synthetic_enums_members_are_not_stored()
    {
        var project = Rich();
        var synthetic = project.Types.All.OfType<EnumType>().Single(e => e.Synthetic != SyntheticEnum.None);
        synthetic.With("StaleMember", 1);   // as a careless caller might

        var loaded = Through(_sql.Repository, "rich", project);

        Assert.Empty(loaded.Types.All.OfType<EnumType>()
                           .Single(e => e.Synthetic != SyntheticEnum.None).Members);
    }

    /// <summary>One store holds many projects, which is the whole difference from a file.</summary>
    [Fact]
    public void Projects_in_one_store_do_not_see_each_other()
    {
        var first = Rich();
        var second = Rich();
        second.Name = "Other";
        second.Buses[0].Messages.RemoveAt(1);

        _sql.Repository.Save(first, "one");
        _sql.Repository.Save(second, "two");

        Assert.Equal("Telemetry", _sql.Repository.Load("one").Name);
        Assert.Equal(2, _sql.Repository.Load("one").Buses[0].Messages.Count);
        Assert.Equal("Other", _sql.Repository.Load("two").Name);
        Assert.Single(_sql.Repository.Load("two").Buses[0].Messages);
    }
}
