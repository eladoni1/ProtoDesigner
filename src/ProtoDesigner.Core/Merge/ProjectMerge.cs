using ProtoDesigner.Core.Model;
using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Core.Merge;

/// <summary>One place two people's edits cannot both be taken.</summary>
public sealed record MergeConflict(string Code, string Description, EntityPath Target)
{
    public override string ToString() => $"{Code}: {Description} [{Target}]";
}

/// <summary>What a merge would do, and what stopped it.</summary>
/// <param name="Applied">
/// One line per change taken from the remote side, for a caller that wants to show what moved. Empty when
/// the merge conflicted, because nothing was applied.
/// </param>
/// <param name="Validation">
/// <see cref="Severity.Error"/> diagnostics on the merged result. A merge can be perfectly clean and still
/// produce an invalid project — two people adding a message with the same wire id conflict nowhere,
/// because they touched different entities, and the result is a bus that no receiver can decode.
/// </param>
public sealed record MergeResult(
    IReadOnlyList<MergeConflict> Conflicts,
    IReadOnlyList<string> Applied,
    IReadOnlyList<Diagnostic> Validation)
{
    /// <summary>True when the merge was applied and the result is valid.</summary>
    public bool Succeeded => Conflicts.Count == 0 && Validation.Count == 0;
}

/// <summary>
/// Three-way merge of two people's edits to one project, entity by entity.
/// </summary>
/// <remarks>
/// <para>
/// This is the thing git structurally cannot do for this format. Two branches that each add a different
/// message to the same bus conflict under a line-based merge, because both insertions land at the same
/// textual anchor — the merge is semantically trivial (take both) and a text diff has no way to know it.
/// Matching on ids instead makes that case the common one it actually is.
/// </para>
/// <para>
/// It is cheap here only because of two rules already in place. <b>Identity is an id</b>, so a rename is
/// an ordinary property change rather than a delete plus an add, and two people renaming different things
/// never meet. <b>Layout is computed</b>, so there are no stored offsets to reconcile — moving a field
/// changes one list, not every entity after it.
/// </para>
/// <para>
/// <b>Nothing is applied unless everything can be.</b> The whole merge is planned first and only then
/// written into <c>local</c>, because a half-applied merge leaves the working copy in a state
/// neither person chose and no undo describes.
/// </para>
/// </remarks>
public static class ProjectMerge
{
    /// <summary>
    /// Merges <paramref name="remote"/>'s changes into <paramref name="local"/>, relative to the common
    /// ancestor <paramref name="baseline"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="local"/> is modified in place when the merge is clean, and left untouched when it
    /// is not. The other two are only read.
    /// </remarks>
    public static MergeResult Merge(Project baseline, Project local, Project remote)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        var conflicts = new List<MergeConflict>();
        var applied = new List<string>();
        var plan = new List<Action>();

        MergeTypes(baseline, local, remote, conflicts, applied, plan);
        MergeBuses(baseline, local, remote, conflicts, applied, plan);

        if (conflicts.Count > 0)
            return new MergeResult(conflicts, Array.Empty<string>(), Array.Empty<Diagnostic>());

        foreach (var step in plan) step();

        // A clean merge is not the same as a valid one, and this is where that bites: two people adding a
        // message each, with the same wire id, touch different entities and conflict nowhere.
        var errors = new Validator().Validate(local)
            .Where(d => d.Severity == Severity.Error)
            .ToList();

        return new MergeResult(conflicts, applied, errors);
    }

    // ---- types -------------------------------------------------------------------------------------

    private static void MergeTypes(Project baseline, Project local, Project remote,
        List<MergeConflict> conflicts, List<string> applied, List<Action> plan)
    {
        var baseById = baseline.Types.All.ToDictionary(t => t.Id);
        var localById = local.Types.All.ToDictionary(t => t.Id);
        var remoteById = remote.Types.All.ToDictionary(t => t.Id);

        foreach (var id in baseById.Keys.Union(localById.Keys).Union(remoteById.Keys))
        {
            baseById.TryGetValue(id, out var b);
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            var target = EntityPath.ForType(r ?? l ?? b!);

            switch (Classify(State(b), State(l), State(r)))
            {
                case Decision.Keep:
                    break;

                case Decision.TakeRemote when r is null:
                    var removed = l!;
                    plan.Add(() => local.Types.Remove(removed.Id));
                    applied.Add($"removed type '{removed.Name}'");
                    break;

                case Decision.TakeRemote:
                    // Replace rather than mutate: a type's shape varies by kind, and swapping the whole
                    // definition keeps that a single decision instead of four copy routines.
                    var incoming = r;
                    plan.Add(() =>
                    {
                        local.Types.Remove(incoming.Id);
                        local.Types.Add(incoming);
                    });
                    applied.Add(l is null ? $"added type '{r.Name}'" : $"updated type '{r.Name}'");
                    break;

                case Decision.Conflict:
                    conflicts.Add(new MergeConflict(MergeCodes.TypeChangedOnBothSides,
                        Describe("Type", b, l, r), target));
                    break;
            }
        }
    }

    // ---- buses, messages, fields -------------------------------------------------------------------

    private static void MergeBuses(Project baseline, Project local, Project remote,
        List<MergeConflict> conflicts, List<string> applied, List<Action> plan)
    {
        var baseById = baseline.Buses.ToDictionary(x => x.Id);
        var localById = local.Buses.ToDictionary(x => x.Id);
        var remoteById = remote.Buses.ToDictionary(x => x.Id);

        foreach (var id in baseById.Keys.Union(localById.Keys).Union(remoteById.Keys))
        {
            baseById.TryGetValue(id, out var b);
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            var named = r ?? l ?? b!;
            var target = EntityPath.ForBus(named);

            switch (Classify(State(b), State(l), State(r)))
            {
                case Decision.TakeRemote when r is null:
                    var removed = l!;
                    plan.Add(() => local.Buses.Remove(removed));
                    applied.Add($"removed bus '{removed.Name}'");
                    continue;

                case Decision.TakeRemote when l is null:
                    var addedBus = r;
                    plan.Add(() => local.Buses.Add(addedBus));
                    applied.Add($"added bus '{r.Name}'");
                    continue;

                case Decision.TakeRemote:
                    var into = l!;
                    var from = r!;
                    plan.Add(() =>
                    {
                        into.Name = from.Name;
                        into.Transport = from.Transport;
                        CopyOptions(from.Options, into.Options);
                    });
                    applied.Add($"updated bus '{r!.Name}'");
                    break;

                case Decision.Conflict:
                    conflicts.Add(new MergeConflict(MergeCodes.BusChangedOnBothSides,
                        Describe("Bus", b, l, r), target));
                    continue;
            }

            // Only worth descending when the bus survives on both sides.
            if (l is null || r is null) continue;

            MergeModules(b, l, r, conflicts, applied, plan);
            MergeMessages(b, l, r, conflicts, applied, plan);
        }
    }

    private static void MergeModules(Bus? baseline, Bus local, Bus remote,
        List<MergeConflict> conflicts, List<string> applied, List<Action> plan)
    {
        var baseById = baseline?.Modules.ToDictionary(x => x.Id) ?? new Dictionary<ModuleId, Module>();
        var localById = local.Modules.ToDictionary(x => x.Id);
        var remoteById = remote.Modules.ToDictionary(x => x.Id);

        foreach (var id in baseById.Keys.Union(localById.Keys).Union(remoteById.Keys))
        {
            baseById.TryGetValue(id, out var b);
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            switch (Classify(State(b), State(l), State(r)))
            {
                case Decision.TakeRemote when r is null:
                    var removed = l!;
                    plan.Add(() => local.RemoveModule(removed.Id));
                    applied.Add($"removed module '{removed.Name}'");
                    break;

                case Decision.TakeRemote when l is null:
                    var added = r;
                    plan.Add(() => local.Modules.Add(added));
                    applied.Add($"added module '{r.Name}'");
                    break;

                case Decision.TakeRemote:
                    var into = l!;
                    var name = r!.Name;
                    plan.Add(() => into.Name = name);
                    applied.Add($"renamed module to '{name}'");
                    break;

                case Decision.Conflict:
                    conflicts.Add(new MergeConflict(MergeCodes.ModuleChangedOnBothSides,
                        Describe("Module", b, l, r), EntityPath.ForBus(local)));
                    break;
            }
        }
    }

    private static void MergeMessages(Bus? baseline, Bus local, Bus remote,
        List<MergeConflict> conflicts, List<string> applied, List<Action> plan)
    {
        var baseById = baseline?.Messages.ToDictionary(x => x.Id) ?? new Dictionary<MessageId, Message>();
        var localById = local.Messages.ToDictionary(x => x.Id);
        var remoteById = remote.Messages.ToDictionary(x => x.Id);

        foreach (var id in baseById.Keys.Union(localById.Keys).Union(remoteById.Keys))
        {
            baseById.TryGetValue(id, out var b);
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            var named = r ?? l ?? b!;
            var target = EntityPath.ForMessage(local, named);

            switch (Classify(State(b), State(l), State(r)))
            {
                case Decision.TakeRemote when r is null:
                    var removed = l!;
                    plan.Add(() => local.Messages.Remove(removed));
                    applied.Add($"removed message '{removed.Name}'");
                    continue;

                case Decision.TakeRemote when l is null:
                    var added = r;
                    plan.Add(() => local.Messages.Add(added));
                    applied.Add($"added message '{r.Name}'");
                    continue;

                case Decision.TakeRemote:
                    var into = l!;
                    var from = r!;
                    plan.Add(() =>
                    {
                        into.Name = from.Name;
                        into.WireId = from.WireId;
                        into.Description = from.Description;
                        CopyOptions(from.Options, into.Options);
                        into.Routes.Clear();
                        into.Routes.AddRange(from.Routes);
                    });
                    applied.Add($"updated message '{from.Name}'");
                    break;

                case Decision.Conflict:
                    conflicts.Add(new MergeConflict(MergeCodes.MessageChangedOnBothSides,
                        Describe("Message", b, l, r), target));
                    continue;
            }

            if (l is null || r is null) continue;

            MergeFields(b, l, r, local, conflicts, applied, plan);
        }
    }

    /// <summary>
    /// Merges one message's field list.
    /// </summary>
    /// <remarks>
    /// <b>Field order is wire order</b>, which makes this the one place a merge must refuse something it
    /// could technically do. If both sides changed the membership or the order of the list, both results
    /// are valid protocols and they are *different* protocols — appending one field each gives a layout
    /// neither person designed, and nothing downstream would report it. So a list touched on both sides
    /// is a conflict even when the two edits are disjoint. Property edits to individual fields merge
    /// normally, because those do not move anything.
    /// </remarks>
    private static void MergeFields(Message? baseline, Message local, Message remote, Bus bus,
        List<MergeConflict> conflicts, List<string> applied, List<Action> plan)
    {
        var baseOrder = Order(baseline);
        var localOrder = Order(local);
        var remoteOrder = Order(remote);

        var localMoved = baseline is not null && localOrder != baseOrder;
        var remoteMoved = baseline is not null && remoteOrder != baseOrder;

        if (localMoved && remoteMoved && localOrder != remoteOrder)
        {
            conflicts.Add(new MergeConflict(MergeCodes.FieldOrderChangedOnBothSides,
                $"Message '{local.Name}' had fields added, removed or reordered on both sides. Field order "
                + "is wire order, so both results are valid protocols and they are different ones — this "
                + "needs a person to say which.",
                EntityPath.ForMessage(bus, local)));
            return;
        }

        if (remoteMoved && localOrder == baseOrder)
        {
            // Only the remote side changed the shape: take its list wholesale, carrying any local property
            // edits on the fields that survive.
            var into = local;
            var order = remote.Fields.ToList();
            plan.Add(() =>
            {
                var keep = into.Fields.ToDictionary(f => f.Id);
                into.Fields.Clear();
                foreach (var f in order) into.Fields.Add(keep.TryGetValue(f.Id, out var mine) ? mine : f);
            });
            applied.Add($"reordered fields of '{local.Name}'");
        }

        var baseById = baseline?.Fields.ToDictionary(x => x.Id) ?? new Dictionary<FieldId, FieldBinding>();
        var localById = local.Fields.ToDictionary(x => x.Id);
        var remoteById = remote.Fields.ToDictionary(x => x.Id);

        foreach (var id in baseById.Keys.Union(localById.Keys).Union(remoteById.Keys))
        {
            baseById.TryGetValue(id, out var b);
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            // Membership is settled above; only a field present on both sides has properties to merge.
            if (l is null || r is null) continue;

            switch (Classify(State(b), State(l), State(r)))
            {
                case Decision.TakeRemote:
                    var into = l;
                    var from = r;
                    plan.Add(() =>
                    {
                        into.Name = from.Name;
                        into.TypeId = from.TypeId;
                        into.Description = from.Description;
                        into.DefaultValue = from.DefaultValue;
                        into.ProtoFieldNumber = from.ProtoFieldNumber;
                        into.Encoding = from.Encoding.Clone();
                    });
                    applied.Add($"updated field '{local.Name}.{from.Name}'");
                    break;

                case Decision.Conflict:
                    conflicts.Add(new MergeConflict(MergeCodes.FieldChangedOnBothSides,
                        Describe("Field", b, l, r),
                        EntityPath.ForField(bus, local, l.Name)));
                    break;
            }
        }
    }

    // ---- the three-way decision --------------------------------------------------------------------

    private enum Decision { Keep, TakeRemote, Conflict }

    /// <summary>
    /// The whole of three-way merging, for one entity, in four lines.
    /// </summary>
    /// <remarks>
    /// A null state means the entity is absent on that side, which folds add and remove into the same
    /// comparison rather than needing cases of their own. Two sides that made the <em>same</em> change are
    /// not a conflict: people reach the same edit independently more often than is comfortable, and
    /// stopping them would be noise.
    /// </remarks>
    private static Decision Classify(string? baseline, string? local, string? remote)
    {
        if (local == remote) return Decision.Keep;        // agreed, including both absent
        if (baseline == remote) return Decision.Keep;     // only the local side moved
        if (baseline == local) return Decision.TakeRemote; // only the remote side moved
        return Decision.Conflict;                          // both moved, differently
    }

    private static string? State(Message? m) => m is null ? null : EntityState.Of(m);
    private static string? State(FieldBinding? f) => f is null ? null : EntityState.Of(f);
    private static string? State(Bus? b) => b is null ? null : EntityState.Of(b);
    private static string? State(Module? m) => m is null ? null : EntityState.Of(m);
    private static string? State(TypeDefinition? t) => t is null ? null : EntityState.Of(t);

    /// <summary>The id list that decides whether a field list moved: membership and order together.</summary>
    private static string Order(Message? m) =>
        m is null ? "" : string.Join(",", m.Fields.Select(f => f.Id.Value));

    private static string Describe(string kind, object? baseline, object? local, object? remote)
    {
        if (local is null) return $"{kind} was removed locally and changed remotely.";
        if (remote is null) return $"{kind} was changed locally and removed remotely.";
        return baseline is null
            ? $"{kind} was added on both sides with the same id but different contents."
            : $"{kind} was changed on both sides.";
    }

    private static void CopyOptions(LayoutOptions from, LayoutOptions into)
    {
        into.Endianness = from.Endianness;
        into.BitOrder = from.BitOrder;
        into.DefaultAlignmentBits = from.DefaultAlignmentBits;
        into.PackingMode = from.PackingMode;
        into.PadToByteBoundary = from.PadToByteBoundary;
    }
}
