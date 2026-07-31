using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

/// <summary>
/// A route names a module that is not on the owning bus. Modules are referenced by ID, so this can only
/// arise from a hand-edited file or a module deleted outside <see cref="Bus.RemoveModule"/>.
/// </summary>
public sealed class RouteUnknownModuleRule : IValidationRule
{
    public string Code => DiagnosticCodes.RouteUnknownModule;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
        {
            var known = bus.Modules.Select(m => m.Id).ToHashSet();
            foreach (var message in bus.Messages)
            {
                foreach (var route in message.Routes)
                {
                    if (!known.Contains(route.From))
                        yield return new Diagnostic(Code, Severity.Error,
                            $"Message '{message.Name}' is routed from a module that is not on bus '{bus.Name}'. "
                            + "Pick a sender, or remove the route.",
                            EntityPath.ForMessage(bus, message));

                    if (!known.Contains(route.To))
                        yield return new Diagnostic(Code, Severity.Error,
                            $"Message '{message.Name}' is routed to a module that is not on bus '{bus.Name}'. "
                            + "Pick a receiver, or remove the route.",
                            EntityPath.ForMessage(bus, message));
                }
            }
        }
    }
}

/// <summary>The same sender-to-receiver leg is declared twice on one message.</summary>
public sealed class DuplicateRouteRule : IValidationRule
{
    public string Code => DiagnosticCodes.DuplicateRoute;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
        {
            foreach (var message in bus.Messages)
            {
                foreach (var group in message.Routes.GroupBy(r => r))
                {
                    if (group.Count() <= 1) continue;
                    var from = bus.FindModule(group.Key.From)?.Name ?? "?";
                    var to = bus.FindModule(group.Key.To)?.Name ?? "?";
                    yield return new Diagnostic(Code, Severity.Warning,
                        $"Message '{message.Name}' declares the route {from} → {to} more than once.",
                        EntityPath.ForMessage(bus, message));
                }
            }
        }
    }
}

/// <summary>
/// A module sends a message to itself. Legal — loopback and self-test paths exist — but far more often a
/// slip in the dropdown, so it is worth surfacing.
/// </summary>
public sealed class RouteToSelfRule : IValidationRule
{
    public string Code => DiagnosticCodes.RouteToSelf;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
        {
            foreach (var message in bus.Messages)
            {
                foreach (var route in message.Routes.Where(r => r.From == r.To).Distinct())
                {
                    var name = bus.FindModule(route.From)?.Name ?? "?";
                    yield return new Diagnostic(Code, Severity.Info,
                        $"Message '{message.Name}' is routed from '{name}' back to itself.",
                        EntityPath.ForMessage(bus, message));
                }
            }
        }
    }
}
