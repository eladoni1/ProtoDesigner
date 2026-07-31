using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application.Commands;

/// <summary>
/// An undoable mutation of the model. Every edit made by the UI goes through the journal — that gives
/// undo/redo, dirty tracking, an audit trail, and the operation stream Phase 6 collaboration ships.
/// </summary>
public interface IEditCommand
{
    /// <summary>Human-readable label shown in the undo menu.</summary>
    string Describe();

    void Apply(Project project);

    void Undo(Project project);
}
