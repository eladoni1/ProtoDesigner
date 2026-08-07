using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Behaviors;
using ProtoDesigner.Wpf.ViewModels;
using ProtoDesigner.Wpf.Views.Dialogs;

namespace ProtoDesigner.Wpf.Views;

public partial class MessageEditorView : UserControl
{
    public MessageEditorView() => InitializeComponent();

    private ProjectViewModel? Project => DataContext as ProjectViewModel;
    private MessageViewModel? Selected => Project?.SelectedMessage;

    private void OnAddField(object sender, RoutedEventArgs e)
    {
        var project = Project;
        var message = project?.SelectedMessage;
        if (project is null || message is null) return;

        // The type comes from the Types panel selection, so the user chooses what they are adding.
        var type = project.SelectedType?.Type;
        if (type is null)
        {
            MessageBox.Show(
                "Select a type in the Types tab first, then press Add field.",
                "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        message.AddField(type);
    }

    private void OnRemoveField(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FieldViewModel field })
            Selected?.RemoveField(field);
    }

    // ---- editing a field's type -------------------------------------------------------------------

    /// <summary>The type a context-menu click was raised against, resolved through the row's Tag.</summary>
    /// <remarks>
    /// A ContextMenu lives outside the visual tree, so walking up from the MenuItem to the menu and reading
    /// its PlacementTarget is the reliable route back to the bound row — the same approach the project tree
    /// uses for buses and messages.
    /// </remarks>
    private static FieldViewModel? MenuField(object? sender)
    {
        if (sender is not MenuItem item) return null;

        DependencyObject? current = item;
        while (current is not null)
        {
            if (current is ContextMenu menu)
                return menu.PlacementTarget is FrameworkElement { Tag: FieldViewModel field } ? field : null;

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private TypeDefinition? TypeOf(FieldViewModel? field) =>
        field is not null && Project is { } p && p.Project.Types.TryGet(field.Field.TypeId, out var type)
            ? type
            : null;

    private void OnEditFieldType(object sender, RoutedEventArgs e) => EditType(TypeOf(MenuField(sender)));

    private void OnRenameFieldType(object sender, RoutedEventArgs e)
    {
        if (Project is { } project && TypeOf(MenuField(sender)) is { } type)
            TypeEditors.Rename(Window.GetWindow(this), project, type);
    }

    private void OnSelectFieldType(object sender, RoutedEventArgs e)
    {
        if (Project is { } project && TypeOf(MenuField(sender)) is { } type)
            TypeEditors.Select(project, type);
    }

    private void OnRemoveFieldFromMenu(object sender, RoutedEventArgs e)
    {
        if (MenuField(sender) is { } field) Selected?.RemoveField(field);
    }

    /// <summary>Double-clicking the type name edits it, the same as double-clicking it in the Types list.</summary>
    private void OnTypeNameClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is not FrameworkElement { Tag: FieldViewModel field }) return;

        e.Handled = true;
        EditType(TypeOf(field));
    }

    private void EditType(TypeDefinition? type)
    {
        if (Project is not { } project || type is null) return;

        if (!TypeEditors.Edit(Window.GetWindow(this), project, type))
        {
            MessageBox.Show($"'{type.Name}' has no editor.",
                "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ---- routes ----------------------------------------------------------------------------------

    private void OnAddRoute(object sender, RoutedEventArgs e)
    {
        var message = Selected;
        if (message is null) return;

        if (!message.CanAddRoute)
        {
            MessageBox.Show(
                "This bus has no modules yet. Right-click the bus and choose Edit to add them, " +
                "then come back and declare who sends this message to whom.",
                "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        message.AddRoute();
    }

    private void OnRemoveRoute(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RouteViewModel route })
            Selected?.RemoveRoute(route);
    }

    /// <summary>
    /// Handles both the legality probe raised while dragging and the committed drop. Order in this list
    /// is wire order, so the move goes through the view model and therefore the command journal.
    /// </summary>
    private void OnReorderRequested(object? sender, ReorderRequestedEventArgs e)
    {
        var message = Selected;
        if (message is null || e.Item is not FieldViewModel field) return;

        if (e.IsProbe)
        {
            e.Rejected = !message.CanMoveField(field, e.NewIndex);
            return;
        }

        if (!message.CanMoveField(field, e.NewIndex))
        {
            MessageBox.Show(
                "That order isn't possible: a dynamic array reads its length from a count field, " +
                "and the count must be laid out before the array.",
                "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        message.MoveField(field, e.NewIndex);
    }
}
