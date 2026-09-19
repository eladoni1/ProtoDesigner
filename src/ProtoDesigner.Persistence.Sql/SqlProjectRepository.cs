using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProtoDesigner.Application;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Persistence.Sql;

/// <summary>
/// Stores projects as rows in a SQL database. The shared backend the JSON file is the single-user
/// equivalent of.
/// </summary>
/// <remarks>
/// <para>
/// The database is the store and is named once, in the constructor; the <c>path</c> the port passes is
/// the <b>key of one project inside it</b>. That is the difference a shared backend makes — one store
/// holds everyone's projects, where one file holds one.
/// </para>
/// <para>
/// <b>A save replaces that project's rows, in one transaction.</b> Writing only what changed needs a
/// per-entity revision to compare against, which is the next piece of Phase 6 and not this one; until
/// then, replacing wholesale is the version that cannot leave a half-written project behind. The
/// transaction is what makes it safe to say that.
/// </para>
/// <para>
/// It is deliberately plain ADO.NET rather than
/// reusing the JSON serializer. Two implementations of one port are only worth having if they are
/// independent — sharing the mapping would mean a bug in it passed both of their tests.
/// </para>
/// <para>
/// SQLite is the engine because it needs no server and runs in the test suite. The SQL is ordinary
/// enough that moving to Postgres is a connection string and a dialect pass, which is the point at which
/// a genuinely concurrent deployment would want to be — SQLite over a network share is not that.
/// </para>
/// </remarks>
public sealed class SqlProjectRepository : IProjectRepository
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _connectionString;

    /// <summary>Opens (and creates, if absent) the store at <paramref name="connectionString"/>.</summary>
    public SqlProjectRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;

        using var connection = Open();
        Execute(connection, Schema.Create);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // ---- save ---------------------------------------------------------------------------------

    public void Save(Project project, string projectKey)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(projectKey);

        using var connection = Open();
        using var tx = connection.BeginTransaction();

        foreach (var table in Schema.Tables)
            Execute(connection, $"DELETE FROM {table} WHERE project_key = $k", ("$k", projectKey));

        WriteProject(connection, projectKey, project);
        foreach (var type in project.Types.All) WriteType(connection, projectKey, type);

        for (var b = 0; b < project.Buses.Count; b++)
            WriteBus(connection, projectKey, project.Buses[b], b);

        tx.Commit();
    }

    private static void WriteProject(SqliteConnection c, string key, Project p) => Execute(c,
        """
        INSERT INTO projects (project_key, name, schema_version,
                              endianness, bit_order, alignment_bits, packing_mode, pad_to_byte)
        VALUES ($k, $name, $ver, $end, $bit, $align, $pack, $pad)
        """,
        Concat(new (string, object?)[] { ("$k", key), ("$name", p.Name), ("$ver", p.SchemaVersion) },
               Options(p.Options)));

    private static void WriteType(SqliteConnection c, string key, TypeDefinition type)
    {
        var (kind, extra) = type switch
        {
            ParameterType p => ("parameter", new (string, object?)[]
            {
                ("$prim", p.Kind.ToString()),
                ("$min", Text(p.Range?.Min)), ("$max", Text(p.Range?.Max)),
                ("$wbits", p.WireBits), ("$wform", p.WireForm.ToString()),
                ("$woff", Text(p.WireOffset)), ("$wscale", Text(p.WireScale)),
            }),
            EnumType e => ("enum", new (string, object?)[]
            {
                ("$prim", e.UnderlyingKind.ToString()),
                ("$wbits", e.WireBits), ("$wform", e.WireForm.ToString()),
                ("$woff", Text(e.WireOffset)), ("$wscale", Text(e.WireScale)),
                ("$flags", e.IsFlags ? 1 : 0), ("$syn", e.Synthetic.ToString()),
            }),
            StructType => ("struct", Array.Empty<(string, object?)>()),
            ArrayType a => ("array", new (string, object?)[]
            {
                ("$elem", a.ElementTypeId.ToString()),
                ("$len", ArrayLengthToJson(a.Length).ToJsonString()),
            }),
            _ => throw new NotSupportedException($"Unknown type kind '{type.GetType().Name}'."),
        };

        Execute(c,
            """
            INSERT INTO types (project_key, id, kind, name, description,
                               primitive_kind, range_min, range_max,
                               wire_bits, wire_form, wire_offset, wire_scale,
                               is_flags, synthetic, element_type_id, array_length)
            VALUES ($k, $id, $kind, $name, $desc,
                    $prim, $min, $max, $wbits, $wform, $woff, $wscale, $flags, $syn, $elem, $len)
            """,
            Concat(new (string, object?)[]
            {
                ("$k", key), ("$id", type.Id.ToString()), ("$kind", kind),
                ("$name", type.Name), ("$desc", type.Description),
                // Every named parameter must be supplied, so the ones this kind does not use are null.
                ("$prim", null), ("$min", null), ("$max", null),
                ("$wbits", null), ("$wform", null), ("$woff", null), ("$wscale", null),
                ("$flags", null), ("$syn", null), ("$elem", null), ("$len", null),
            }, extra));

        if (type is EnumType en)
        {
            // A synthetic enum's members come from the bus at generation. Storing them is the one way
            // the type could go stale, which is exactly what it exists to prevent.
            if (en.Synthetic != SyntheticEnum.None) return;

            for (var i = 0; i < en.Members.Count; i++)
                Execute(c,
                    "INSERT INTO enum_members (project_key, type_id, ordinal, name, value) " +
                    "VALUES ($k, $t, $o, $n, $v)",
                    ("$k", key), ("$t", en.Id.ToString()), ("$o", i),
                    ("$n", en.Members[i].Name), ("$v", en.Members[i].Value));
        }

        if (type is StructType s) WriteFields(c, key, s.Id.ToString(), s.Fields);
    }

    private static void WriteBus(SqliteConnection c, string key, Bus bus, int ordinal)
    {
        Execute(c,
            """
            INSERT INTO buses (project_key, id, ordinal, name, transport,
                               endianness, bit_order, alignment_bits, packing_mode, pad_to_byte)
            VALUES ($k, $id, $o, $name, $transport, $end, $bit, $align, $pack, $pad)
            """,
            Concat(new (string, object?)[]
                   {
                       ("$k", key), ("$id", bus.Id.ToString()), ("$o", ordinal),
                       ("$name", bus.Name), ("$transport", bus.Transport.ToString()),
                   },
                   Options(bus.Options)));

        for (var i = 0; i < bus.Modules.Count; i++)
            Execute(c,
                "INSERT INTO modules (project_key, bus_id, id, ordinal, name) VALUES ($k, $b, $id, $o, $n)",
                ("$k", key), ("$b", bus.Id.ToString()), ("$id", bus.Modules[i].Id.ToString()),
                ("$o", i), ("$n", bus.Modules[i].Name));

        for (var i = 0; i < bus.Messages.Count; i++)
        {
            var m = bus.Messages[i];
            Execute(c,
                """
                INSERT INTO messages (project_key, bus_id, id, ordinal, name, wire_id, description,
                                      endianness, bit_order, alignment_bits, packing_mode, pad_to_byte)
                VALUES ($k, $b, $id, $o, $name, $wire, $desc, $end, $bit, $align, $pack, $pad)
                """,
                Concat(new (string, object?)[]
                       {
                           ("$k", key), ("$b", bus.Id.ToString()), ("$id", m.Id.ToString()), ("$o", i),
                           ("$name", m.Name), ("$wire", m.WireId), ("$desc", m.Description),
                       },
                       Options(m.Options)));

            for (var r = 0; r < m.Routes.Count; r++)
                Execute(c,
                    "INSERT INTO routes (project_key, message_id, ordinal, from_module, to_module) " +
                    "VALUES ($k, $m, $o, $f, $t)",
                    ("$k", key), ("$m", m.Id.ToString()), ("$o", r),
                    ("$f", m.Routes[r].From.ToString()), ("$t", m.Routes[r].To.ToString()));

            WriteFields(c, key, m.Id.ToString(), m.Fields);
        }
    }

    private static void WriteFields(SqliteConnection c, string key, string ownerId, IReadOnlyList<FieldBinding> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var e = f.Encoding;
            Execute(c,
                """
                INSERT INTO fields (project_key, owner_id, ordinal, id, name, type_id, description,
                                    default_value, proto_field_number,
                                    bit_width, endianness, bit_order, allow_bit_packing, alignment_bits,
                                    transform_offset, transform_scale)
                VALUES ($k, $owner, $o, $id, $name, $type, $desc, $default, $proto,
                        $width, $end, $bit, $pack, $align, $toff, $tscale)
                """,
                ("$k", key), ("$owner", ownerId), ("$o", i), ("$id", f.Id.ToString()),
                ("$name", f.Name), ("$type", f.TypeId.ToString()), ("$desc", f.Description),
                // Mirrors the file format: written as text, read back as a decimal when it parses as
                // one. The two implementations must agree, or a project moved between them would look
                // edited to the merge.
                ("$default", f.DefaultValue?.ToString()), ("$proto", f.ProtoFieldNumber),
                ("$width", e.BitWidth), ("$end", e.Endianness?.ToString()), ("$bit", e.BitOrder?.ToString()),
                ("$pack", e.AllowBitPacking ? 1 : 0), ("$align", e.AlignmentBits),
                ("$toff", Text(e.Transform?.Offset)), ("$tscale", Text(e.Transform?.Scale)));
        }
    }

    // ---- load ---------------------------------------------------------------------------------

    public Project Load(string projectKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectKey);

        using var connection = Open();

        using var head = Query(connection,
            "SELECT name, schema_version, endianness, bit_order, alignment_bits, packing_mode, pad_to_byte " +
            "FROM projects WHERE project_key = $k", ("$k", projectKey));

        if (!head.Read())
            throw new KeyNotFoundException($"No project stored under '{projectKey}'.");

        var project = new Project(head.GetString(0)) { SchemaVersion = head.GetInt32(1) };
        ReadOptions(project.Options, head, 2);
        head.Close();

        ReadTypes(connection, projectKey, project);
        ReadBuses(connection, projectKey, project);
        return project;
    }

    private static void ReadTypes(SqliteConnection c, string key, Project project)
    {
        var structFields = new List<StructType>();

        using (var r = Query(c,
            "SELECT id, kind, name, description, primitive_kind, range_min, range_max, " +
            "       wire_bits, wire_form, wire_offset, wire_scale, is_flags, synthetic, " +
            "       element_type_id, array_length " +
            "FROM types WHERE project_key = $k", ("$k", key)))
        {
            while (r.Read())
            {
                var id = new TypeId(Guid.Parse(r.GetString(0)));
                var name = r.GetString(2);
                var range = Dec(r, 5) is { } min && Dec(r, 6) is { } max ? new NumericRange(min, max) : (NumericRange?)null;

                TypeDefinition type = r.GetString(1) switch
                {
                    "parameter" => new ParameterType(id, name, En<PrimitiveKind>(r, 4)!.Value, range)
                    {
                        // After construction: the constructor derives a WireForm from the kind, and the
                        // stored one is the user's choice.
                        WireBits = Int(r, 7), WireForm = En<WireForm>(r, 8)!.Value,
                        WireOffset = Dec(r, 9), WireScale = Dec(r, 10),
                    },
                    "enum" => new EnumType(id, name, En<PrimitiveKind>(r, 4)!.Value, Int(r, 11) == 1)
                    {
                        Synthetic = En<SyntheticEnum>(r, 12)!.Value,
                        WireBits = Int(r, 7), WireForm = En<WireForm>(r, 8)!.Value,
                        WireOffset = Dec(r, 9), WireScale = Dec(r, 10),
                    },
                    "struct" => new StructType(id, name),
                    "array" => new ArrayType(id, name, new TypeId(Guid.Parse(r.GetString(13))),
                                             ArrayLengthFromJson(r.GetString(14))),
                    var k => throw new NotSupportedException($"Unknown stored type kind '{k}'."),
                };

                type.Description = Str(r, 3);
                project.Types.Add(type);
                if (type is StructType s) structFields.Add(s);
            }
        }

        foreach (var type in project.Types.All.OfType<EnumType>())
        {
            if (type.Synthetic != SyntheticEnum.None) continue;   // derived, never stored
            using var r = Query(c,
                "SELECT name, value FROM enum_members WHERE project_key = $k AND type_id = $t ORDER BY ordinal",
                ("$k", key), ("$t", type.Id.ToString()));
            while (r.Read()) type.With(r.GetString(0), r.GetInt64(1));
        }

        foreach (var s in structFields)
            s.Fields.AddRange(ReadFields(c, key, s.Id.ToString()));
    }

    private static void ReadBuses(SqliteConnection c, string key, Project project)
    {
        var buses = new List<Bus>();

        using (var r = Query(c,
            "SELECT id, name, transport, endianness, bit_order, alignment_bits, packing_mode, pad_to_byte " +
            "FROM buses WHERE project_key = $k ORDER BY ordinal", ("$k", key)))
        {
            while (r.Read())
            {
                var bus = new Bus(new BusId(Guid.Parse(r.GetString(0))), r.GetString(1),
                                  En<Transport>(r, 2)!.Value);
                ReadOptions(bus.Options, r, 3);
                buses.Add(bus);
            }
        }

        foreach (var bus in buses)
        {
            project.Buses.Add(bus);

            using (var r = Query(c,
                "SELECT id, name FROM modules WHERE project_key = $k AND bus_id = $b ORDER BY ordinal",
                ("$k", key), ("$b", bus.Id.ToString())))
            {
                while (r.Read())
                    bus.Modules.Add(new Module(new ModuleId(Guid.Parse(r.GetString(0))), r.GetString(1)));
            }

            var messages = new List<Message>();
            using (var r = Query(c,
                "SELECT id, name, wire_id, description, endianness, bit_order, alignment_bits, " +
                "       packing_mode, pad_to_byte " +
                "FROM messages WHERE project_key = $k AND bus_id = $b ORDER BY ordinal",
                ("$k", key), ("$b", bus.Id.ToString())))
            {
                while (r.Read())
                {
                    var message = new Message(new MessageId(Guid.Parse(r.GetString(0))), r.GetString(1))
                    {
                        WireId = Int(r, 2),
                        Description = Str(r, 3),
                    };
                    ReadOptions(message.Options, r, 4);
                    messages.Add(message);
                }
            }

            foreach (var message in messages)
            {
                bus.Messages.Add(message);
                message.Fields.AddRange(ReadFields(c, key, message.Id.ToString()));

                using var r = Query(c,
                    "SELECT from_module, to_module FROM routes " +
                    "WHERE project_key = $k AND message_id = $m ORDER BY ordinal",
                    ("$k", key), ("$m", message.Id.ToString()));
                while (r.Read())
                    message.Routes.Add(new MessageRoute(new ModuleId(Guid.Parse(r.GetString(0))),
                                                        new ModuleId(Guid.Parse(r.GetString(1)))));
            }
        }
    }

    private static List<FieldBinding> ReadFields(SqliteConnection c, string key, string ownerId)
    {
        var fields = new List<FieldBinding>();
        using var r = Query(c,
            "SELECT id, name, type_id, description, default_value, proto_field_number, " +
            "       bit_width, endianness, bit_order, allow_bit_packing, alignment_bits, " +
            "       transform_offset, transform_scale " +
            "FROM fields WHERE project_key = $k AND owner_id = $o ORDER BY ordinal",
            ("$k", key), ("$o", ownerId));

        while (r.Read())
        {
            var encoding = new FieldEncoding
            {
                BitWidth = Int(r, 6),
                Endianness = En<Endianness>(r, 7),
                BitOrder = En<BitOrder>(r, 8),
                AllowBitPacking = Int(r, 9) == 1,
                AlignmentBits = Int(r, 10),
                Transform = Dec(r, 11) is { } off && Dec(r, 12) is { } scale
                    ? new ScalarTransform(off, scale)
                    : null,
            };

            fields.Add(new FieldBinding(new FieldId(Guid.Parse(r.GetString(0))), r.GetString(1),
                                        new TypeId(Guid.Parse(r.GetString(2))), encoding)
            {
                Description = Str(r, 3),
                DefaultValue = DefaultValue(Str(r, 4)),
                ProtoFieldNumber = Int(r, 5),
            });
        }
        return fields;
    }

    /// <summary>Mirrors the file format: a decimal when it parses as one, otherwise the text.</summary>
    private static object? DefaultValue(string? stored) =>
        stored is null ? null
        : decimal.TryParse(stored, NumberStyles.Any, Inv, out var d) ? d
        : stored;

    // ---- the five layout options, written and read as one group -------------------------------

    private static (string, object?)[] Options(LayoutOptions o) => new (string, object?)[]
    {
        ("$end", o.Endianness?.ToString()), ("$bit", o.BitOrder?.ToString()),
        ("$align", o.DefaultAlignmentBits), ("$pack", o.PackingMode?.ToString()),
        ("$pad", o.PadToByteBoundary is { } b ? (b ? 1 : 0) : null),
    };

    private static void ReadOptions(LayoutOptions o, SqliteDataReader r, int first)
    {
        o.Endianness = En<Endianness>(r, first);
        o.BitOrder = En<BitOrder>(r, first + 1);
        o.DefaultAlignmentBits = Int(r, first + 2);
        o.PackingMode = En<BitPackingMode>(r, first + 3);
        o.PadToByteBoundary = Int(r, first + 4) is { } v ? v == 1 : null;
    }

    // ---- ArrayLength, the one union ----------------------------------------------------------

    private static JsonObject ArrayLengthToJson(ArrayLength length)
    {
        var obj = length switch
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
                ["kind"] = "lengthPrefixed", ["prefixBits"] = l.PrefixBits, ["maxCount"] = l.MaxCount,
            },
            ArrayLength.Terminated t => new JsonObject
            {
                ["kind"] = "terminated",
                ["sentinel"] = Convert.ToHexString(t.Sentinel.ToArray()),
                ["maxCount"] = t.MaxCount,
            },
            ArrayLength.FillRemaining r => new JsonObject { ["kind"] = "fillRemaining", ["maxCount"] = r.MaxCount },
            _ => throw new NotSupportedException($"Unknown array length '{length.GetType().Name}'."),
        };

        // Fixed carries its minimum in `count`; restating it would be two sources for one number.
        if (length is not ArrayLength.Fixed && length.MinimumCount != 0)
            obj["minCount"] = length.MinimumCount;

        return obj;
    }

    private static ArrayLength ArrayLengthFromJson(string json)
    {
        var obj = JsonNode.Parse(json)!.AsObject();
        int Num(string key) => obj[key] is JsonValue v ? v.GetValue<int>() : 0;

        var min = Num("minCount");
        var max = Num("maxCount");

        return obj["kind"]!.GetValue<string>() switch
        {
            "fixed" => new ArrayLength.Fixed(Num("count")),
            "countFromField" => new ArrayLength.CountFromField(
                new FieldId(Guid.Parse(obj["countFieldId"]!.GetValue<string>())), max, min),
            "lengthPrefixed" => new ArrayLength.LengthPrefixed(Num("prefixBits"), max, min),
            "terminated" => new ArrayLength.Terminated(
                Convert.FromHexString(obj["sentinel"]!.GetValue<string>()), max, min),
            "fillRemaining" => new ArrayLength.FillRemaining(max, min),
            var k => throw new NotSupportedException($"Unknown stored array length '{k}'."),
        };
    }

    // ---- ADO plumbing --------------------------------------------------------------------------

    private static void Execute(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static SqliteDataReader Query(SqliteConnection c, string sql,
                                          params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteReader();
    }

    /// <summary>
    /// One parameter list from several. Later entries win, so a type kind's own values replace the
    /// nulls declared for every column it does not use.
    /// </summary>
    private static (string, object?)[] Concat(params (string, object?)[][] groups)
    {
        var byName = new Dictionary<string, object?>();
        foreach (var group in groups)
            foreach (var (name, value) in group) byName[name] = value;
        return byName.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    private static string? Text(decimal? value) => value?.ToString(Inv);

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static decimal? Dec(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : decimal.Parse(r.GetString(i), NumberStyles.Any, Inv);
    private static T? En<T>(SqliteDataReader r, int i) where T : struct, Enum =>
        r.IsDBNull(i) ? null : Enum.Parse<T>(r.GetString(i));
}
