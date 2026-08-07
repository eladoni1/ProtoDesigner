namespace ProtoDesigner.Core.Model;

/// <summary>
/// The members a <see cref="SyntheticEnum"/> takes from a bus.
/// </summary>
/// <remarks>
/// <para>
/// One definition, two callers: <c>IrBuilder</c> fills the IR with it at generation, and the editor shows
/// the same list so a user can see what a message-id field will actually contain. A second copy in the UI
/// would be a second thing to keep in step, and the first time they disagreed the editor would be lying.
/// </para>
/// <para>
/// Derived on every call and never stored — that is the whole point of the type. Renaming a message or a
/// module changes the answer and leaves nothing behind to go stale.
/// </para>
/// </remarks>
public static class SyntheticEnumMembers
{
    /// <summary>Zero is reserved on both, and no real entry can hold it.</summary>
    public const string NotAssigned = "NotAssigned";

    public static IReadOnlyList<EnumMember> For(Bus bus, SyntheticEnum kind)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var members = new List<EnumMember> { new(NotAssigned, 0) };

        switch (kind)
        {
            case SyntheticEnum.MessageId:
                // The declared WireId, which the user chose and a deployed peer reads off the wire.
                members.AddRange(bus.Messages
                    .Where(m => m.WireId is > 0)
                    .OrderBy(m => m.WireId!.Value)
                    .Select(m => new EnumMember(m.Name, m.WireId!.Value)));
                break;

            case SyntheticEnum.ModuleId:
                // Declaration order: nothing puts a module id in a frame, so there is nothing to preserve
                // across a reorder.
                members.AddRange(bus.Modules.Select((m, i) => new EnumMember(m.Name, i + 1)));
                break;

            default:
                return [];
        }

        return members;
    }
}
