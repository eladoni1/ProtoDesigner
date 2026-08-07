using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.ViewModels;
using ProtoDesigner.Wpf.Views.Dialogs;

namespace ProtoDesigner.Wpf.Views;

public partial class ProjectTreeView : UserControl
{
    public ProjectTreeView() => InitializeComponent();

    private ProjectViewModel? Vm => DataContext as ProjectViewModel;

    private Window? OwnerWindow => Window.GetWindow(this);

    // ---- selection ------------------------------------------------------------------------------

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is null) return;

        switch (e.NewValue)
        {
            case MessageViewModel message:
                Vm.SelectedMessage = message;
                break;
            case BusViewModel bus:
                Vm.SelectedBus = bus;
                break;
        }
    }

    /// <summary>Selecting a type is what "Add field" acts on, so a single click is enough to choose one.</summary>
    private void OnTypeClicked(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is not FrameworkElement { Tag: TypeItemViewModel item }) return;

        Vm.SelectedType = item;

        if (e.ChangedButton == MouseButton.Left && e.ClickCount >= 2)
            EditType(item);
    }

    // ---- buses ----------------------------------------------------------------------------------

    private void OnCreateBus(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        var bus = BusEditorDialog.CreateNew(OwnerWindow, Vm);
        if (bus is not null) Vm.SelectedBus = bus;
    }

    private void OnEditBus(object sender, RoutedEventArgs e)
    {
        var bus = ContextTarget<BusViewModel>(sender) ?? Vm?.SelectedBus;
        if (bus is null || Vm is null) return;

        if (BusEditorDialog.Edit(OwnerWindow, Vm, bus)) Vm.RefreshAll();
    }

    private void OnRenameBus(object sender, RoutedEventArgs e)
    {
        var bus = ContextTarget<BusViewModel>(sender) ?? Vm?.SelectedBus;
        if (bus is null) return;

        var name = TextPromptDialog.Ask(OwnerWindow, "Rename bus", "Bus name", bus.Name);
        if (name is not null) bus.Name = name;
    }

    private void OnDeleteBus(object sender, RoutedEventArgs e)
    {
        var bus = ContextTarget<BusViewModel>(sender) ?? Vm?.SelectedBus;
        if (bus is null || Vm is null) return;

        var count = bus.Messages.Count;
        var warning = count == 0
            ? $"Delete bus '{bus.Name}'?"
            : $"Delete bus '{bus.Name}' and its {count} message(s)?";

        if (MessageBox.Show(warning, "ProtoDesigner", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        if (ReferenceEquals(Vm.SelectedMessage?.Bus, bus)) Vm.SelectedMessage = null;
        Vm.RemoveBus(bus);
    }

    // ---- messages -------------------------------------------------------------------------------

    private void OnCreateMessage(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        var bus = ContextTarget<BusViewModel>(sender) ?? Vm.SelectedBus;
        if (bus is null)
        {
            MessageBox.Show(
                "Create a bus first — a message always belongs to one.",
                "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = TextPromptDialog.Ask(OwnerWindow, "Create message", "Message name",
            "NewMessage", $"Added to bus '{bus.Name}'.");
        if (name is null) return;

        var message = bus.AddMessage(name);
        Vm.SelectedMessage = message;
    }

    private void OnRenameMessage(object sender, RoutedEventArgs e)
    {
        var message = ContextTarget<MessageViewModel>(sender) ?? Vm?.SelectedMessage;
        if (message is null) return;

        var name = TextPromptDialog.Ask(OwnerWindow, "Rename message", "Message name", message.Name);
        if (name is not null) message.Name = name;
    }

    private void OnDeleteMessage(object sender, RoutedEventArgs e)
    {
        var message = ContextTarget<MessageViewModel>(sender) ?? Vm?.SelectedMessage;
        if (message is null || Vm is null) return;

        if (MessageBox.Show($"Delete message '{message.Name}'?", "ProtoDesigner",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        if (ReferenceEquals(Vm.SelectedMessage, message)) Vm.SelectedMessage = null;
        message.Bus.RemoveMessage(message);
    }

    // ---- types ----------------------------------------------------------------------------------

    private void OnCreatePrimitive(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var created = PrimitiveEditorDialog.CreateNew(OwnerWindow, Vm);
        if (created is not null) SelectType(created);
    }

    private void OnCreateEnum(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var created = EnumEditorDialog.CreateNew(OwnerWindow, Vm);
        if (created is not null) SelectType(created);
    }

    private void OnCreateStruct(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var created = StructEditorDialog.CreateNew(OwnerWindow, Vm);
        if (created is not null) SelectType(created);
    }

    private void OnCreateArray(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        if (!Vm.Project.Types.All.Any())
        {
            MessageBox.Show("Create a primitive first — an array needs an element type.",
                "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var created = ArrayEditorDialog.CreateNew(OwnerWindow, Vm);
        if (created is not null) SelectType(created);
    }

    private void OnEditType(object sender, RoutedEventArgs e)
    {
        var item = ContextTarget<TypeItemViewModel>(sender) ?? Vm?.SelectedType;
        if (item is not null) EditType(item);
    }

    private void OnRenameType(object sender, RoutedEventArgs e)
    {
        var item = ContextTarget<TypeItemViewModel>(sender) ?? Vm?.SelectedType;
        if (item is null) return;

        var name = TextPromptDialog.Ask(OwnerWindow, "Rename type", "Type name", item.Name,
            "References are by identity, so renaming never breaks a field that uses this type.");
        if (name is not null)
        {
            item.Name = name;
            Vm?.RefreshAll();
        }
    }

    private void OnDuplicateType(object sender, RoutedEventArgs e)
    {
        var item = ContextTarget<TypeItemViewModel>(sender) ?? Vm?.SelectedType;
        if (item is null || Vm is null) return;

        var copy = Vm.Types.Duplicate(item);
        if (copy is not null) SelectType(copy);
    }

    private void OnDeleteType(object sender, RoutedEventArgs e)
    {
        var item = ContextTarget<TypeItemViewModel>(sender) ?? Vm?.SelectedType;
        if (item is null || Vm is null) return;

        if (!Vm.Types.Remove(item, out var reason))
        {
            MessageBox.Show(reason, "Cannot delete type", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void EditType(TypeItemViewModel item)
    {
        if (Vm is null) return;

        switch (item.Type)
        {
            case ParameterType p: PrimitiveEditorDialog.Edit(OwnerWindow, Vm, p); break;
            case EnumType en:     EnumEditorDialog.Edit(OwnerWindow, Vm, en); break;
            case StructType s:    StructEditorDialog.Edit(OwnerWindow, Vm, s); break;
            case ArrayType a:     ArrayEditorDialog.Edit(OwnerWindow, Vm, a); break;
            default: return;
        }
        Vm.RefreshAll();
    }

    private void EditTypeById(TypeId id)
    {
        var item = Vm?.Types.All.FirstOrDefault(t => t.Type.Id == id);
        if (item is not null) EditType(item);
    }

    private void SelectType(TypeDefinition type)
    {
        if (Vm is null) return;
        Vm.SelectedType = Vm.Types.All.FirstOrDefault(t => t.Type.Id == type.Id);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Resolves which entity a context-menu click was raised for. A ContextMenu sits outside the visual
    /// tree, so the placement target's Tag is the reliable route back to the bound item.
    /// </summary>
    private static T? ContextTarget<T>(object? sender) where T : class
    {
        if (sender is not MenuItem menuItem) return null;

        DependencyObject? current = menuItem;
        while (current is not null)
        {
            if (current is ContextMenu menu)
            {
                if (menu.PlacementTarget is FrameworkElement { Tag: T tagged }) return tagged;
                if (menu.PlacementTarget is FrameworkElement { DataContext: T bound }) return bound;
                return null;
            }
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
