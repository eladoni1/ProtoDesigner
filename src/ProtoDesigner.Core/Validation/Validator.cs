using ProtoDesigner.Core.Model;
using ProtoDesigner.Core.Validation.Rules;

namespace ProtoDesigner.Core.Validation;

/// <summary>
/// Runs the configured rules against a project. The default rule set is the shipped catalogue; pass a
/// custom set to unit-test one rule in isolation or to experiment with an in-progress rule.
/// </summary>
public sealed class Validator
{
    private readonly IReadOnlyList<IValidationRule> _rules;

    public Validator() : this(DefaultRules()) { }

    public Validator(IEnumerable<IValidationRule> rules)
    {
        _rules = rules?.ToArray() ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>The shipped rule catalogue. Adding a rule to the product is a one-line edit here.</summary>
    public static IReadOnlyList<IValidationRule> DefaultRules() => new IValidationRule[]
    {
        new DuplicateBusNameRule(),
        new DuplicateMessageNameRule(),
        new DuplicateTypeNameRule(),
        new DuplicateWireIdRule(),
        new WireIdNotAssignedRule(),
        new DuplicateFieldNameRule(),
        new RouteUnknownModuleRule(),
        new DuplicateRouteRule(),
        new RouteToSelfRule(),
        new UnknownTypeReferenceRule(),
        new RecursiveTypeRule(),
        new EnumWithoutMembersRule(),
        new DuplicateEnumMemberRule(),
        new EncodingFeasibilityRule(),
        new DynamicArrayRule(),
        new ArrayOfCompositeElementRule(),
        new TransportBudgetRule(),
        new UnreferencedTypeRule(),
        new BusHasNoMessagesRule(),
        new MessageHasNoFieldsRule(),
    };

    /// <summary>Runs every rule and returns the sorted, de-duplicated findings.</summary>
    public IReadOnlyList<Diagnostic> Validate(Project project)
    {
        var ctx = new ValidationContext(project);
        var findings = new List<Diagnostic>();

        foreach (var rule in _rules)
        {
            try
            {
                findings.AddRange(rule.Validate(ctx));
            }
            catch (Exception ex)
            {
                findings.Add(new Diagnostic(
                    "PD9999", Severity.Error,
                    $"Rule {rule.GetType().Name} threw {ex.GetType().Name}: {ex.Message}. This is a bug in the rule.",
                    EntityPath.Project));
            }
        }

        return findings
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Target.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>True if <see cref="Validate"/> would produce any Error.</summary>
    public bool HasBlockingErrors(Project project) =>
        Validate(project).Any(d => d.Severity == Severity.Error);
}
