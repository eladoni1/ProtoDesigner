namespace ProtoDesigner.Core.Merge;

/// <summary>
/// Stable codes for the places a three-way merge has to stop and ask.
/// </summary>
/// <remarks>
/// Same contract as <see cref="Validation.DiagnosticCodes"/> — never renumbered, never repurposed — for
/// the same reason: they are what a UI switches on and what a test asserts. They live in their own range
/// because a merge conflict is not a validation finding: it is about two projects, and there is no single
/// model a rule could have run against.
/// </remarks>
public static class MergeCodes
{
    public const string TypeChangedOnBothSides      = "PD0090";
    public const string BusChangedOnBothSides       = "PD0091";
    public const string ModuleChangedOnBothSides    = "PD0092";
    public const string MessageChangedOnBothSides   = "PD0093";
    public const string FieldChangedOnBothSides     = "PD0094";

    /// <summary>
    /// A message's field list was added to, removed from or reordered on both sides.
    /// </summary>
    /// <remarks>
    /// Its own code because it is the one conflict a merge could technically resolve and must not. Field
    /// order is wire order: appending one field on each side gives a layout neither person designed, and
    /// every other check downstream would call it correct.
    /// </remarks>
    public const string FieldOrderChangedOnBothSides = "PD0095";
}
