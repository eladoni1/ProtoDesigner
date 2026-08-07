using System.Windows;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.ViewModels;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>
/// Opens the editor that matches a type's kind, from wherever the user reached the type.
/// </summary>
/// <remarks>
/// Extracted from <see cref="ProjectTreeView"/> once a field row could ask for the same thing. The
/// dispatch is small but it is the kind of small that drifts: a fifth type kind added in one copy and not
/// the other would leave a type editable from the library and inert from a field, with nothing failing to
/// say so.
/// </remarks>
internal static class TypeEditors
{
    /// <summary>Opens the right dialog for <paramref name="type"/>. Returns false for a kind with no editor.</summary>
    public static bool Edit(Window? owner, ProjectViewModel project, TypeDefinition type)
    {
        switch (type)
        {
            case ParameterType p: PrimitiveEditorDialog.Edit(owner, project, p); break;
            case EnumType e:      EnumEditorDialog.Edit(owner, project, e); break;
            case StructType s:    StructEditorDialog.Edit(owner, project, s); break;
            case ArrayType a:     ArrayEditorDialog.Edit(owner, project, a); break;
            default: return false;
        }

        project.RefreshAll();
        return true;
    }

    /// <summary>Renames a type in place. Every reference is by id, so nothing else has to change.</summary>
    public static void Rename(Window? owner, ProjectViewModel project, TypeDefinition type)
    {
        var item = project.Types.All.FirstOrDefault(t => t.Type.Id == type.Id);
        if (item is null) return;

        var name = TextPromptDialog.Ask(owner, "Rename type", "Type name", item.Name,
            "References are by identity, so renaming never breaks a field that uses this type.");
        if (name is null) return;

        item.Name = name;
        project.RefreshAll();
    }

    /// <summary>Makes <paramref name="type"/> the Types panel's selection, so Add field will use it.</summary>
    public static void Select(ProjectViewModel project, TypeDefinition type) =>
        project.SelectedType = project.Types.All.FirstOrDefault(t => t.Type.Id == type.Id);
}
