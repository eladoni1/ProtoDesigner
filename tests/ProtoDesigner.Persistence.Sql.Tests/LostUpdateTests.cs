using ProtoDesigner.Application;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Persistence.Sql.Tests;

/// <summary>
/// Work landing in the gap between a save's read and its write.
/// </summary>
/// <remarks>
/// <para>
/// The merging save closes the ordinary case: every <see cref="WorkingCopy.Save"/> re-reads storage
/// first, so two people who opened the same project and edited different messages both survive whichever
/// order they save in. That holds, and is tested elsewhere.
/// </para>
/// <para>
/// What it cannot close from where it sits is the window <em>inside</em> one save — between the read it
/// merges against and the write that follows. <see cref="A_write_landing_in_the_window_is_not_lost"/> was
/// written before the stamp existed and reproduced the loss: the interloper's whole message vanished and
/// both saves reported success. The stamp is what closes it.
/// </para>
/// <para>
/// The interloper always edits a <em>different</em> entity than we do. That is the case worth guarding:
/// two people touching the same message is an ordinary merge conflict, reported long before a stamp
/// could matter, so an interloper aimed at our own message would test the merge instead of the window.
/// </para>
/// </remarks>
public sealed class LostUpdateTests : IDisposable
{
    private readonly SqliteStore _store = new();

    public void Dispose() => _store.Dispose();

    private static readonly TypeId U8 = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
    private static readonly BusId TheBus = new(Guid.Parse("b0000000-0000-0000-0000-000000000001"));
    private static readonly MessageId Ours = new(Guid.Parse("d0000000-0000-0000-0000-000000000001"));

    private static Project Build()
    {
        var project = new Project("Telemetry");
        project.Types.Add(new ParameterType(U8, "u8", PrimitiveKind.U8));

        var message = new Message(Ours, "Status") { WireId = 1 };
        message.Fields.Add(new FieldBinding("mode", U8));

        var bus = new Bus(TheBus, "Main", Transport.Ethernet);
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return project;
    }

    /// <summary>Someone else adds a message of their own and saves it.</summary>
    private void SomeoneElseAddsAMessage(string key, int n)
    {
        var theirs = _store.Repository.Load(key);
        // Offset well clear of Ours (…0001). Colliding with it made the schema refuse the write with a
        // UNIQUE violation — correctly, and a duplicate id is something the file backend would have
        // written without complaint.
        var added = new Message(new MessageId(Guid.Parse($"d0000000-0000-0000-0000-0000000000{n + 0xb0:x2}")),
                                $"Theirs{n}") { WireId = 100 + n };
        added.Fields.Add(new FieldBinding("payload", U8));
        theirs.Buses[0].Messages.Add(added);
        _store.Repository.Save(theirs, key);
    }

    /// <summary>
    /// Writes on someone else's behalf the instant a read returns — which is when the window opens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It stays disarmed until <see cref="Arm"/>, because <see cref="WorkingCopy.Open"/> reads twice and
    /// an interloper firing then would land in the <em>baseline</em>. The save would merge it in as
    /// ordinary incoming work and the window would never be exercised — which is exactly what the first
    /// version of this test did, and why it looked like the fix had not worked.
    /// </para>
    /// <para>
    /// It implements <see cref="IStampedProjectRepository"/>, not just <see cref="IProjectRepository"/>.
    /// A decorator that implements only the narrow interface silently downgrades the store to the
    /// unguarded path — a real hazard for any wrapper, and the second way this test misled its author.
    /// </para>
    /// </remarks>
    private sealed class Interloping : IStampedProjectRepository
    {
        private readonly IStampedProjectRepository _inner;
        private readonly Action<int> _write;
        private readonly bool _everyRead;
        private bool _armed;
        private int _count;

        public Interloping(IStampedProjectRepository inner, Action<int> write, bool everyRead)
        {
            _inner = inner;
            _write = write;
            _everyRead = everyRead;
        }

        public void Arm() => _armed = true;

        public Project Load(string path) => Load(path, out _);

        public Project Load(string path, out string stamp)
        {
            var project = _inner.Load(path, out stamp);

            if (_armed)
            {
                if (!_everyRead) _armed = false;
                _write(++_count);
            }

            return project;
        }

        public void Save(Project project, string path) => _inner.Save(project, path);

        public bool SaveIfUnchanged(Project project, string path, string expectedStamp) =>
            _inner.SaveIfUnchanged(project, path, expectedStamp);
    }

    [Fact]
    public void A_write_landing_in_the_window_is_not_lost()
    {
        const string key = "shared";
        _store.Repository.Save(Build(), key);

        var racing = new Interloping(_store.Repository, n => SomeoneElseAddsAMessage(key, n), everyRead: false);
        var ours = WorkingCopy.Open(racing, key);
        ours.Project.Buses[0].Messages[0].Name = "EditedByUs";

        racing.Arm();   // the next read is the one the save merges against
        var outcome = ours.Save();

        var stored = _store.Repository.Load(key);

        // The stamp refuses the first write, so the save reads again and merges against what overtook
        // it. Both survive, and the retry is invisible beyond the status.
        Assert.True(outcome.Wrote);
        Assert.Equal(SaveStatus.Merged, outcome.Status);
        Assert.Equal("EditedByUs", stored.Buses[0].Messages.Single(m => m.Id == Ours).Name);
        Assert.Contains(stored.Buses[0].Messages, m => m.Name == "Theirs1");
    }

    /// <summary>
    /// A store that overtakes every attempt reports it rather than spinning or writing anyway.
    /// </summary>
    /// <remarks>
    /// Writing regardless would be the lost update this mechanism exists to stop; looping forever would
    /// hang the editor on a busy store. Saying so is the only honest third option.
    /// </remarks>
    [Fact]
    public void A_save_overtaken_every_time_gives_up_rather_than_writing_anyway()
    {
        const string key = "busy";
        _store.Repository.Save(Build(), key);

        var racing = new Interloping(_store.Repository, n => SomeoneElseAddsAMessage(key, n), everyRead: true);
        var ours = WorkingCopy.Open(racing, key);
        ours.Project.Buses[0].Messages[0].Name = "EditedByUs";

        racing.Arm();

        Assert.Throws<InvalidOperationException>(() => ours.Save());

        // Nothing of ours reached the store.
        Assert.Equal("Status", _store.Repository.Load(key).Buses[0].Messages.Single(m => m.Id == Ours).Name);
    }
}
