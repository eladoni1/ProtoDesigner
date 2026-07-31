using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ProtoDesigner.Wpf.Behaviors;

/// <summary>
/// Drag-to-reorder for an <see cref="ItemsControl"/>. Attach with <c>b:DragReorder.IsEnabled="True"</c>.
/// </summary>
/// <remarks>
/// Position tracking uses the real <see cref="DragEventArgs"/> from DragOver/Drop rather than
/// <c>Mouse.GetPosition</c>. During an active OLE drag the mouse is captured by the drag-drop system and
/// <c>Mouse.GetPosition</c> returns stale coordinates, so a behaviour built on it computes a target index
/// that never changes and the drop silently does nothing.
///
/// The control raises <see cref="ReorderRequestedEvent"/> instead of mutating anything, so the view model
/// can route the move through the command journal. It probes first (<see cref="ReorderRequestedEventArgs.IsProbe"/>)
/// so an illegal drop shows no insertion line rather than failing after the user commits.
/// </remarks>
public static class DragReorder
{
    private const double DragThreshold = 5;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(DragReorder),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    public static readonly RoutedEvent ReorderRequestedEvent =
        EventManager.RegisterRoutedEvent(
            "ReorderRequested", RoutingStrategy.Bubble,
            typeof(EventHandler<ReorderRequestedEventArgs>), typeof(DragReorder));

    public static void AddReorderRequestedHandler(DependencyObject d, EventHandler<ReorderRequestedEventArgs> h)
    {
        if (d is UIElement e) e.AddHandler(ReorderRequestedEvent, h);
    }

    public static void RemoveReorderRequestedHandler(DependencyObject d, EventHandler<ReorderRequestedEventArgs> h)
    {
        if (d is UIElement e) e.RemoveHandler(ReorderRequestedEvent, h);
    }

    private const string Format = "ProtoDesigner.ReorderItem";

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items) return;

        if ((bool)e.NewValue)
        {
            items.AllowDrop = true;
            items.PreviewMouseLeftButtonDown += OnMouseDown;
            items.PreviewMouseMove += OnMouseMove;
            items.PreviewMouseLeftButtonUp += OnMouseUp;
            items.DragOver += OnDragOver;
            items.Drop += OnDrop;
            items.DragLeave += OnDragLeave;
        }
        else
        {
            items.PreviewMouseLeftButtonDown -= OnMouseDown;
            items.PreviewMouseMove -= OnMouseMove;
            items.PreviewMouseLeftButtonUp -= OnMouseUp;
            items.DragOver -= OnDragOver;
            items.Drop -= OnDrop;
            items.DragLeave -= OnDragLeave;
        }
    }

    private static Point _origin;
    private static object? _payload;
    private static bool _dragging;
    private static InsertionAdorner? _adorner;

    // ---- starting a drag --------------------------------------------------------------------------

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl items) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        var container = ContainerFrom(items, e.OriginalSource as DependencyObject);
        if (container is null) return;

        _origin = e.GetPosition(items);
        _payload = items.ItemContainerGenerator.ItemFromContainer(container);
        if (_payload == DependencyProperty.UnsetValue) _payload = null;
        _dragging = false;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ItemsControl items) return;
        if (_payload is null || _dragging || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(items);
        if (Math.Abs(current.Y - _origin.Y) < DragThreshold &&
            Math.Abs(current.X - _origin.X) < DragThreshold) return;

        _dragging = true;
        var payload = _payload;
        try
        {
            var data = new DataObject();
            data.SetData(Format, true);           // marker: the payload itself is held in the static field
            DragDrop.DoDragDrop(items, data, DragDropEffects.Move);
        }
        finally
        {
            ClearAdorner();
            _dragging = false;
            _payload = null;
        }
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;
        _payload = null;
    }

    // ---- during the drag --------------------------------------------------------------------------

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl items || _payload is null) return;

        var position = e.GetPosition(items);
        var target = ResolveTarget(items, position, out var insertAfter, out var container);

        if (container is null || target < 0)
        {
            ClearAdorner();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var source = items.Items.IndexOf(_payload);
        var destination = Normalise(source, target, insertAfter);

        var probe = new ReorderRequestedEventArgs(ReorderRequestedEvent, _payload, source, destination)
        {
            IsProbe = true,
        };
        items.RaiseEvent(probe);

        if (probe.Rejected || destination == source)
        {
            ClearAdorner();
            e.Effects = probe.Rejected ? DragDropEffects.None : DragDropEffects.Move;
        }
        else
        {
            ShowAdorner(container, insertAfter);
            e.Effects = DragDropEffects.Move;
        }

        e.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs e) => ClearAdorner();

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl items || _payload is null) { ClearAdorner(); return; }

        ClearAdorner();

        var position = e.GetPosition(items);
        var target = ResolveTarget(items, position, out var insertAfter, out var container);
        if (container is null || target < 0) return;

        var source = items.Items.IndexOf(_payload);
        var destination = Normalise(source, target, insertAfter);
        if (destination == source) return;

        items.RaiseEvent(new ReorderRequestedEventArgs(ReorderRequestedEvent, _payload, source, destination));
        e.Handled = true;
    }

    // ---- geometry ---------------------------------------------------------------------------------

    /// <summary>
    /// Converts "dropped above/below row N" into a destination index in the list as it will look after
    /// the dragged row is removed, which is what a Move expects.
    /// </summary>
    private static int Normalise(int sourceIndex, int targetIndex, bool insertAfter)
    {
        var destination = insertAfter ? targetIndex + 1 : targetIndex;
        if (sourceIndex >= 0 && sourceIndex < destination) destination--;
        return destination;
    }

    private static int ResolveTarget(ItemsControl items, Point position, out bool insertAfter, out FrameworkElement? container)
    {
        insertAfter = false;
        container = null;

        FrameworkElement? last = null;
        var lastIndex = -1;

        for (var i = 0; i < items.Items.Count; i++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            if (!fe.IsVisible) continue;

            last = fe;
            lastIndex = i;

            var topLeft = fe.TranslatePoint(new Point(0, 0), items);
            var bottom = topLeft.Y + fe.ActualHeight;
            if (position.Y < topLeft.Y || position.Y > bottom) continue;

            insertAfter = position.Y > topLeft.Y + fe.ActualHeight / 2;
            container = fe;
            return i;
        }

        // Below the last row: append.
        if (last is not null)
        {
            var topLeft = last.TranslatePoint(new Point(0, 0), items);
            if (position.Y > topLeft.Y)
            {
                insertAfter = true;
                container = last;
                return lastIndex;
            }
            // Above the first row: prepend.
            insertAfter = false;
            container = last;
            return 0;
        }
        return -1;
    }

    // ---- adorner ----------------------------------------------------------------------------------

    private static void ShowAdorner(FrameworkElement container, bool below)
    {
        if (_adorner is not null &&
            ReferenceEquals(_adorner.AdornedElement, container) &&
            _adorner.Below == below) return;

        ClearAdorner();
        var layer = AdornerLayer.GetAdornerLayer(container);
        if (layer is null) return;

        _adorner = new InsertionAdorner(container, below);
        layer.Add(_adorner);
    }

    private static void ClearAdorner()
    {
        if (_adorner is null) return;
        AdornerLayer.GetAdornerLayer(_adorner.AdornedElement)?.Remove(_adorner);
        _adorner = null;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static FrameworkElement? ContainerFrom(ItemsControl items, DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, items))
        {
            if (source is FrameworkElement fe &&
                items.ItemContainerGenerator.ItemFromContainer(fe) != DependencyProperty.UnsetValue)
                return fe;

            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>Text boxes, combo boxes and buttons keep their normal click behaviour.</summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBoxBase or ComboBox or ButtonBase or PasswordBox) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : null;
        }
        return false;
    }
}

public sealed class ReorderRequestedEventArgs : RoutedEventArgs
{
    public ReorderRequestedEventArgs(RoutedEvent routedEvent, object item, int oldIndex, int newIndex)
        : base(routedEvent)
    {
        Item = item;
        OldIndex = oldIndex;
        NewIndex = newIndex;
    }

    public object Item { get; }
    public int OldIndex { get; }
    public int NewIndex { get; }

    /// <summary>True while asking whether the drop would be legal; no mutation should occur.</summary>
    public bool IsProbe { get; init; }

    /// <summary>Set by the handler to refuse the move — suppresses the insertion line.</summary>
    public bool Rejected { get; set; }
}

/// <summary>A single accent line showing where the dragged row would land.</summary>
internal sealed class InsertionAdorner : Adorner
{
    private static readonly Pen Line = CreatePen();

    public InsertionAdorner(UIElement adorned, bool below) : base(adorned)
    {
        Below = below;
        IsHitTestVisible = false;
    }

    public bool Below { get; }

    private static Pen CreatePen()
    {
        var brush = System.Windows.Application.Current?.Resources["Accent"] as Brush ?? Brushes.MediumPurple;
        var pen = new Pen(brush, 2);
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = AdornedElement.RenderSize.Width;
        var y = Below ? AdornedElement.RenderSize.Height : 0;
        dc.DrawLine(Line, new Point(0, y), new Point(width, y));
        dc.DrawEllipse(Line.Brush, null, new Point(3, y), 3, 3);
    }
}
