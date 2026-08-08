using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProtoDesigner.Application;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.CodeGen;
using ProtoDesigner.Core.Model;
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

    /// <summary>One message the current scope covers, with whether the user and the target both want it.</summary>
    /// <remarks>
    /// The two reasons a message can be left out are kept apart on purpose. <see cref="IsIncluded"/> is the
    /// user's choice; <see cref="IsEligible"/> is the target's answer, and when it is false the tick is
    /// disabled rather than merely cleared — otherwise a user would tick it, watch it come back unticked,
    /// and have no idea why. <see cref="Explanation"/> is the why, which nothing in this dialog showed
    /// per-message before.
    /// </remarks>
    private sealed class MessageRow : ObservableObject
    {
        public MessageRow(Bus bus, Message message, string label)
        {
            Bus = bus;
            Message = message;
            Label = label;
        }

        public Bus Bus { get; }

        public Message Message { get; }

        public string Label { get; }

        private bool _isIncluded = true;
        public bool IsIncluded { get => _isIncluded; set => SetProperty(ref _isIncluded, value); }

        private bool _isEligible = true;
        public bool IsEligible { get => _isEligible; set => SetProperty(ref _isEligible, value); }

        private string? _blocker;
        public string? Blocker
        {
            get => _blocker;
            set { if (SetProperty(ref _blocker, value)) OnPropertyChanged(nameof(Explanation)); }
        }

        public string Explanation => Blocker ?? $"Include '{Message.Name}' in the generated output.";
    }

    private readonly ObservableCollection<MessageRow> _messages = new();

    /// <summary>
    /// Ticks the user has cleared, remembered by id so switching target or scope does not silently
    /// re-enable something they deliberately turned off.
    /// </summary>
    private readonly HashSet<MessageId> _unticked = new();

    private GenerateCodeDialog(ProjectViewModel project)
    {
        InitializeComponent();
        _project = project;

        FileList.ItemsSource = _files;
        MessageList.ItemsSource = _messages;

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

        RebuildTargetOptions();
        RebuildMessageList();

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
        RebuildMessageList();
        Refresh();
    }

    private void OnTargetOptionToggled(object sender, RoutedEventArgs e) => Refresh();

    // ---- message checklist ----------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the checklist for the current scope and target, keeping the user's own exclusions.
    /// </summary>
    /// <remarks>
    /// Eligibility comes from <see cref="ProtobufCompatibility"/> — the same gate
    /// <see cref="CodeGenerationService"/> applies — rather than from a second copy of the rules. That is
    /// what stops the list offering a message the generator would then drop.
    /// </remarks>
    private void RebuildMessageList()
    {
        _messages.Clear();

        if (ScopeBox.SelectedItem is not ScopeOption scope ||
            TargetBox.SelectedItem is not TargetOption target)
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var scopes = scope.Resolve();
        var manyBuses = scopes.Count > 1;

        foreach (var s in scopes)
        {
            // Only ask about eligibility for a target that has something to refuse. For the C target every
            // message is expressible, and running the protobuf rules would be wasted work.
            var blockers = target.Generator.CoversEveryMessage
                ? new Dictionary<MessageId, string>()
                : ProtobufCompatibility.ForBus(_project.Project, s.Bus)
                    .Where(el => !el.IsEligible)
                    .ToDictionary(el => el.Message.Id, el => el.Reason!);

            foreach (var message in s.Messages)
            {
                var label = manyBuses ? $"{s.Bus.Name} · {message.Name}" : message.Name;
                var row = new MessageRow(s.Bus, message, label);

                if (blockers.TryGetValue(message.Id, out var reason))
                {
                    row.IsEligible = false;
                    row.IsIncluded = false;
                    row.Blocker = $"{target.Generator.DisplayName} cannot express this message. {reason}";
                }
                else
                {
                    row.IsIncluded = !_unticked.Contains(message.Id);
                }

                _messages.Add(row);
            }
        }

        MessagePanel.Visibility = _messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMessageSummary();
    }

    private void UpdateMessageSummary()
    {
        var included = _messages.Count(m => m.IsIncluded);
        var refused = _messages.Count(m => !m.IsEligible);

        MessageSummary.Text = refused > 0
            ? $"{included} of {_messages.Count} selected · {refused} this target cannot express"
            : $"{included} of {_messages.Count} selected";
    }

    private void OnMessageToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not CheckBox { DataContext: MessageRow row }) return;

        if (row.IsIncluded) _unticked.Remove(row.Message.Id);
        else _unticked.Add(row.Message.Id);

        UpdateMessageSummary();
        Refresh();
    }

    private void OnSelectAllMessages(object sender, RoutedEventArgs e) => SetAllMessages(true);

    private void OnSelectNoMessages(object sender, RoutedEventArgs e) => SetAllMessages(false);

    private void SetAllMessages(bool included)
    {
        foreach (var row in _messages)
        {
            // An ineligible message stays off either way: "All" means "everything that can be generated",
            // and ticking one the target refuses would promise output that never arrives.
            if (!row.IsEligible) continue;

            row.IsIncluded = included;
            if (included) _unticked.Remove(row.Message.Id);
            else _unticked.Add(row.Message.Id);
        }

        UpdateMessageSummary();
        Refresh();
    }

    /// <summary>The ticked messages, as scopes, preserving each bus's own message order.</summary>
    private IReadOnlyList<GenerationScope> SelectedScopes() =>
        _messages
            .Where(m => m.IsIncluded)
            .GroupBy(m => m.Bus)
            .SelectMany(g => GenerationScopes.ForMessages(g.Key, g.Select(m => m.Message.Id)))
            .ToList();

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
        // Assigning can change eligibility: PD0073 refuses a message whose fields share a number, and
        // giving every field its own clears it.
        RebuildMessageList();
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
            ScopeBox.SelectedItem is not ScopeOption)
        {
            ShowStatus("Pick a language and a scope.", Array.Empty<string>(), blocking: true);
            return;
        }

        // The checklist is the scope now: it starts as everything the picker chose, minus what the target
        // refuses and what the user unticked. Reading it here rather than re-resolving the picker is what
        // makes the preview agree with the ticks.
        var selected = SelectedScopes();

        CodeGenerationResult result;
        try
        {
            result = CodeGenerationService.Generate(
                _project.Project, target.Generator, selected, BuildOptions());
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
        //
        // Read off the checklist rather than result.Excluded: the ineligible messages were already dropped
        // before the service saw them, so its own excluded list is empty by the time it returns. The
        // service still narrows independently, and must — the CLI has no checklist.
        var skipped = _messages
            .Where(m => !m.IsEligible)
            .Select(m => $"{m.Message.Name}: {m.Blocker}")
            .ToArray();

        var unticked = _messages.Count(m => m.IsEligible && !m.IsIncluded);

        if (_files.Count == 0)
        {
            // Three different reasons for an empty result, and saying the wrong one sends the user looking
            // in the wrong place: an empty scope, a target that refuses everything in it, or ticks the user
            // cleared themselves. Only the middle one is about the target.
            var headline =
                _messages.Count == 0 ? "Nothing to generate for this selection."
                : skipped.Length == _messages.Count
                    ? $"Nothing to generate: none of the {_messages.Count} message(s) in this scope can be "
                      + $"expressed by {target.Generator.DisplayName}."
                    : "Nothing to generate: no messages are ticked.";

            var detail = _messages.Count == 0
                ? new[] { "That scope covers no messages." }
                : skipped;

            ShowStatus(headline, detail, blocking: true);
            return;
        }

        // Unticking is a choice, not a surprise, so it is counted rather than listed field by field.
        if (skipped.Length > 0)
            ShowStatus($"{skipped.Length} message(s) left out — this target cannot express them.",
                skipped, blocking: false);
        else if (unticked > 0)
            ShowStatus($"{unticked} message(s) unticked and not generated.",
                Array.Empty<string>(), blocking: false);
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
