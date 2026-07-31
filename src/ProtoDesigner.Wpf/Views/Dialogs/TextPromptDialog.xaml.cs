using System.Windows;
using System.Windows.Input;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>
/// A single-line text prompt. Used for the many small "name this thing" interactions — renaming a bus,
/// creating a message — where a full dialog would be heavier than the decision warrants.
/// </summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public string Value
    {
        get => Input.Text;
        private set => Input.Text = value;
    }

    /// <summary>Shows the prompt and returns the entered text, or null if the user cancelled.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "", string? hint = null)
    {
        var dialog = new TextPromptDialog
        {
            Title = title,
            Owner = owner,
            Value = initial,
        };
        dialog.PromptText.Text = prompt;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            dialog.HintText.Text = hint;
            dialog.HintText.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value)
            ? dialog.Value.Trim()
            : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) DialogResult = false;
    }
}
