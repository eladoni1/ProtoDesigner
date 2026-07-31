using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application.Commands;

/// <summary>
/// The single mutation bus for a Project. Applies commands, tracks the dirty flag, and supports linear
/// undo/redo. Doing a new command clears the redo stack — the standard model.
/// </summary>
public sealed class CommandJournal
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();

    public CommandJournal(Project project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
    }

    public Project Project { get; }

    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public IEnumerable<string> UndoStack => _undo.Select(c => c.Describe());
    public IEnumerable<string> RedoStack => _redo.Select(c => c.Describe());

    public event EventHandler? Changed;

    public void Do(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply(Project);
        _undo.Push(command);
        _redo.Clear();
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var command = _undo.Pop();
        command.Undo(Project);
        _redo.Push(command);
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var command = _redo.Pop();
        command.Apply(Project);
        _undo.Push(command);
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Flags unsaved changes for an edit made outside the command stack. Used by bulk operations such as
    /// propagating a type's wire encoding, which touch many bindings at once and are re-derivable from
    /// the type rather than individually undoable.
    /// </summary>
    public void MarkDirty()
    {
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called after a successful save. Clears dirty; keeps undo history intact.</summary>
    public void MarkSaved()
    {
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
