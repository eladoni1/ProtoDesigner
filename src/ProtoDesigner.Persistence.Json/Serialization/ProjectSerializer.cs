using System.Globalization;
using System.Text.Json.Nodes;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Persistence.Json.Serialization;

/// <summary>
/// Model ↔ <see cref="JsonObject"/>. The on-disk shape is flat and ID-keyed so a file entry can map 1:1 to
/// a future DB row without reshaping, and so that unrelated entities never move between saves.
/// </summary>
internal static class ProjectSerializer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- to JSON --------------------------------------------------------------------------------

    public static JsonObject ToJson(Project project)
    {
        var doc = new JsonObject
        {
            ["schemaVersion"] = project.SchemaVersion,
            ["name"] = project.Name,
            ["options"] = ToJson(project.Options),
            ["types"] = TypesToJson(project.Types),
            ["buses"] = BusesToJson(project.Buses),
        };
        return doc;
    }

    private static JsonObject TypesToJson(TypeLibrary types)
    {
        var obj = new JsonObject();
        foreach (var t in types.All.OrderBy(t => t.Id.Value))
            obj[t.Id.ToString()] = TypeToJson(t);
        return obj;
    }

    private static JsonObject TypeToJson(TypeDefinition type)
    {
        var obj = new JsonObject
        {
            ["name"] = type.Name,
        };
        if (type.Description is not null) obj["description"] = type.Description;

        switch (type)
        {
            case ParameterType p:
                obj["kind"] = "parameter";
                obj["primitive"] = p.Kind.ToString();
                if (p.Range is { } range)
                {
                    obj["rangeMin"] = range.Min.ToString(Inv);
                    obj["rangeMax"] = range.Max.ToString(Inv);
                }
                if (p.WireBits.HasValue) obj["wireBits"] = p.WireBits.Value;
                obj["wireForm"] = p.WireForm.ToString();
                if (p.WireOffset.HasValue) obj["wireOffset"] = p.WireOffset.Value.ToString(Inv);
                if (p.WireScale.HasValue) obj["wireScale"] = p.WireScale.Value.ToString(Inv);
                break;

            case EnumType e:
                obj["kind"] = "enum";
                obj["underlying"] = e.UnderlyingKind.ToString();
                obj["isFlags"] = e.IsFlags;
                if (e.WireBits.HasValue) obj["wireBits"] = e.WireBits.Value;
                obj["wireForm"] = e.WireForm.ToString();
                if (e.WireOffset.HasValue) obj["wireOffset"] = e.WireOffset.Value.ToString(Inv);
                if (e.WireScale.HasValue) obj["wireScale"] = e.WireScale.Value.ToString(Inv);
                var members = new JsonArray();
                foreach (var m in e.Members)
                    members.Add(new JsonObject { ["name"] = m.Name, ["value"] = m.Value });
                obj["members"] = members;
                break;

            case StructType s:
                obj["kind"] = "struct";
                obj["fields"] = FieldsToJson(s.Fields);
                break;

            case ArrayType a:
                obj["kind"] = "array";
                obj["elementTypeId"] = a.ElementTypeId.ToString();
                obj["length"] = ArrayLengthToJson(a.Length);
                break;

            default:
                throw new NotSupportedException($"Unknown type kind '{type.GetType().Name}'.");
        }

        return obj;
    }

    private static JsonObject ArrayLengthToJson(ArrayLength length)
    {
        return length switch
        {
            ArrayLength.Fixed f => new JsonObject { ["kind"] = "fixed", ["count"] = f.Count },
            ArrayLength.CountFromField c => new JsonObject
            {
                ["kind"] = "countFromField",
                ["countFieldId"] = c.CountFieldId.ToString(),
                ["maxCount"] = c.MaxCount,
            },
            ArrayLength.LengthPrefixed l => new JsonObject
            {
                ["kind"] = "lengthPrefixed",
                ["prefixBits"] = l.PrefixBits,
                ["maxCount"] = l.MaxCount,
            },
            ArrayLength.Terminated t => new JsonObject
            {
                ["kind"] = "terminated",
                ["sentinel"] = new JsonArray(t.Sentinel.Select(b => (JsonNode?)b).ToArray()),
                ["maxCount"] = t.MaxCount,
            },
            ArrayLength.FillRemaining r => new JsonObject { ["kind"] = "fillRemaining", ["maxCount"] = r.MaxCount },
            _ => throw new NotSupportedException($"Unknown array length '{length.GetType().Name}'."),
        };
    }

    private static JsonArray BusesToJson(IReadOnlyList<Bus> buses)
    {
        var arr = new JsonArray();
        foreach (var b in buses.OrderBy(b => b.Id.Value))
        {
            var obj = new JsonObject
            {
                ["id"] = b.Id.ToString(),
                ["name"] = b.Name,
                ["transport"] = b.Transport.ToString(),
                ["options"] = ToJson(b.Options),
                ["modules"] = new JsonArray(b.Modules
                    .Select(m => (JsonNode?)new JsonObject { ["id"] = m.Id.ToString(), ["name"] = m.Name })
                    .ToArray()),
                ["messages"] = MessagesToJson(b.Messages),
            };
            arr.Add(obj);
        }
        return arr;
    }

    private static JsonArray MessagesToJson(IReadOnlyList<Message> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages.OrderBy(m => m.Id.Value))
        {
            var obj = new JsonObject
            {
                ["id"] = m.Id.ToString(),
                ["name"] = m.Name,
            };
            if (m.WireId.HasValue) obj["wireId"] = m.WireId.Value;
            if (m.Description is not null) obj["description"] = m.Description;
            obj["options"] = ToJson(m.Options);
            if (m.Routes.Count > 0)
                obj["routes"] = new JsonArray(m.Routes
                    .Select(r => (JsonNode?)new JsonObject
                    {
                        ["from"] = r.From.ToString(),
                        ["to"] = r.To.ToString(),
                    })
                    .ToArray());
            obj["fields"] = FieldsToJson(m.Fields);
            arr.Add(obj);
        }
        return arr;
    }

    /// <summary>Fields keep declaration order — order IS wire order, so it must not be re-sorted here.</summary>
    private static JsonArray FieldsToJson(IReadOnlyList<FieldBinding> fields)
    {
        var arr = new JsonArray();
        foreach (var f in fields)
        {
            var obj = new JsonObject
            {
                ["id"] = f.Id.ToString(),
                ["name"] = f.Name,
                ["typeId"] = f.TypeId.ToString(),
                ["encoding"] = ToJson(f.Encoding),
            };
            if (f.DefaultValue is not null)
                obj["defaultValue"] = f.DefaultValue.ToString();
            if (f.Description is not null)
                obj["description"] = f.Description;
            arr.Add(obj);
        }
        return arr;
    }

    private static JsonObject ToJson(FieldEncoding encoding)
    {
        var obj = new JsonObject();
        if (encoding.BitWidth.HasValue) obj["bitWidth"] = encoding.BitWidth.Value;
        if (encoding.Endianness.HasValue) obj["endianness"] = encoding.Endianness.Value.ToString();
        if (encoding.BitOrder.HasValue) obj["bitOrder"] = encoding.BitOrder.Value.ToString();
        if (encoding.AllowBitPacking) obj["allowBitPacking"] = true;
        if (encoding.AlignmentBits.HasValue) obj["alignmentBits"] = encoding.AlignmentBits.Value;
        if (encoding.Transform is { } t)
        {
            obj["transform"] = new JsonObject
            {
                ["offset"] = t.Offset.ToString(Inv),
                ["scale"] = t.Scale.ToString(Inv),
            };
        }
        return obj;
    }

    private static JsonObject ToJson(LayoutOptions options)
    {
        var obj = new JsonObject();
        if (options.Endianness.HasValue) obj["endianness"] = options.Endianness.Value.ToString();
        if (options.BitOrder.HasValue) obj["bitOrder"] = options.BitOrder.Value.ToString();
        if (options.DefaultAlignmentBits.HasValue) obj["defaultAlignmentBits"] = options.DefaultAlignmentBits.Value;
        if (options.PackingMode.HasValue) obj["packingMode"] = options.PackingMode.Value.ToString();
        if (options.PadToByteBoundary.HasValue) obj["padToByteBoundary"] = options.PadToByteBoundary.Value;
        return obj;
    }

    // ---- from JSON ------------------------------------------------------------------------------

    public static Project FromJson(JsonObject doc)
    {
        var project = new Project(RequireString(doc, "name"))
        {
            SchemaVersion = doc["schemaVersion"]?.GetValue<int>() ?? Project.CurrentSchemaVersion,
        };

        ApplyOptions(project.Options, doc["options"] as JsonObject);

        if (doc["types"] is JsonObject typesObj)
        {
            foreach (var pair in typesObj)
            {
                if (pair.Value is not JsonObject typeObj) continue;
                var type = TypeFromJson(new TypeId(Guid.Parse(pair.Key)), typeObj);
                project.Types.Add(type);
            }
        }

        if (doc["buses"] is JsonArray busesArr)
        {
            foreach (var node in busesArr)
            {
                if (node is not JsonObject busObj) continue;
                project.Buses.Add(BusFromJson(busObj));
            }
        }

        return project;
    }

    private static TypeDefinition TypeFromJson(TypeId id, JsonObject obj)
    {
        var name = RequireString(obj, "name");
        var kind = RequireString(obj, "kind");
        var description = obj["description"]?.GetValue<string>();

        TypeDefinition type = kind switch
        {
            "parameter" => BuildParameter(),
            "enum" => BuildEnum(),
            "struct" => BuildStruct(),
            "array" => BuildArray(),
            _ => throw new NotSupportedException($"Unknown type kind '{kind}'."),
        };
        type.Description = description;
        return type;

        ParameterType BuildParameter()
        {
            var primitive = Enum.Parse<PrimitiveKind>(RequireString(obj, "primitive"));
            NumericRange? range = null;
            if (obj["rangeMin"] is not null && obj["rangeMax"] is not null)
            {
                var min = decimal.Parse(obj["rangeMin"]!.GetValue<string>(), Inv);
                var max = decimal.Parse(obj["rangeMax"]!.GetValue<string>(), Inv);
                range = new NumericRange(min, max);
            }
            var parameter = new ParameterType(id, name, primitive, range);
            if (obj["wireBits"] is JsonValue wb) parameter.WireBits = wb.GetValue<int>();
            // "Integer" was the pre-split name for what is now Unsigned; keep old files loading.
            if (obj["wireForm"] is JsonValue wf)
            {
                var raw = wf.GetValue<string>();
                parameter.WireForm = string.Equals(raw, "Integer", StringComparison.Ordinal)
                    ? WireForm.Unsigned
                    : Enum.Parse<WireForm>(raw);
            }
            if (obj["wireOffset"] is JsonValue wo) parameter.WireOffset = decimal.Parse(wo.GetValue<string>(), Inv);
            if (obj["wireScale"] is JsonValue ws) parameter.WireScale = decimal.Parse(ws.GetValue<string>(), Inv);
            return parameter;
        }

        EnumType BuildEnum()
        {
            var underlying = Enum.Parse<PrimitiveKind>(RequireString(obj, "underlying"));
            var isFlags = obj["isFlags"]?.GetValue<bool>() ?? false;
            var enumType = new EnumType(id, name, underlying, isFlags);
            if (obj["members"] is JsonArray members)
                foreach (var m in members.OfType<JsonObject>())
                    enumType.With(RequireString(m, "name"), m["value"]!.GetValue<long>());
            if (obj["wireBits"] is JsonValue ewb) enumType.WireBits = ewb.GetValue<int>();
            // "Integer" was the pre-split name for what is now Unsigned; keep old files loading.
            if (obj["wireForm"] is JsonValue ewf)
            {
                var raw = ewf.GetValue<string>();
                enumType.WireForm = string.Equals(raw, "Integer", StringComparison.Ordinal)
                    ? WireForm.Unsigned
                    : Enum.Parse<WireForm>(raw);
            }
            if (obj["wireOffset"] is JsonValue ewo) enumType.WireOffset = decimal.Parse(ewo.GetValue<string>(), Inv);
            if (obj["wireScale"] is JsonValue ews) enumType.WireScale = decimal.Parse(ews.GetValue<string>(), Inv);
            return enumType;
        }

        StructType BuildStruct()
        {
            var s = new StructType(id, name);
            if (obj["fields"] is JsonArray fields)
                foreach (var f in fields.OfType<JsonObject>())
                    s.Fields.Add(FieldFromJson(f));
            return s;
        }

        ArrayType BuildArray()
        {
            var elementTypeId = new TypeId(Guid.Parse(RequireString(obj, "elementTypeId")));
            var length = ArrayLengthFromJson((JsonObject)obj["length"]!);
            return new ArrayType(id, name, elementTypeId, length);
        }
    }

    private static ArrayLength ArrayLengthFromJson(JsonObject obj)
    {
        var kind = RequireString(obj, "kind");
        return kind switch
        {
            "fixed" => new ArrayLength.Fixed(obj["count"]!.GetValue<int>()),
            "countFromField" => new ArrayLength.CountFromField(
                new FieldId(Guid.Parse(RequireString(obj, "countFieldId"))),
                obj["maxCount"]!.GetValue<int>()),
            "lengthPrefixed" => new ArrayLength.LengthPrefixed(
                obj["prefixBits"]!.GetValue<int>(),
                obj["maxCount"]!.GetValue<int>()),
            "terminated" => new ArrayLength.Terminated(
                ((JsonArray)obj["sentinel"]!).Select(n => (byte)n!.GetValue<int>()).ToArray(),
                obj["maxCount"]!.GetValue<int>()),
            "fillRemaining" => new ArrayLength.FillRemaining(obj["maxCount"]!.GetValue<int>()),
            _ => throw new NotSupportedException($"Unknown array length kind '{kind}'."),
        };
    }

    private static Bus BusFromJson(JsonObject obj)
    {
        var id = new BusId(Guid.Parse(RequireString(obj, "id")));
        var name = RequireString(obj, "name");
        var transport = Enum.Parse<Transport>(RequireString(obj, "transport"));
        var bus = new Bus(id, name, transport);
        ApplyOptions(bus.Options, obj["options"] as JsonObject);

        if (obj["modules"] is JsonArray modules)
            foreach (var m in modules)
            {
                if (m is null) continue;
                // A file written before modules had identity stored them as bare strings. Mint an ID for
                // those; nothing referenced them yet, so nothing can break.
                if (m is JsonObject mo)
                    bus.Modules.Add(new Module(new ModuleId(Guid.Parse(RequireString(mo, "id"))), RequireString(mo, "name")));
                else
                    bus.Modules.Add(new Module(m.GetValue<string>()));
            }

        if (obj["messages"] is JsonArray messages)
            foreach (var m in messages.OfType<JsonObject>())
                bus.Messages.Add(MessageFromJson(m));

        return bus;
    }

    private static Message MessageFromJson(JsonObject obj)
    {
        var id = new MessageId(Guid.Parse(RequireString(obj, "id")));
        var message = new Message(id, RequireString(obj, "name"))
        {
            WireId = obj["wireId"]?.GetValue<int>(),
            Description = obj["description"]?.GetValue<string>(),
        };
        ApplyOptions(message.Options, obj["options"] as JsonObject);

        if (obj["routes"] is JsonArray routes)
            foreach (var r in routes.OfType<JsonObject>())
                message.Routes.Add(new MessageRoute(
                    new ModuleId(Guid.Parse(RequireString(r, "from"))),
                    new ModuleId(Guid.Parse(RequireString(r, "to")))));

        if (obj["fields"] is JsonArray fields)
            foreach (var f in fields.OfType<JsonObject>())
                message.Fields.Add(FieldFromJson(f));
        return message;
    }

    private static FieldBinding FieldFromJson(JsonObject obj)
    {
        var id = new FieldId(Guid.Parse(RequireString(obj, "id")));
        var name = RequireString(obj, "name");
        var typeId = new TypeId(Guid.Parse(RequireString(obj, "typeId")));
        var encoding = EncodingFromJson(obj["encoding"] as JsonObject);
        var field = new FieldBinding(id, name, typeId, encoding)
        {
            Description = obj["description"]?.GetValue<string>(),
        };

        if (obj["defaultValue"] is JsonValue dv)
        {
            var text = dv.GetValue<string>();
            if (decimal.TryParse(text, NumberStyles.Any, Inv, out var d))
                field.DefaultValue = d;
            else
                field.DefaultValue = text;
        }

        return field;
    }

    private static FieldEncoding EncodingFromJson(JsonObject? obj)
    {
        var encoding = FieldEncoding.Natural();
        if (obj is null) return encoding;

        if (obj["bitWidth"] is JsonValue bw) encoding.BitWidth = bw.GetValue<int>();
        if (obj["endianness"] is JsonValue e) encoding.Endianness = Enum.Parse<Endianness>(e.GetValue<string>());
        if (obj["bitOrder"] is JsonValue bo) encoding.BitOrder = Enum.Parse<BitOrder>(bo.GetValue<string>());
        if (obj["allowBitPacking"] is JsonValue abp) encoding.AllowBitPacking = abp.GetValue<bool>();
        if (obj["alignmentBits"] is JsonValue a) encoding.AlignmentBits = a.GetValue<int>();
        if (obj["transform"] is JsonObject t)
        {
            var offset = decimal.Parse(t["offset"]!.GetValue<string>(), Inv);
            var scale = decimal.Parse(t["scale"]!.GetValue<string>(), Inv);
            encoding.Transform = new ScalarTransform(offset, scale);
        }
        return encoding;
    }

    private static void ApplyOptions(LayoutOptions options, JsonObject? obj)
    {
        if (obj is null) return;
        if (obj["endianness"] is JsonValue e) options.Endianness = Enum.Parse<Endianness>(e.GetValue<string>());
        if (obj["bitOrder"] is JsonValue bo) options.BitOrder = Enum.Parse<BitOrder>(bo.GetValue<string>());
        if (obj["defaultAlignmentBits"] is JsonValue a) options.DefaultAlignmentBits = a.GetValue<int>();
        if (obj["packingMode"] is JsonValue pm) options.PackingMode = Enum.Parse<BitPackingMode>(pm.GetValue<string>());
        if (obj["padToByteBoundary"] is JsonValue pad) options.PadToByteBoundary = pad.GetValue<bool>();
    }

    private static string RequireString(JsonObject obj, string key) =>
        obj[key]?.GetValue<string>() ?? throw new FormatException($"Missing required '{key}' property.");
}
