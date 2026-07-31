using System.Windows;
using System.Windows.Controls;
using ProtoDesigner.Wpf.Behaviors;
using ProtoDesigner.Wpf.ViewModels;

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
