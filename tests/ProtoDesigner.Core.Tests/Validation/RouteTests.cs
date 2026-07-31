namespace ProtoDesigner.Core.Tests.Validation;

/// <summary>
/// Modules and the routes between them. The load-bearing property is that a route holds a
/// <see cref="ModuleId"/>, so renaming a module can never orphan one — the same rule that governs types
/// and fields, applied to the bus topology.
/// </summary>
public class RouteTests
{
    [Fact]
    public void A_route_between_two_modules_on_the_bus_is_clean()
    {
        var b = new ValidationBuilder();
        var a = b.Bus.AddModule("Sensor");
        var c = b.Bus.AddModule("Controller");

        var m = b.NewMessage("Telemetry", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));
        m.Routes.Add(new MessageRoute(a.Id, c.Id));

        var diagnostics = b.Run();
        diagnostics.HasNo(DiagnosticCodes.RouteUnknownModule);
        diagnostics.HasNo(DiagnosticCodes.DuplicateRoute);
        diagnostics.HasNo(DiagnosticCodes.RouteToSelf);
    }

    [Fact]
    public void A_route_to_a_module_that_is_not_on_the_bus_reports_PD0007()
    {
        var b = new ValidationBuilder();
        var a = b.Bus.AddModule("Sensor");

        var m = b.NewMessage("Telemetry", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));
        m.Routes.Add(new MessageRoute(a.Id, ModuleId.New()));

        var d = b.Run().Has(DiagnosticCodes.RouteUnknownModule);
        Assert.Equal(Severity.Error, d.Severity);
    }

    [Fact]
    public void The_same_leg_declared_twice_reports_PD0008()
    {
        var b = new ValidationBuilder();
        var a = b.Bus.AddModule("Sensor");
        var c = b.Bus.AddModule("Controller");

        var m = b.NewMessage("Telemetry", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));
        m.Routes.Add(new MessageRoute(a.Id, c.Id));
        m.Routes.Add(new MessageRoute(a.Id, c.Id));

        var d = b.Run().Has(DiagnosticCodes.DuplicateRoute);
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("Sensor → Controller", d.Message);
    }

    [Fact]
    public void A_module_routing_to_itself_reports_PD0009_as_info()
    {
        var b = new ValidationBuilder();
        var a = b.Bus.AddModule("Sensor");

        var m = b.NewMessage("Loopback", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));
        m.Routes.Add(new MessageRoute(a.Id, a.Id));

        var d = b.Run().Has(DiagnosticCodes.RouteToSelf);
        Assert.Equal(Severity.Info, d.Severity);
    }

    // The point of giving modules identity: the display name is free to change.
    [Fact]
    public void Renaming_a_module_leaves_its_routes_intact()
    {
        var b = new ValidationBuilder();
        var a = b.Bus.AddModule("Sensor");
        var c = b.Bus.AddModule("Controller");

        var m = b.NewMessage("Telemetry", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));
        m.Routes.Add(new MessageRoute(a.Id, c.Id));

        a.Name = "TempSensor";

        Assert.Equal(a.Id, m.Routes[0].From);
        b.Run().HasNo(DiagnosticCodes.RouteUnknownModule);
    }

    // Deleting a module has to take its routes with it, or the remainder point at nothing.
    [Fact]
    public void Removing_a_module_removes_the_routes_that_used_it()
    {
        var b = new ValidationBuilder();
        var a = b.Bus.AddModule("Sensor");
        var c = b.Bus.AddModule("Controller");
        var d = b.Bus.AddModule("Logger");

        var m = b.NewMessage("Telemetry", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));
        m.Routes.Add(new MessageRoute(a.Id, c.Id));
        m.Routes.Add(new MessageRoute(a.Id, d.Id));

        Assert.True(b.Bus.RemoveModule(d.Id));

        Assert.Single(m.Routes);
        Assert.Equal(c.Id, m.Routes[0].To);
        b.Run().HasNo(DiagnosticCodes.RouteUnknownModule);
    }

    [Fact]
    public void A_message_with_no_routes_is_still_valid()
    {
        var b = new ValidationBuilder();
        b.Bus.AddModule("Sensor");
        b.NewMessage("Telemetry", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));

        b.Run().NoErrors();
    }
}
