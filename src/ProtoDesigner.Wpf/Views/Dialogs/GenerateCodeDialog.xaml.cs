using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProtoDesigner.Application;
using ProtoDesigner.CodeGen;
using ProtoDesigner.Wpf.ViewModels;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>One generated file as the list shows it.</summary>
public sealed record GeneratedFileRow(string Name, string Contents)
{
    public string SizeLabel
    {
        get
        {
            var lines = Contents.Count(c => c == '\n') + 1;
            return $"{lines} lines · {Contents.Length:N0} chars";
        }
    }

    /// <summary>
    /// The file's name. Overridden because a record's generated ToString prints every property — which
    /// here is the whole generated file, and it is what a screen reader would read out for the row.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// Generates code from the current project: pick a language and a scope, see the output, then write it.
/// </summary>
/// <remarks>
/// <para>
/// The preview is the point. Generation runs on every change and shows the result in memory, so the
/// question "what would this produce?" is answered without a folder full of files to clean up — and a
/// target that only emits declarations still shows you the message structure, which is the half worth
/// reading.
/// </para>
/// <para>
/// The work itself goes through <see cref="CodeGenerationService"/>, the same use case the CLI calls.
/// This dialog decides what to show; it does not decide what generating means.
/// </para>
/// </remarks>
public partial class GenerateCodeDialog : Window
{
    private readonly ProjectViewModel _project;
    private readonly ObservableCollection<GeneratedFileRow> _files = new();
    private bool _ready;

    /// <summary>A choice in the scope picker, paired with the scopes it resolves to.</summary>
    private sealed record ScopeOption(string Label, Func<IReadOnlyList<GenerationScope>> Resolve)
    {
        public override string ToString() => Label;
    }

    private sealed record TargetOption(IProtocolGenerator Generator)
    {
        public override string ToString() => Generator.DisplayName;
    }

    private GenerateCodeDialog(ProjectViewModel project)
    {
        InitializeComponent();
        _project = project;

        FileList.ItemsSource = _files;

        TargetBox.ItemsSource = GeneratorCatalog.All.Select(g => new TargetOption(g)).ToArray();
        TargetBox.SelectedIndex = 0;

        ScopeBox.ItemsSource = BuildScopeOptions();
        ScopeBox.SelectedIndex = 0;

        NamespaceBox.Text = project.Project.Name;
        OutputBox.Text = DefaultOutputFolder(project);

        _ready = true;
        Refresh();
    }

    public static void Show(Window? owner, ProjectViewModel project)
    {
        var dialog = new GenerateCodeDialog(project) { Owner = owner };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Everything worth generating, in narrowing order: the whole project, then each bus, then each
    /// module. Modules come from <see cref="GenerationScopes"/> rather than being enumerated here so the
    /// dialog offers exactly what the CLI's <c>--module</c> accepts.
    /// </summary>
    private ScopeOption[] BuildScopeOptions()
    {
        var project = _project.Project;
        var options = new List<ScopeOption>
        {
            new($"Whole project — {CountMessages(GenerationScopes.ForProject(project))} message(s)",
                () => GenerationScopes.ForProject(project)),
        };

        foreach (var bus in project.Buses)
        {
            var captured = bus;
            options.Add(new($"Bus: {captured.Name} — {captured.Messages.Count} message(s)",
                () => GenerationScopes.ForBus(captured)));
        }

        foreach (var moduleName in GenerationScopes.ModuleNames(project))
        {
            var captured = moduleName;
            var count = CountMessages(GenerationScopes.ForModuleNamed(project, captured));
            options.Add(new($"Module: {captured} — {count} message(s)",
                () => GenerationScopes.ForModuleNamed(project, captured)));
        }

        return options.ToArray();
    }

    private static int CountMessages(IReadOnlyList<GenerationScope> scopes) =>
        scopes.Sum(s => s.Messages.Count);

    /// <summary>
    /// A saved project generates into a <c>generated</c> folder beside itself. An unsaved one has nowhere
    /// obvious to put it, so the box stays empty and the user picks.
    /// </summary>
    private static string DefaultOutputFolder(ProjectViewModel project)
    {
        if (project.CurrentFilePath is not { } path) return "";
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "" : Path.Combine(directory, "generated");
    }

    // ---- preview -------------------------------------------------------------------------------

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Regenerates in memory and repaints the list, the preview and the banner.</summary>
    private void Refresh()
    {
        if (!_ready) return;

        var previouslySelected = (FileList.SelectedItem as GeneratedFileRow)?.Name;
        _files.Clear();

        if (TargetBox.SelectedItem is not TargetOption target ||
            ScopeBox.SelectedItem is not ScopeOption scope)
        {
            ShowStatus("Pick a language and a scope.", Array.Empty<string>(), blocking: true);
            return;
        }

        var ns = string.IsNullOrWhiteSpace(NamespaceBox.Text) ? _project.Project.Name : NamespaceBox.Text.Trim();

        CodeGenerationResult result;
        try
        {
            result = CodeGenerationService.Generate(
                _project.Project, target.Generator, scope.Resolve(), new GeneratorOptions(Namespace: ns));
        }
        catch (Exception ex)
        {
            // A layout the engine calls impossible reaches here as an exception rather than a diagnostic.
            // Showing it beats a dialog that silently previews nothing.
            ShowStatus("Could not generate.", new[] { ex.Message }, blocking: true);
            return;
        }

        if (result.Refused)
        {
            ShowStatus(
                $"Refusing to generate: {result.BlockingErrors.Count} validation error(s). Fix these first.",
                result.BlockingErrors.Select(d => $"{d.Code}  {d.Message}  [{d.Target}]").ToArray(),
                blocking: true);
            return;
        }

        foreach (var file in result.Files.Files)
            _files.Add(new GeneratedFileRow(file.RelativePath, file.Contents));

        if (_files.Count == 0)
        {
            ShowStatus("Nothing to generate for this selection.",
                new[] { "That scope covers no messages." }, blocking: true);
            return;
        }

        HideStatus();
        WriteButton.IsEnabled = true;
        WriteButton.Content = $"Write {_files.Count} file(s)";

        // Keep the user on the file they were reading when only the namespace changed.
        FileList.SelectedItem = _files.FirstOrDefault(f => f.Name == previouslySelected) ?? _files[0];
    }

    private void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not GeneratedFileRow row)
        {
            PreviewTitle.Text = "Preview";
            PreviewText.Text = "";
            return;
        }

        PreviewTitle.Text = row.Name;
        PreviewText.Text = row.Contents;
        PreviewText.ScrollToHome();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not GeneratedFileRow row) return;
        try
        {
            Clipboard.SetText(row.Contents);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process; that is not worth losing the dialog over.
            MessageBox.Show(this, $"Could not copy:\n\n{ex.Message}", "ProtoDesigner",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- writing -------------------------------------------------------------------------------

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose an output folder" };
        if (!string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            var start = Path.GetDirectoryName(OutputBox.Text.Trim());
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }

    private void OnWrite(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0) return;

        var outDir = OutputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(outDir))
        {
            ShowStatus("Choose an output folder first.", Array.Empty<string>(), blocking: false);
            return;
        }

        // Overwriting generated output is the normal case — the files carry a "do not edit" header — but
        // it is still someone's folder, so say what is about to be replaced rather than assuming.
        var existing = _files
            .Select(f => Path.Combine(outDir, f.Name))
            .Where(File.Exists)
            .ToList();

        if (existing.Count > 0)
        {
            var names = string.Join("\n  ", existing.Select(Path.GetFileName));
            var answer = MessageBox.Show(this,
                $"{existing.Count} file(s) already exist in that folder and will be overwritten:\n\n  {names}\n\nContinue?",
                "ProtoDesigner", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        try
        {
            var set = new GeneratedFileSet(
                _files.Select(f => new GeneratedFile(f.Name, f.Contents)).ToList());
            var written = CodeGenerationService.Write(set, outDir);

            ShowStatus($"Wrote {written.Count} file(s) to {outDir}.",
                written.Select(Path.GetFileName).OfType<string>().ToArray(), blocking: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Could not write the output:\n\n{ex.Message}", "ProtoDesigner",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // ---- banner --------------------------------------------------------------------------------

    /// <summary>
    /// Shows the banner. <paramref name="blocking"/> means there is nothing to write — it clears the
    /// preview and disables the button, so a stale listing can never be written after a failed run.
    /// </summary>
    private void ShowStatus(string headline, IReadOnlyList<string> details, bool blocking)
    {
        StatusText.Text = headline;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(blocking ? "ErrorBrush" : "SuccessBrush");
        StatusList.ItemsSource = details;
        StatusBanner.Visibility = Visibility.Visible;

        if (!blocking) return;

        PreviewTitle.Text = "Preview";
        PreviewText.Text = "";
        WriteButton.IsEnabled = false;
        WriteButton.Content = "Write files";
    }

    private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;
}
