namespace ProtoDesigner.Core.Validation;

/// <summary>A single check. Rules are pure over the context — a rule that mutates the model is a bug.</summary>
public interface IValidationRule
{
    /// <summary>Stable identifier of the primary code this rule emits. Aids test discovery.</summary>
    string Code { get; }

    IEnumerable<Diagnostic> Validate(ValidationContext ctx);
}
