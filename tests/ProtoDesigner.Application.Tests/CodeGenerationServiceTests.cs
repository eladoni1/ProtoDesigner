using ProtoDesigner.CodeGen;

namespace ProtoDesigner.Application.Tests;

/// <summary>
/// The generate use case the CLI and the editor both call. The refusal rule is the one worth pinning
/// down: a model with an error must produce diagnostics and no files, because output from a protocol
/// that does not validate is worse than no output — it compiles, ships, and is wrong on the wire.
/// </summary>
public class CodeGenerationServiceTests
{
    private static readonly IProtocolGenerator CTarget = GeneratorCatalog.Find("c")!;

    /// <summary>A minimal valid project: one bus, one message, one field.</summary>
    private static (Project Project, Bus Bus) Valid()
    {
        var project = new Project("Sample");
        // 32-bit so every catalog target can express it, including protobuf.
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Ping") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "counter", u32.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        return (project, bus);
    }

    [Fact]
    public void A_valid_project_generates_files()
    {
        var (project, bus) = Valid();

        var result = CodeGenerationService.Generate(
            project, CTarget, GenerationScopes.ForBus(bus), new GeneratorOptions(Namespace: "proto"));

        Assert.False(result.Refused);
        Assert.Empty(result.BlockingErrors);
        Assert.NotEmpty(result.Files.Files);
        Assert.Equal(1, result.MessageCount);
    }

    [Fact]
    public void A_project_with_an_error_is_refused_and_produces_nothing()
    {
        var (project, bus) = Valid();

        // Two messages sharing a wire id on one bus: a receiver cannot tell them apart, so it is an error
        // rather than a warning.
        var clash = new Message(MessageId.New(), "Pong") { WireId = 1 };
        clash.Fields.Add(new FieldBinding(FieldId.New(), "counter", project.Types.All.First().Id));
        bus.Messages.Add(clash);

        var result = CodeGenerationService.Generate(
            project, CTarget, GenerationScopes.ForBus(bus), new GeneratorOptions(Namespace: "proto"));

        Assert.True(result.Refused);
        Assert.NotEmpty(result.BlockingErrors);
        Assert.Empty(result.Files.Files);
        Assert.Equal(0, result.MessageCount);
    }

    [Fact]
    public void An_empty_scope_generates_nothing_but_is_not_a_refusal()
    {
        // Nothing is wrong with the project — the selection just covers no messages. Reporting that as a
        // validation failure would send the user hunting for a bug that is not there.
        var (project, _) = Valid();

        var result = CodeGenerationService.Generate(
            project, CTarget, Array.Empty<GenerationScope>(), new GeneratorOptions());

        Assert.False(result.Refused);
        Assert.Empty(result.BlockingErrors);
        Assert.Empty(result.Files.Files);
    }

    [Fact]
    public void Generating_does_not_touch_the_disk()
    {
        // The editor previews before writing anything, which only works if producing the files and
        // writing them are genuinely separate steps.
        var (project, bus) = Valid();
        var before = Directory.GetCurrentDirectory();
        var snapshot = Directory.GetFiles(before).Length;

        var result = CodeGenerationService.Generate(
            project, CTarget, GenerationScopes.ForBus(bus), new GeneratorOptions());

        Assert.NotEmpty(result.Files.Files);
        Assert.Equal(snapshot, Directory.GetFiles(before).Length);
    }

    [Fact]
    public void Writing_puts_every_file_under_the_output_directory()
    {
        var (project, bus) = Valid();
        var result = CodeGenerationService.Generate(
            project, CTarget, GenerationScopes.ForBus(bus), new GeneratorOptions(Namespace: "proto"));

        var outDir = Path.Combine(Path.GetTempPath(), "protodesigner-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var written = CodeGenerationService.Write(result.Files, outDir);

            Assert.Equal(result.Files.Files.Count, written.Count);
            foreach (var path in written)
            {
                Assert.True(File.Exists(path), $"expected {path} to exist");
                Assert.StartsWith(outDir, path, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Writing_creates_the_output_directory_when_it_is_missing()
    {
        var (project, bus) = Valid();
        var result = CodeGenerationService.Generate(
            project, CTarget, GenerationScopes.ForBus(bus), new GeneratorOptions());

        var outDir = Path.Combine(Path.GetTempPath(), "protodesigner-tests",
            Guid.NewGuid().ToString("N"), "nested", "deeper");
        try
        {
            var written = CodeGenerationService.Write(result.Files, outDir);
            Assert.NotEmpty(written);
            Assert.True(Directory.Exists(outDir));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Every_catalog_target_generates_from_the_same_project()
    {
        // The point of the catalog is that a second language is one entry, not a second pipeline. If a
        // target cannot run the shared use case, it is not really behind the interface.
        var (project, bus) = Valid();

        foreach (var generator in GeneratorCatalog.All)
        {
            var result = CodeGenerationService.Generate(
                project, generator, GenerationScopes.ForBus(bus), new GeneratorOptions(Namespace: "Proto"));

            Assert.False(result.Refused);
            Assert.NotEmpty(result.Files.Files);
        }
    }
}
