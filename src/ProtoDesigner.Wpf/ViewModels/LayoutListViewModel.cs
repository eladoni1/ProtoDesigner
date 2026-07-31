using System.Collections.ObjectModel;
using ProtoDesigner.Core.Layout;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

/// <summary>
/// A plain, one-row-per-element reading of the computed layout. Replaces the coloured byte map, which
/// showed regions as blocks and made a variable-length array's actual size impossible to read.
/// </summary>
public sealed class LayoutListViewModel : ObservableObject
{
    public ObservableCollection<LayoutRowViewModel> Rows { get; } = new();

    private string _title = "—";
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private string _summary = "Select a message.";
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }

    private bool _hasRows;
    public bool HasRows { get => _hasRows; set => SetProperty(ref _hasRows, value); }

    public void Show(MessageViewModel? message)
    {
        Rows.Clear();
        var project = message?.Project;

        if (message is null)
        {
            Title = "—";
            Summary = "Select a message to see its layout.";
            HasRows = false;
            return;
        }

        Title = message.Name;

        if (message.Layout is null)
        {
            Summary = message.LayoutError?.Message ?? "Layout could not be computed.";
            HasRows = false;
            return;
        }

        var layout = message.Layout;
        Summary = layout.IsFixedSize
            ? $"{layout.MinBytes} bytes, fixed"
            : $"{layout.MinBytes}–{layout.MaxBytes} bytes, variable";

        // Only real values, in wire order. Padding and struct openers are noise here; array element
        // descriptors ("samples[]") describe one element rather than a field, so they are folded into
        // the array's own row.
        foreach (var node in layout.Flatten())
        {
            if (node.Kind == LayoutNodeKind.Padding) continue;
            if (node.Kind == LayoutNodeKind.Struct) continue;
            if (node.Path.Contains("[]")) continue;

            Rows.Add(new LayoutRowViewModel(message, node, project));
        }

        HasRows = Rows.Count > 0;
    }
}

public sealed class LayoutRowViewModel
{
    public LayoutRowViewModel(MessageViewModel message, LayoutNode node, ProjectViewModel? project)
    {
        Path = node.Path;

        // Resolve through the node's own TypeId. Going via the message's top-level fields only works
        // for those fields — a struct member's binding lives on the struct, so it would fall through
        // to the node kind and print "Parameter" instead of the type the user actually chose.
        TypeName = node.TypeId is { } id && project is not null
            ? project.TypeName(id)
            : node.Kind.ToString();

        Offset = FormatOffset(node.BitOffset);

        if (node.Kind == LayoutNodeKind.Array)
        {
            // The case that was unreadable before: say the element stride, the count, and the total.
            var stride = node.ElementBits;
            if (node.ElementCount is { } count)
            {
                Size = FormatSize(stride * count);
                Detail = $"{FormatSize(stride)} × {count}";
            }
            else
            {
                var region = message.Layout?.Regions.FirstOrDefault(r => r.Index == node.RegionIndex);
                var max = region?.MaxElements ?? 0;
                Size = $"0–{FormatSize(stride * max)}";
                Detail = $"{FormatSize(stride)} × 0..{max}";
            }
        }
        else
        {
            Size = FormatSize(node.BitWidth);
            Detail = node.Transform.IsIdentity
                ? string.Empty
                : $"scaled ÷ {decimal.Round(node.Transform.Scale, 6)}";
        }
    }

    public string Path { get; }
    public string TypeName { get; }
    public string Offset { get; }
    public string Size { get; }
    public string Detail { get; }

    /// <summary>Byte offset, with the bit position appended only when the field does not start on a byte.</summary>
    private static string FormatOffset(int bitOffset) =>
        bitOffset % 8 == 0 ? $"{bitOffset / 8}" : $"{bitOffset / 8}+{bitOffset % 8}b";

    private static string FormatSize(int bits) =>
        bits % 8 == 0 ? $"{bits / 8} B" : $"{bits} bit";
}
