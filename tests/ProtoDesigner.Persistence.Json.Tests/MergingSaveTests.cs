using ProtoDesigner.Application;
using ProtoDesigner.Core.Merge;
using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Persistence.Json.Tests;

/// <summary>
/// Two people saving to one file, over real storage.
/// </summary>
/// <remarks>
/// <para>
/// These live in the persistence suite rather than beside the rest of the application layer because the
/// thing under test is the interaction: <see cref="WorkingCopy"/> is only correct if a project written to
/// disk and read back is <em>the same project</em> as far as the merge can tell. A fake repository that
/// handed back objects would prove the save logic and quietly assume the half most likely to be wrong.
/// </para>
/// <para>
/// <see cref="Opening_a_file_and_saving_it_straight_back_merges_nothing"/> is that assumption stated as a
/// test. If the serializer ever drops or reorders something the merge renders into an entity's state, the
/// first symptom in the field would be a save reporting phantom incoming changes from a file nobody
/// edited.
/// </para>
/// </remarks>
public class MergingSaveTests : IDisposable
{
    private static readonly TypeId U8 = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
    private static readonly BusId TheBus = new(Guid.Parse("b0000000-0000-0000-0000-000000000001"));
    private static readonly MessageId Status = new(Guid.Parse("d0000000-0000-0000-0000-000000000001"));
    private static readonly FieldId Mode = new(Guid.Parse("f0000000-0000-0000-0000-000000000001"));

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"protodesigner-merge-{Guid.NewGuid():N}.pdproj");

    private readonly JsonProjectRepository _repo = new();

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static Project Build()
    {
        var project = new Project("Telemetry");
        project.Types.Add(new ParameterType(U8, "u8", PrimitiveKind.U8));

        var message = new Message(Status, "Status") { WireId = 1 };
        message.Fields.Add(new FieldBinding(Mode, "mode", U8));

        var bus = new Bus(TheBus, "Main", Transport.Ethernet);
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return project;
    }

    /// <summary>The file as it stands, opened as a second person's editor would open it.</summary>
    private Project Stored() => _repo.Load(_path);

    private static Message MessageOf(Project p) => p.Buses[0].Messages[0];

    private static Message NewMessage(string name, int wireId, string id)
    {
        var m = new Message(new MessageId(Guid.Parse(id)), name) { WireId = wireId };
        m.Fields.Add(new FieldBinding(FieldId.New(), "payload", U8));
        return m;
    }

    private WorkingCopy OpenedOnAFileContaining(Project project)
    {
        _repo.Save(project, _path);
        return WorkingCopy.Open(_repo, _path);
    }

    // ---- the assumption everything else rests on ---------------------------------------------------

    [Fact]
    public void Opening_a_file_and_saving_it_straight_back_merges_nothing()
    {
        var copy = OpenedOnAFileContaining(Build());

        var outcome = copy.Save();

        Assert.Equal(SaveStatus.Written, outcome.Status);
        Assert.Empty(outcome.Merged);
    }

    // ---- the ordinary cases ------------------------------------------------------------------------

    [Fact]
    public void A_save_nobody_raced_writes_what_is_on_screen()
    {
        var copy = OpenedOnAFileContaining(Build());
        MessageOf(copy.Project).Name = "Renamed";

        var outcome = copy.Save();

        Assert.Equal(SaveStatus.Written, outcome.Status);
        Assert.Equal("Renamed", MessageOf(Stored()).Name);
    }

    /// <summary>
    /// The case a plain save destroys silently: two people, two different messages, one file.
    /// </summary>
    [Fact]
    public void Someone_elses_new_message_is_taken_in_rather_than_overwritten()
    {
        var copy = OpenedOnAFileContaining(Build());

        // They add a message and save while our copy is open.
        var theirs = Stored();
        theirs.Buses[0].Messages.Add(NewMessage("Bob", 11, "d0000000-0000-0000-0000-0000000000b1"));
        _repo.Save(theirs, _path);

        // We rename a different one and save.
        MessageOf(copy.Project).Name = "Renamed";
        var outcome = copy.Save();

        Assert.Equal(SaveStatus.Merged, outcome.Status);
        Assert.NotEmpty(outcome.Merged);

        // Both edits survive, on disk and in the working copy.
        Assert.Equal(new[] { "Renamed", "Bob" }, Stored().Buses[0].Messages.Select(m => m.Name));
        Assert.Equal(new[] { "Renamed", "Bob" }, copy.Project.Buses[0].Messages.Select(m => m.Name));
    }

    [Fact]
    public void A_conflicted_save_writes_nothing_and_changes_nothing()
    {
        var copy = OpenedOnAFileContaining(Build());

        var theirs = Stored();
        MessageOf(theirs).Name = "Theirs";
        _repo.Save(theirs, _path);

        MessageOf(copy.Project).Name = "Ours";
        var outcome = copy.Save();

        Assert.Equal(SaveStatus.Conflicted, outcome.Status);
        Assert.False(outcome.Wrote);
        Assert.Contains(outcome.Conflicts, c => c.Code == MergeCodes.MessageChangedOnBothSides);

        // Their file is intact and our screen is intact. Nobody lost anything to find out.
        Assert.Equal("Theirs", MessageOf(Stored()).Name);
        Assert.Equal("Ours", MessageOf(copy.Project).Name);
    }

    /// <summary>
    /// A conflict must not poison the copy: fix the thing it named, save again, and it goes through.
    /// </summary>
    [Fact]
    public void Resolving_the_conflict_and_saving_again_succeeds()
    {
        var copy = OpenedOnAFileContaining(Build());

        var theirs = Stored();
        MessageOf(theirs).Name = "Theirs";
        _repo.Save(theirs, _path);

        MessageOf(copy.Project).Name = "Ours";
        Assert.Equal(SaveStatus.Conflicted, copy.Save().Status);

        // Take their name — the ordinary way a person resolves this.
        MessageOf(copy.Project).Name = "Theirs";
        var outcome = copy.Save();

        Assert.Equal(SaveStatus.Written, outcome.Status);
        Assert.Equal("Theirs", MessageOf(Stored()).Name);
    }

    /// <summary>
    /// After a merged save, the baseline is what was just written — so editing something that arrived in
    /// that merge is an ordinary edit.
    /// </summary>
    /// <remarks>
    /// The edit has to be to the message that <em>came in</em>, which is the part worth spelling out. A
    /// baseline left behind still agrees with the stored copy about everything it already knew, so editing
    /// one of those saves cleanly either way and proves nothing. It is the incoming message that the stale
    /// baseline has never heard of: renaming it reads as "added on both sides with different contents",
    /// and the second save conflicts with the person whose change we just accepted.
    /// </remarks>
    [Fact]
    public void A_merged_save_moves_the_baseline_on()
    {
        var copy = OpenedOnAFileContaining(Build());

        var theirs = Stored();
        theirs.Buses[0].Messages.Add(NewMessage("Bob", 11, "d0000000-0000-0000-0000-0000000000b1"));
        _repo.Save(theirs, _path);

        Assert.Equal(SaveStatus.Merged, copy.Save().Status);

        copy.Project.Buses[0].Messages.Single(m => m.Name == "Bob").Name = "Robert";
        var second = copy.Save();

        Assert.Equal(SaveStatus.Written, second.Status);
        Assert.Empty(second.Merged);
        Assert.Contains(Stored().Buses[0].Messages, m => m.Name == "Robert");
    }

    /// <summary>
    /// A merge can be clean and still land two messages on one wire id — the two people touched different
    /// entities, so nothing conflicts. The save goes through and says so, rather than refusing: the
    /// editor's standing rule is that a duplicate wire id is reported, not refused.
    /// </summary>
    [Fact]
    public void A_clean_merge_that_collides_wire_ids_is_written_and_reported()
    {
        var copy = OpenedOnAFileContaining(Build());

        var theirs = Stored();
        theirs.Buses[0].Messages.Add(NewMessage("Bob", 10, "d0000000-0000-0000-0000-0000000000b1"));
        _repo.Save(theirs, _path);

        copy.Project.Buses[0].Messages.Add(NewMessage("Alice", 10, "d0000000-0000-0000-0000-0000000000a1"));
        var outcome = copy.Save();

        Assert.Equal(SaveStatus.Merged, outcome.Status);
        Assert.True(outcome.Wrote);
        Assert.Contains(outcome.Validation, d => d.Code == DiagnosticCodes.DuplicateWireId);
        Assert.Equal(3, Stored().Buses[0].Messages.Count);
    }

    // ---- first save, and save as -------------------------------------------------------------------

    [Fact]
    public void A_project_that_was_never_stored_just_writes()
    {
        var copy = WorkingCopy.Started(_repo, Build(), _path);

        var outcome = copy.Save();

        Assert.Equal(SaveStatus.Written, outcome.Status);
        Assert.Equal("Telemetry", Stored().Name);
    }

    [Fact]
    public void Save_as_forks_to_the_new_path_and_saves_there_from_then_on()
    {
        var copy = OpenedOnAFileContaining(Build());
        var forked = Path.Combine(Path.GetTempPath(), $"protodesigner-merge-{Guid.NewGuid():N}.pdproj");

        try
        {
            MessageOf(copy.Project).Name = "Forked";
            Assert.Equal(SaveStatus.Written, copy.SaveAs(forked).Status);

            Assert.Equal(forked, copy.Path);
            Assert.Equal("Forked", MessageOf(_repo.Load(forked)).Name);
            Assert.Equal("Status", MessageOf(Stored()).Name);   // the original is left alone

            // And the fork is now what ordinary saves merge against.
            MessageOf(copy.Project).Name = "Again";
            Assert.Equal(SaveStatus.Written, copy.Save().Status);
            Assert.Equal("Again", MessageOf(_repo.Load(forked)).Name);
        }
        finally
        {
            if (File.Exists(forked)) File.Delete(forked);
        }
    }
}
