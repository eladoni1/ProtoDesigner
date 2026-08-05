using System.Text.RegularExpressions;
using ProtoDesigner.CodeGen.Cpp;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// The generated C++ must run on a bare target: no heap, no exceptions, no C++ standard library
/// containers.
/// </summary>
/// <remarks>
/// This is a product constraint, not a style preference. The code is meant for microcontrollers where
/// there may be no allocator at all, and where an allocation on the decode path is a latency spike or a
/// hard fault rather than a slow function. Encoding it as a test is what stops a future generator change
/// reaching for <c>std::vector</c> because it was convenient.
/// </remarks>
public class CppFreestandingTests
{
    private static readonly CppGenerator Generator = new();

    /// <summary>
    /// Constructs that pull in an allocator, unwinding, or the standard library. Word boundaries keep
    /// <c>new</c> from matching a member called <c>newValue</c>, and <c>free</c> from matching
    /// <c>free_slots</c>.
    /// </summary>
    private static readonly (string Pattern, string Why)[] Banned =
    {
        (@"\bnew\b",           "operator new allocates"),
        (@"\bdelete\b",        "operator delete implies allocation"),
        (@"\bmalloc\b",        "malloc allocates"),
        (@"\bcalloc\b",        "calloc allocates"),
        (@"\brealloc\b",       "realloc allocates"),
        (@"\bfree\s*\(",       "free implies allocation"),
        (@"\bthrow\b",         "exceptions are disabled on the target"),
        (@"\bstd::vector\b",   "std::vector allocates"),
        (@"\bstd::string\b",   "std::string allocates"),
        (@"\bstd::function\b", "std::function may allocate"),
        (@"#include <vector>", "pulls in an allocating container"),
        (@"#include <string>", "pulls in an allocating container"),
    };

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Corpus.All()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void The_generated_cpp_allocates_nothing(string corpusName)
    {
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();   // one call — project and bus must come from the same instance
        var ir = new IrBuilder().Build(project, bus);

        var set = Generator.Generate(ir, new GeneratorOptions(Namespace: "proto"));

        foreach (var file in set.Files.Where(f => f.RelativePath.EndsWith(".h", StringComparison.Ordinal)))
            AssertFreestanding(file.RelativePath, file.Contents);
    }

    [Fact]
    public void The_runtime_header_allocates_nothing()
    {
        // The runtime is the one file every generated protocol includes, so it carries the constraint for
        // all of them.
        var ir = Corpus.BuildIr(Corpus.Scalars());
        var set = Generator.Generate(ir, new GeneratorOptions());
        var runtime = set.Files.Single(f => f.RelativePath == "protodesigner_runtime.h");

        AssertFreestanding(runtime.RelativePath, runtime.Contents);
    }

    /// <summary>
    /// Checks the code, ignoring comments — a comment is free to use the word "new" in prose, and the
    /// generated headers explain themselves at length.
    /// </summary>
    private static void AssertFreestanding(string path, string contents)
    {
        var code = string.Join("\n", contents
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));

        foreach (var (pattern, why) in Banned)
        {
            var match = Regex.Match(code, pattern);
            Assert.False(match.Success,
                $"{path} uses '{match.Value}' — {why}. The generated C++ must run on a target with no heap.");
        }
    }
}
