using ProtoDesigner.Application;

namespace ProtoDesigner.Persistence.Contract;

/// <summary>
/// What every <see cref="IProjectRepository"/> must do, whatever it stores into.
/// </summary>
/// <remarks>
/// <para>
/// Phase 6 adds a second implementation, and the point of a port is that callers cannot tell which one
/// they have. These are the promises callers already rely on — written down now, while there is one
/// implementation to check them against, rather than discovered when the second one behaves differently.
/// </para>
/// <para>
/// The first clause is the one that matters and the one an implementer would not guess.
/// <see cref="WorkingCopy"/> loads the same project <em>twice</em>: once as the copy being edited and
/// once as the untouched baseline its next save merges against. A repository that returned a cached
/// instance would hand back the same object for both, so the baseline would mutate along with the edits
/// and every merge would compare a project against itself — finding no changes, taking nothing, and
/// reporting success. Nothing downstream would notice.
/// </para>
/// <para>
/// Each backend's suite derives from this and supplies a store. The concrete subclasses live beside the
/// implementation they check, so a red test names the backend rather than this file.
/// </para>
/// </remarks>
public abstract class ProjectRepositoryContract
{
    /// <summary>A repository to test, and a location in it that nothing has been stored at yet.</summary>
    protected abstract (IProjectRepository Repository, string Path) NewStore();

    private static readonly TypeId U8 = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
    private static readonly BusId TheBus = new(Guid.Parse("b0000000-0000-0000-0000-000000000001"));
    private static readonly MessageId Status = new(Guid.Parse("d0000000-0000-0000-0000-000000000001"));
    private static readonly FieldId Mode = new(Guid.Parse("f0000000-0000-0000-0000-000000000001"));

    private static Project Build(string name = "Telemetry")
    {
        var project = new Project(name);
        project.Types.Add(new ParameterType(U8, "u8", PrimitiveKind.U8));

        var message = new Message(Status, "Status") { WireId = 1 };
        message.Fields.Add(new FieldBinding(Mode, "mode", U8));

        var bus = new Bus(TheBus, "Main", Transport.Ethernet);
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return project;
    }

    /// <summary>Identity and order — the two things every other part of the system keys on.</summary>
    private static string Fingerprint(Project p) => string.Join("|",
        p.Name,
        string.Join(",", p.Types.All.Select(t => t.Id.Value).OrderBy(g => g)),
        string.Join(",", p.Buses.SelectMany(b => b.Messages)
                          .SelectMany(m => m.Fields.Select(f => $"{m.Id.Value}.{f.Id.Value}"))));

    [Fact]
    public void Loading_twice_gives_two_independent_projects()
    {
        var (repo, path) = NewStore();
        repo.Save(Build(), path);

        var first = repo.Load(path);
        var second = repo.Load(path);

        first.Buses[0].Messages[0].Name = "Edited";

        Assert.NotSame(first, second);
        Assert.Equal("Status", second.Buses[0].Messages[0].Name);
    }

    [Fact]
    public void Saving_then_loading_preserves_the_project()
    {
        var (repo, path) = NewStore();
        var saved = Build();

        repo.Save(saved, path);

        Assert.Equal(Fingerprint(saved), Fingerprint(repo.Load(path)));
    }

    [Fact]
    public void Saving_over_a_stored_project_replaces_it()
    {
        var (repo, path) = NewStore();
        repo.Save(Build(), path);

        var replacement = Build("Replaced");
        replacement.Buses[0].Messages.RemoveAt(0);
        repo.Save(replacement, path);

        var loaded = repo.Load(path);
        Assert.Equal("Replaced", loaded.Name);
        Assert.Empty(loaded.Buses[0].Messages);
    }

    /// <summary>
    /// Nothing stored means it fails, never an empty project.
    /// </summary>
    /// <remarks>
    /// An empty project is a valid one, so inventing it loses the difference between "not there" and
    /// "there and empty". A merging save would read it as a baseline in which every message had been
    /// deleted, and take that as the other author's work.
    /// </remarks>
    [Fact]
    public void Loading_something_never_stored_fails_rather_than_inventing_one()
    {
        var (repo, path) = NewStore();

        Assert.ThrowsAny<Exception>(() => repo.Load(path));
    }
}
