using System.Collections.ObjectModel;
using ProtoDesigner.Core.Validation;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly Validator _validator = new();

    public ObservableCollection<Diagnostic> Diagnostics { get; } = new();

    public int ErrorCount => Diagnostics.Count(d => d.Severity == Severity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == Severity.Warning);
    public int InfoCount => Diagnostics.Count(d => d.Severity == Severity.Info);
    public bool HasErrors => ErrorCount > 0;
    public bool HasNone => Diagnostics.Count == 0;

    public string Summary =>
        Diagnostics.Count == 0
            ? "No issues"
            : $"{ErrorCount} error(s) · {WarningCount} warning(s) · {InfoCount} info";

    public void Refresh(Core.Model.Project project)
    {
        Diagnostics.Clear();
        foreach (var d in _validator.Validate(project))
            Diagnostics.Add(d);

        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasNone));
        OnPropertyChanged(nameof(Summary));
    }
}
