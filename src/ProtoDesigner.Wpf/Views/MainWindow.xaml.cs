using System.Windows;
using System.Windows.Input;
using ProtoDesigner.Wpf.ViewModels;
using ProtoDesigner.Wpf.Views.Dialogs;

namespace ProtoDesigner.Wpf.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Opens the generate dialog. A routed command rather than a plain click handler so the menu item,
    /// the toolbar button and Ctrl+G are one thing with one gesture shown next to it.
    /// </summary>
    public static readonly RoutedUICommand GenerateCodeCommand =
        new("Generate code", nameof(GenerateCodeCommand), typeof(MainWindow));

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the generate dialog against the current project. It works on the in-memory model rather than
    /// the saved file, so what you are looking at is what gets generated — including edits you have not
    /// saved yet.
    /// </summary>
    private void OnGenerateCode(object sender, ExecutedRoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        GenerateCodeDialog.Show(this, main.Project);
    }
}
