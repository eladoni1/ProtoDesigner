namespace ProtoDesigner.Core.Model;

/// <summary>
/// Layout settings at one level of the hierarchy. Null means "inherit from the level above".
/// Resolution order is field, message, bus, project, built-in default.
/// </summary>
public sealed class LayoutOptions
{
    public Endianness? Endianness { get; set; }

    public BitOrder? BitOrder { get; set; }

    /// <summary>Alignment applied to fields that are not bit-packed. Default is 8 (byte-aligned).</summary>
    public int? DefaultAlignmentBits { get; set; }

    public BitPackingMode? PackingMode { get; set; }

    /// <summary>Pads the message to a whole number of bytes. Set false for pure bit-stream protocols.</summary>
    public bool? PadToByteBoundary { get; set; }
}

/// <summary>Fully resolved layout settings. What <see cref="Layout.LayoutEngine"/> actually reads.</summary>
public sealed record EffectiveLayoutOptions(
    Endianness Endianness,
    BitOrder BitOrder,
    int DefaultAlignmentBits,
    BitPackingMode PackingMode,
    bool PadToByteBoundary)
{
    public static EffectiveLayoutOptions Default { get; } =
        new(Model.Endianness.Little, Model.BitOrder.MsbFirst, 8, BitPackingMode.Contiguous, true);

    /// <summary>Resolves a chain of overrides, most specific first. Nulls fall through to the next level.</summary>
    public static EffectiveLayoutOptions Resolve(params LayoutOptions?[] mostSpecificFirst)
    {
        var d = Default;
        return new EffectiveLayoutOptions(
            First(mostSpecificFirst, o => o.Endianness) ?? d.Endianness,
            First(mostSpecificFirst, o => o.BitOrder) ?? d.BitOrder,
            First(mostSpecificFirst, o => o.DefaultAlignmentBits) ?? d.DefaultAlignmentBits,
            First(mostSpecificFirst, o => o.PackingMode) ?? d.PackingMode,
            First(mostSpecificFirst, o => o.PadToByteBoundary) ?? d.PadToByteBoundary);
    }

    private static T? First<T>(LayoutOptions?[] chain, Func<LayoutOptions, T?> select) where T : struct
    {
        foreach (var level in chain)
        {
            if (level is null) continue;
            var value = select(level);
            if (value.HasValue) return value;
        }
        return null;
    }
}

/// <summary>Flat, ID-keyed store of type definitions. Mirrors the persisted shape so a file entry maps to a row later.</summary>
public sealed class TypeLibrary
{
    private readonly Dictionary<TypeId, TypeDefinition> _byId = new();

    public T Add<T>(T type) where T : TypeDefinition
    {
        if (!_byId.TryAdd(type.Id, type))
            throw new ArgumentException($"Type {type.Id} is already registered.", nameof(type));
        return type;
    }

    public TypeDefinition this[TypeId id] => _byId.TryGetValue(id, out var type)
        ? type
        : throw new KeyNotFoundException($"No type registered with id {id}.");

    public bool TryGet(TypeId id, out TypeDefinition? type) => _byId.TryGetValue(id, out type);

    public bool Contains(TypeId id) => _byId.ContainsKey(id);

    /// <summary>Removes a type by id. Returns true if a type was removed. Does not check for references.</summary>
    public bool Remove(TypeId id) => _byId.Remove(id);

    public IReadOnlyCollection<TypeDefinition> All => _byId.Values;

    public int Count => _byId.Count;
}

/// <summary>
/// A participant on a bus — an ECU, a service, a board. Named by the user at bus creation and referenced
/// by ID from every route, so renaming one never orphans a route.
/// </summary>
public sealed class Module
{
    public Module(ModuleId id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Module name must not be empty.", nameof(name));
        Id = id;
        Name = name;
    }

    public Module(string name) : this(ModuleId.New(), name) { }

    public ModuleId Id { get; }

    /// <summary>Display name. Mutable; references are by <see cref="Id"/>.</summary>
    public string Name { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// One sender-to-receiver leg of a message. A message carries a list of these, so the same layout can be
/// published on several legs — <c>Sensor → Controller</c> and <c>Sensor → Logger</c> — without duplicating it.
/// </summary>
public sealed record MessageRoute(ModuleId From, ModuleId To);

/// <summary>The layout of data sent between modules on a bus. Field order is the wire order.</summary>
public sealed class Message
{
    public Message(MessageId id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Message name must not be empty.", nameof(name));
        Id = id;
        Name = name;
    }

    public Message(string name) : this(MessageId.New(), name) { }

    public MessageId Id { get; }

    public string Name { get; set; }

    /// <summary>On-wire discriminator, used when the receiver cannot infer the type from port or channel.</summary>
    public int? WireId { get; set; }

    public LayoutOptions Options { get; } = new();

    public List<FieldBinding> Fields { get; } = new();

    /// <summary>
    /// Who sends this message to whom, as (from, to) pairs over the modules of the owning bus. Empty means
    /// the routing is not modelled — the message is still perfectly valid, it just says nothing about who
    /// talks to whom.
    /// </summary>
    public List<MessageRoute> Routes { get; } = new();

    public string? Description { get; set; }

    public Message With(params FieldBinding[] fields)
    {
        Fields.AddRange(fields);
        return this;
    }

    public FieldBinding? Find(FieldId id) => Fields.FirstOrDefault(f => f.Id == id);

    /// <summary>Moves a field to a new index. Layout is recomputed from the new order; nothing is stored per field.</summary>
    public void MoveField(FieldId id, int newIndex)
    {
        var current = Fields.FindIndex(f => f.Id == id);
        if (current < 0)
            throw new ArgumentException($"Field {id} is not in message '{Name}'.", nameof(id));
        if (newIndex < 0 || newIndex >= Fields.Count)
            throw new ArgumentOutOfRangeException(nameof(newIndex));

        var field = Fields[current];
        Fields.RemoveAt(current);
        Fields.Insert(newIndex, field);
    }

    public bool RemoveField(FieldId id) => Fields.RemoveAll(f => f.Id == id) > 0;

    public override string ToString() => $"Message '{Name}' ({Fields.Count} fields)";
}

/// <summary>A communication bus connecting modules. Owns the messages exchanged over it.</summary>
public sealed class Bus
{
    public Bus(BusId id, string name, Transport transport)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Bus name must not be empty.", nameof(name));
        Id = id;
        Name = name;
        Transport = transport;
    }

    public Bus(string name, Transport transport) : this(BusId.New(), name, transport) { }

    public BusId Id { get; }

    public string Name { get; set; }

    public Transport Transport { get; set; }

    public LayoutOptions Options { get; } = new();

    /// <summary>The participants on this bus. Messages route between them.</summary>
    public List<Module> Modules { get; } = new();

    public List<Message> Messages { get; } = new();

    public Bus With(params Message[] messages)
    {
        Messages.AddRange(messages);
        return this;
    }

    public Module AddModule(string name)
    {
        var module = new Module(name);
        Modules.Add(module);
        return module;
    }

    public Module? FindModule(ModuleId id) => Modules.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Removes a module and every route that referenced it. Routes are dropped rather than left dangling:
    /// a half-route names a module that no longer exists, which nothing downstream can act on.
    /// </summary>
    public bool RemoveModule(ModuleId id)
    {
        if (Modules.RemoveAll(m => m.Id == id) == 0) return false;
        foreach (var message in Messages)
            message.Routes.RemoveAll(r => r.From == id || r.To == id);
        return true;
    }
}

/// <summary>Root aggregate. Owns the shared type library and every bus.</summary>
public sealed class Project
{
    public const int CurrentSchemaVersion = 1;

    public Project(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name must not be empty.", nameof(name));
        Name = name;
    }

    public string Name { get; set; }

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public LayoutOptions Options { get; } = new();

    public TypeLibrary Types { get; } = new();

    public List<Bus> Buses { get; } = new();

    /// <summary>Resolves the settings that apply to a message, walking message then bus then project.</summary>
    public EffectiveLayoutOptions OptionsFor(Bus bus, Message message) =>
        EffectiveLayoutOptions.Resolve(message.Options, bus.Options, Options);
}
