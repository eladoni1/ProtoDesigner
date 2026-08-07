using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProtoDesigner.Application;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.CodeGen;
using ProtoDesigner.Wpf.Mvvm;
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

    /// <summary>
    /// One target-declared setting, bound to a checkbox. The dialog knows nothing about what any
    /// particular key means — it renders whatever the selected generator declares.
    /// </summary>
    private sealed class TargetOptionRow : ObservableObject
    {
        public TargetOptionRow(GeneratorOption option)
        {
            Option = option;
            _isEnabled = option.Default is not "false" and not "0";
        }

        public GeneratorOption Option { get; }

        public string Label => Option.Label;

        public string Description => Option.Description;

        private bool _isEnabled;
        public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    }

    private readonly ObservableCollection<TargetOptionRow> _targetOptions = new();

    /// <summary>One protoc language the user can tick, shown only for a target that emits a schema.</summary>
    private sealed class ProtocLanguageRow(ProtocLanguage language) : ObservableObject
    {
        private bool _isEnabled;

        public string Id => language.Id;
        public string Label => language.Label;

        public string? Description => language.Note;

        public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    }

    private readonly ObservableCollection<ProtocLanguageRow> _protocLanguages = new();

    private GenerateCodeDialog(ProjectViewModel project)
    {
        InitializeComponent();
        _project = project;

        FileList.ItemsSource = _files;

        TargetOptionsList.ItemsSource = _targetOptions;

        foreach (var language in ProtocCompiler.Languages)
            _protocLanguages.Add(new ProtocLanguageRow(language));
        ProtocList.ItemsSource = _protocLanguages;

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

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        // Changing the target changes which settings exist, so the panel is rebuilt before regenerating.
        if (ReferenceEquals(sender, TargetBox)) RebuildTargetOptions();
        Refresh();
    }

    private void OnTargetOptionToggled(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Shows the settings the selected generator declares, and nothing else.</summary>
    private void RebuildTargetOptions()
    {
        _targetOptions.Clear();

        if (TargetBox.SelectedItem is TargetOption target)
            foreach (var option in target.Generator.Options)
                _targetOptions.Add(new TargetOptionRow(option));

        TargetOptionsList.Visibility = _targetOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshProtocPanel();
        RefreshAssignButton();
    }

    /// <summary>
    /// Offers to compile the schema, but only for the target that emits one and only when protoc is
    /// actually installed.
    /// </summary>
    /// <remarks>
    /// Hidden rather than disabled when protoc is missing: a greyed row invites a hunt for the switch
    /// that enables it, and there is none — the answer is to install a compiler this project
    /// deliberately does not vendor. The panel's own caption says where to put it.
    /// </remarks>
    private void RefreshProtocPanel()
    {
        var isSchemaTarget = TargetBox.SelectedItem is TargetOption { Generator.Id: "proto" };
        var available = isSchemaTarget && ProtocCompiler.Locate() is not null;

        ProtocPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ProtocMissingText.Visibility = isSchemaTarget && !available ? Visibility.Visible : Visibility.Collapsed;

        if (!available)
            foreach (var row in _protocLanguages) row.IsEnabled = false;
    }

    private List<string> SelectedProtocLanguages() =>
        ProtocPanel.Visibility == Visibility.Visible
            ? _protocLanguages.Where(l => l.IsEnabled).Select(l => l.Id).ToList()
            : [];

    /// <summary>
    /// Offers the assign action only where it means something: a target that needs stable field numbers,
    /// and a project that still has fields without one.
    /// </summary>
    private void RefreshAssignButton()
    {
        var needsNumbers = TargetBox.SelectedItem is TargetOption { Generator.Id: "proto" }
                           && AssignProtoFieldNumbersCommand.HasUnassigned(_project.Project);

        AssignNumbersButton.Visibility = needsNumbers ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAssignFieldNumbers(object sender, RoutedEventArgs e)
    {
        // Through the journal, so it is undoable and marks the project dirty. A generator must never
        // mutate the model on its way past, and a permanent decision like this belongs in the history
        // where the user can see and reverse it.
        _project.Journal.Do(new AssignProtoFieldNumbersCommand());

        RefreshAssignButton();
        Refresh();
    }

    /// <summary>The checkbox states as the option bag a generator reads.</summary>
    private GeneratorOptions BuildOptions()
    {
        var ns = string.IsNullOrWhiteSpace(NamespaceBox.Text) ? _project.Project.Name : NamespaceBox.Text.Trim();
        var options = new GeneratorOptions(Namespace: ns);

        foreach (var row in _targetOptions)
            options = options.With(row.Option.Key, row.IsEnabled ? "true" : "false");

        return options;
    }

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

        CodeGenerationResult result;
        try
        {
            result = CodeGenerationService.Generate(
                _project.Project, target.Generator, scope.Resolve(), BuildOptions());
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

        // What a target cannot express is stated with the blocking field named. A message that vanishes
        // from the output without explanation is the failure this whole gate exists to prevent — the user
        // should never have to work out why their message is missing.
        var skipped = result.Excluded
            .Select(e => $"{e.Message.Name}: {e.Reason}")
            .ToArray();

        if (_files.Count == 0)
        {
            ShowStatus(
                skipped.Length > 0
                    ? $"Nothing to generate: none of the {skipped.Length} selected message(s) can be "
                      + $"expressed by {target.Generator.DisplayName}."
                    : "Nothing to generate for this selection.",
                skipped.Length > 0 ? skipped : new[] { "That scope covers no messages." },
                blocking: true);
            return;
        }

        if (skipped.Length > 0)
            ShowStatus($"{skipped.Length} message(s) left out — this target cannot express them.",
                skipped, blocking: false);
        else
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

            var detail = written.Select(Path.GetFileName).OfType<string>().ToList();
            var summary = $"Wrote {written.Count} file(s) to {outDir}.";

            // Compiling the schema is a second step over what was just written, so it happens here rather
            // than in the preview — there is nothing to preview, and protoc needs files on disk.
            var languages = SelectedProtocLanguages();
            if (languages.Count > 0)
            {
                var compilation = ProtocCompiler.Run(outDir, languages);

                if (!compilation.Succeeded)
                {
                    // The schema is on disk and correct; only the compile failed. Say both, so the user
                    // does not go looking for output that was never the problem.
                    ShowStatus($"{summary} protoc did not run: {compilation.Error}", detail, blocking: false);
                    return;
                }

                detail.AddRange(compilation.Produced);
                summary += $" protoc produced {compilation.Produced.Count} more for "
                           + $"{string.Join(", ", languages)}.";
            }

            ShowStatus(summary, detail, blocking: false);
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
