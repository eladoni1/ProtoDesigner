using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application.Commands;

// ---- bus --------------------------------------------------------------------------------------

public sealed class AddBusCommand : IEditCommand
{
    private readonly Bus _bus;

    public AddBusCommand(Bus bus) => _bus = bus;

    public string Describe() => $"Add bus '{_bus.Name}'";
    public void Apply(Project project) => project.Buses.Add(_bus);
    public void Undo(Project project) => project.Buses.Remove(_bus);
}

public sealed class RemoveBusCommand : IEditCommand
{
    private readonly Bus _bus;
    private int _index = -1;

    public RemoveBusCommand(Bus bus) => _bus = bus;

    public string Describe() => $"Remove bus '{_bus.Name}'";

    public void Apply(Project project)
    {
        _index = project.Buses.IndexOf(_bus);
        if (_index >= 0) project.Buses.RemoveAt(_index);
    }

    public void Undo(Project project)
    {
        if (_index >= 0 && _index <= project.Buses.Count) project.Buses.Insert(_index, _bus);
    }
}

public sealed class RenameBusCommand : IEditCommand
{
    private readonly Bus _bus;
    private readonly string _newName;
    private string _oldName = string.Empty;

    public RenameBusCommand(Bus bus, string newName) { _bus = bus; _newName = newName; }

    public string Describe() => $"Rename bus to '{_newName}'";
    public void Apply(Project _)  { _oldName = _bus.Name; _bus.Name = _newName; }
    public void Undo(Project _)   { _bus.Name = _oldName; }
}

// ---- modules ----------------------------------------------------------------------------------

public sealed class AddModuleCommand : IEditCommand
{
    private readonly Bus _bus;
    private readonly Module _module;

    public AddModuleCommand(Bus bus, Module module) { _bus = bus; _module = module; }

    public string Describe() => $"Add module '{_module.Name}'";
    public void Apply(Project _) => _bus.Modules.Add(_module);
    public void Undo(Project _) => _bus.Modules.Remove(_module);
}

/// <summary>
/// Removes a module and every route that used it, restoring both on undo. Routes have to travel with the
/// module: leaving them behind would point at a module that no longer exists.
/// </summary>
public sealed class RemoveModuleCommand : IEditCommand
{
    private readonly Bus _bus;
    private readonly Module _module;
    private int _index = -1;
    private readonly List<(Message Message, int Index, MessageRoute Route)> _routes = new();

    public RemoveModuleCommand(Bus bus, Module module) { _bus = bus; _module = module; }

    public string Describe() => $"Remove module '{_module.Name}'";

    public void Apply(Project _)
    {
        _routes.Clear();
        foreach (var message in _bus.Messages)
        {
            for (var i = message.Routes.Count - 1; i >= 0; i--)
            {
                var route = message.Routes[i];
                if (route.From != _module.Id && route.To != _module.Id) continue;
                _routes.Add((message, i, route));
                message.Routes.RemoveAt(i);
            }
        }

        _index = _bus.Modules.IndexOf(_module);
        if (_index >= 0) _bus.Modules.RemoveAt(_index);
    }

    public void Undo(Project _)
    {
        if (_index >= 0 && _index <= _bus.Modules.Count) _bus.Modules.Insert(_index, _module);

        // Applied back-to-front, so replaying in reverse restores the original indices exactly.
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            var (message, at, route) = _routes[i];
            message.Routes.Insert(Math.Min(at, message.Routes.Count), route);
        }
        _routes.Clear();
    }
}

public sealed class RenameModuleCommand : IEditCommand
{
    private readonly Module _module;
    private readonly string _newName;
    private string _oldName = string.Empty;

    public RenameModuleCommand(Module module, string newName) { _module = module; _newName = newName; }

    public string Describe() => $"Rename module to '{_newName}'";
    public void Apply(Project _) { _oldName = _module.Name; _module.Name = _newName; }
    public void Undo(Project _)  { _module.Name = _oldName; }
}

// ---- routes -----------------------------------------------------------------------------------

public sealed class AddRouteCommand : IEditCommand
{
    private readonly Message _message;
    private readonly MessageRoute _route;

    public AddRouteCommand(Message message, MessageRoute route) { _message = message; _route = route; }

    public string Describe() => $"Add route to '{_message.Name}'";
    public void Apply(Project _) => _message.Routes.Add(_route);
    public void Undo(Project _)
    {
        var at = _message.Routes.LastIndexOf(_route);
        if (at >= 0) _message.Routes.RemoveAt(at);
    }
}

public sealed class RemoveRouteCommand : IEditCommand
{
    private readonly Message _message;
    private readonly MessageRoute _route;
    private int _index = -1;

    public RemoveRouteCommand(Message message, MessageRoute route) { _message = message; _route = route; }

    public string Describe() => $"Remove route from '{_message.Name}'";

    public void Apply(Project _)
    {
        _index = _message.Routes.IndexOf(_route);
        if (_index >= 0) _message.Routes.RemoveAt(_index);
    }

    public void Undo(Project _)
    {
        if (_index >= 0 && _index <= _message.Routes.Count) _message.Routes.Insert(_index, _route);
    }
}

/// <summary>Swaps one leg for another in place, which is what changing a dropdown on an existing row means.</summary>
public sealed class ChangeRouteCommand : IEditCommand
{
    private readonly Message _message;
    private readonly int _index;
    private readonly MessageRoute _newRoute;
    private MessageRoute? _oldRoute;

    public ChangeRouteCommand(Message message, int index, MessageRoute newRoute)
    {
        _message = message; _index = index; _newRoute = newRoute;
    }

    public string Describe() => $"Change route on '{_message.Name}'";

    public void Apply(Project _)
    {
        if (_index < 0 || _index >= _message.Routes.Count) return;
        _oldRoute = _message.Routes[_index];
        _message.Routes[_index] = _newRoute;
    }

    public void Undo(Project _)
    {
        if (_oldRoute is null || _index < 0 || _index >= _message.Routes.Count) return;
        _message.Routes[_index] = _oldRoute;
    }
}

// ---- message ----------------------------------------------------------------------------------

public sealed class AddMessageCommand : IEditCommand
{
    private readonly Bus _bus;
    private readonly Message _message;

    public AddMessageCommand(Bus bus, Message message) { _bus = bus; _message = message; }

    public string Describe() => $"Add message '{_message.Name}'";
    public void Apply(Project _) => _bus.Messages.Add(_message);
    public void Undo(Project _) => _bus.Messages.Remove(_message);
}

public sealed class RemoveMessageCommand : IEditCommand
{
    private readonly Bus _bus;
    private readonly Message _message;
    private int _index = -1;

    public RemoveMessageCommand(Bus bus, Message message) { _bus = bus; _message = message; }

    public string Describe() => $"Remove message '{_message.Name}'";

    public void Apply(Project _)
    {
        _index = _bus.Messages.IndexOf(_message);
        if (_index >= 0) _bus.Messages.RemoveAt(_index);
    }

    public void Undo(Project _)
    {
        if (_index >= 0 && _index <= _bus.Messages.Count) _bus.Messages.Insert(_index, _message);
    }
}

public sealed class RenameMessageCommand : IEditCommand
{
    private readonly Message _message;
    private readonly string _newName;
    private string _oldName = string.Empty;

    public RenameMessageCommand(Message message, string newName) { _message = message; _newName = newName; }

    public string Describe() => $"Rename message to '{_newName}'";
    public void Apply(Project _)  { _oldName = _message.Name; _message.Name = _newName; }
    public void Undo(Project _)   { _message.Name = _oldName; }
}

public sealed class SetWireIdCommand : IEditCommand
{
    private readonly Message _message;
    private readonly int? _newWireId;
    private int? _oldWireId;

    public SetWireIdCommand(Message message, int? newWireId) { _message = message; _newWireId = newWireId; }

    public string Describe() => _newWireId.HasValue
        ? $"Set wire id of '{_message.Name}' to {_newWireId}"
        : $"Clear wire id of '{_message.Name}'";
    public void Apply(Project _)  { _oldWireId = _message.WireId; _message.WireId = _newWireId; }
    public void Undo(Project _)   { _message.WireId = _oldWireId; }
}

// ---- fields -----------------------------------------------------------------------------------

public sealed class AddFieldCommand : IEditCommand
{
    private readonly Message _message;
    private readonly FieldBinding _field;
    private readonly int _index;

    public AddFieldCommand(Message message, FieldBinding field, int? index = null)
    {
        _message = message;
        _field = field;
        _index = index ?? message.Fields.Count;
    }

    public string Describe() => $"Add field '{_field.Name}'";
    public void Apply(Project _) => _message.Fields.Insert(Math.Min(_index, _message.Fields.Count), _field);
    public void Undo(Project _) => _message.Fields.Remove(_field);
}

public sealed class RemoveFieldCommand : IEditCommand
{
    private readonly Message _message;
    private readonly FieldBinding _field;
    private int _index = -1;

    public RemoveFieldCommand(Message message, FieldBinding field) { _message = message; _field = field; }

    public string Describe() => $"Remove field '{_field.Name}'";

    public void Apply(Project _)
    {
        _index = _message.Fields.IndexOf(_field);
        if (_index >= 0) _message.Fields.RemoveAt(_index);
    }

    public void Undo(Project _)
    {
        if (_index >= 0 && _index <= _message.Fields.Count) _message.Fields.Insert(_index, _field);
    }
}

public sealed class MoveFieldCommand : IEditCommand
{
    private readonly Message _message;
    private readonly FieldBinding _field;
    private readonly int _newIndex;
    private int _oldIndex = -1;

    public MoveFieldCommand(Message message, FieldBinding field, int newIndex)
    {
        _message = message; _field = field; _newIndex = newIndex;
    }

    public string Describe() => $"Move field '{_field.Name}'";

    public void Apply(Project _)
    {
        _oldIndex = _message.Fields.IndexOf(_field);
        if (_oldIndex < 0) return;
        _message.Fields.RemoveAt(_oldIndex);
        var target = Math.Clamp(_newIndex, 0, _message.Fields.Count);
        _message.Fields.Insert(target, _field);
    }

    public void Undo(Project _)
    {
        if (_oldIndex < 0) return;
        _message.Fields.Remove(_field);
        _message.Fields.Insert(Math.Min(_oldIndex, _message.Fields.Count), _field);
    }
}

public sealed class RenameFieldCommand : IEditCommand
{
    private readonly FieldBinding _field;
    private readonly string _newName;
    private string _oldName = string.Empty;

    public RenameFieldCommand(FieldBinding field, string newName) { _field = field; _newName = newName; }

    public string Describe() => $"Rename field to '{_newName}'";
    public void Apply(Project _) { _oldName = _field.Name; _field.Name = _newName; }
    public void Undo(Project _)  { _field.Name = _oldName; }
}

/// <summary>Sets, or clears, a message's own byte order — the default every field inherits.</summary>
public sealed class SetMessageEndiannessCommand : IEditCommand
{
    private readonly Message _message;
    private readonly Endianness? _value;
    private Endianness? _previous;

    public SetMessageEndiannessCommand(Message message, Endianness? value)
    {
        _message = message;
        _value = value;
    }

    public string Describe() =>
        _value is null ? $"Inherit byte order for '{_message.Name}'" : $"Set '{_message.Name}' to {_value}";

    public void Apply(Project _) { _previous = _message.Options.Endianness; _message.Options.Endianness = _value; }
    public void Undo(Project _) { _message.Options.Endianness = _previous; }
}

/// <summary>
/// Sets, or clears, a field's own byte order.
/// </summary>
/// <remarks>
/// Null means "inherit", which is a real state rather than a missing one: it is what lets a message or
/// bus keep answering for the field. So the undo restores the previous nullable value rather than
/// clearing it.
/// </remarks>
public sealed class SetFieldEndiannessCommand : IEditCommand
{
    private readonly FieldBinding _field;
    private readonly Endianness? _value;
    private Endianness? _previous;

    public SetFieldEndiannessCommand(FieldBinding field, Endianness? value)
    {
        _field = field;
        _value = value;
    }

    public string Describe() =>
        _value is null ? $"Inherit byte order for '{_field.Name}'" : $"Set '{_field.Name}' to {_value}";

    public void Apply(Project _) { _previous = _field.Encoding.Endianness; _field.Encoding.Endianness = _value; }
    public void Undo(Project _) { _field.Encoding.Endianness = _previous; }
}

/// <summary>
/// Replaces the encoding wholesale. The undo restores the previous instance — cheaper and simpler
/// than tracking individual property changes, and encoding edits are already coarse-grained in the UI.
/// </summary>
public sealed class ChangeEncodingCommand : IEditCommand
{
    private readonly FieldBinding _field;
    private readonly FieldEncoding _newEncoding;
    private FieldEncoding _oldEncoding = FieldEncoding.Natural();

    public ChangeEncodingCommand(FieldBinding field, FieldEncoding newEncoding)
    {
        _field = field;
        _newEncoding = newEncoding;
    }

    public string Describe() => $"Change encoding of '{_field.Name}'";
    public void Apply(Project _) { _oldEncoding = _field.Encoding; _field.Encoding = _newEncoding; }
    public void Undo(Project _)  { _field.Encoding = _oldEncoding; }
}

public sealed class ChangeFieldTypeCommand : IEditCommand
{
    private readonly FieldBinding _field;
    private readonly TypeId _newTypeId;
    private TypeId _oldTypeId;

    public ChangeFieldTypeCommand(FieldBinding field, TypeId newTypeId) { _field = field; _newTypeId = newTypeId; }

    public string Describe() => $"Change type of '{_field.Name}'";
    public void Apply(Project _) { _oldTypeId = _field.TypeId; _field.TypeId = _newTypeId; }
    public void Undo(Project _)  { _field.TypeId = _oldTypeId; }
}

// ---- types ------------------------------------------------------------------------------------

public sealed class AddTypeCommand : IEditCommand
{
    private readonly TypeDefinition _type;
    public AddTypeCommand(TypeDefinition type) => _type = type;
    public string Describe() => $"Add type '{_type.Name}'";
    public void Apply(Project project) => project.Types.Add(_type);
    public void Undo(Project project) => project.Types.Remove(_type.Id);
}

public sealed class RemoveTypeCommand : IEditCommand
{
    private readonly TypeDefinition _type;

    public RemoveTypeCommand(TypeDefinition type) => _type = type;

    public string Describe() => $"Remove type '{_type.Name}'";
    public void Apply(Project project) => project.Types.Remove(_type.Id);
    public void Undo(Project project) => project.Types.Add(_type);
}

public sealed class RenameTypeCommand : IEditCommand
{
    private readonly TypeDefinition _type;
    private readonly string _newName;
    private string _oldName = string.Empty;

    public RenameTypeCommand(TypeDefinition type, string newName) { _type = type; _newName = newName; }
    public string Describe() => $"Rename type to '{_newName}'";
    public void Apply(Project _) { _oldName = _type.Name; _type.Name = _newName; }
    public void Undo(Project _)  { _type.Name = _oldName; }
}

// ---- protobuf export ---------------------------------------------------------------------------

/// <summary>
/// Assigns protobuf field numbers to any field that does not yet have one, across the whole project.
/// </summary>
/// <remarks>
/// <para>
/// It is a command rather than something the generator does on the way past, for two reasons. Generators
/// read the IR and must never mutate the project — that one-way boundary is what keeps "add a language
/// without touching the editor" true. And a field number is permanent: once assigned and shipped it can
/// never mean a different field, so the act of creating one belongs in the undo history where the user
/// can see it.
/// </para>
/// <para>
/// Existing numbers are never reassigned or compacted. New ones continue above the highest already in
/// use within that message, so a field added after a gap does not silently claim a retired number.
/// </para>
/// </remarks>
public sealed class AssignProtoFieldNumbersCommand : IEditCommand
{
    private readonly List<(FieldBinding Field, int? Previous)> _changed = new();

    public string Describe() => "Assign protobuf field numbers";

    public void Apply(Project project)
    {
        _changed.Clear();

        foreach (var fields in FieldGroups(project))
        {
            // One number space per message and per struct, since each becomes its own protobuf message.
            var next = fields
                .Select(f => f.ProtoFieldNumber ?? 0)
                .DefaultIfEmpty(0)
                .Max() + 1;

            foreach (var field in fields)
            {
                if (field.ProtoFieldNumber is not null) continue;

                _changed.Add((field, null));
                field.ProtoFieldNumber = next++;
            }
        }
    }

    public void Undo(Project _)
    {
        foreach (var (field, previous) in _changed) field.ProtoFieldNumber = previous;
        _changed.Clear();
    }

    /// <summary>True when anything at all would be assigned — lets a caller skip a no-op edit.</summary>
    public static bool HasUnassigned(Project project) =>
        FieldGroups(project).Any(group => group.Any(f => f.ProtoFieldNumber is null));

    private static IEnumerable<IReadOnlyList<FieldBinding>> FieldGroups(Project project)
    {
        foreach (var bus in project.Buses)
            foreach (var message in bus.Messages)
                yield return message.Fields;

        foreach (var type in project.Types.All)
            if (type is StructType s)
                yield return s.Fields;
    }
}
