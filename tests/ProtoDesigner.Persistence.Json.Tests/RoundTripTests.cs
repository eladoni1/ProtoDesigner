namespace ProtoDesigner.Persistence.Json.Tests;

public class RoundTripTests
{
    [Fact]
    public void An_empty_project_survives_a_round_trip()
    {
        var original = new Project("Empty");
        var text = JsonProjectRepository.SaveToString(original);
        var reloaded = JsonProjectRepository.LoadFromString(text);

        Assert.Equal(original.Name, reloaded.Name);
        Assert.Empty(reloaded.Buses);
        Assert.Empty(reloaded.Types.All);
    }

    [Fact]
    public void A_realistic_project_survives_a_round_trip()
    {
        var original = BuildSample();
        var text = JsonProjectRepository.SaveToString(original);
        var reloaded = JsonProjectRepository.LoadFromString(text);

        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(original.Types.Count, reloaded.Types.Count);
        Assert.Equal(original.Buses.Count, reloaded.Buses.Count);
        Assert.Equal(original.Buses[0].Messages.Count, reloaded.Buses[0].Messages.Count);

        var originalMsg = original.Buses[0].Messages[0];
        var reloadedMsg = reloaded.Buses[0].Messages.Single(m => m.Id == originalMsg.Id);
        Assert.Equal(originalMsg.Name, reloadedMsg.Name);
        Assert.Equal(originalMsg.WireId, reloadedMsg.WireId);
        Assert.Equal(originalMsg.Fields.Count, reloadedMsg.Fields.Count);
        for (var i = 0; i < originalMsg.Fields.Count; i++)
        {
            Assert.Equal(originalMsg.Fields[i].Id, reloadedMsg.Fields[i].Id);
            Assert.Equal(originalMsg.Fields[i].Name, reloadedMsg.Fields[i].Name);
        }
    }

    // Modules carry identity, so a route must come back pointing at the very same module — not at one
    // that merely happens to share a name.
    [Fact]
    public void Modules_and_routes_survive_a_round_trip_by_identity()
    {
        var original = BuildSample();
        var reloaded = JsonProjectRepository.LoadFromString(JsonProjectRepository.SaveToString(original));

        var originalBus = original.Buses[0];
        var reloadedBus = reloaded.Buses.Single(b => b.Id == originalBus.Id);

        Assert.Equal(
            originalBus.Modules.Select(m => (m.Id, m.Name)),
            reloadedBus.Modules.Select(m => (m.Id, m.Name)));

        var originalMsg = originalBus.Messages[0];
        var reloadedMsg = reloadedBus.Messages.Single(m => m.Id == originalMsg.Id);
        Assert.Equal(originalMsg.Routes, reloadedMsg.Routes);

        var from = reloadedBus.FindModule(reloadedMsg.Routes[0].From);
        Assert.NotNull(from);
        Assert.Equal("Sensor", from!.Name);
    }

    // A file written before modules had identity stored them as bare strings.
    [Fact]
    public void Modules_written_as_bare_strings_still_load()
    {
        var legacy = $$"""
        {
          "schemaVersion": {{Project.CurrentSchemaVersion}},
          "name": "Legacy",
          "options": {},
          "types": {},
          "buses": [
            {
              "id": "{{Guid.NewGuid()}}",
              "name": "Main",
              "transport": "Ethernet",
              "options": {},
              "modules": ["Sensor", "Controller"],
              "messages": []
            }
          ]
        }
        """;

        var project = JsonProjectRepository.LoadFromString(legacy);

        Assert.Equal(new[] { "Sensor", "Controller" }, project.Buses[0].Modules.Select(m => m.Name));
        Assert.All(project.Buses[0].Modules, m => Assert.NotEqual(default, m.Id));
    }

    [Fact]
    public void Save_is_deterministic_for_the_same_model()
    {
        var project = BuildSample();
        var first = JsonProjectRepository.SaveToString(project);
        var second = JsonProjectRepository.SaveToString(project);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Reordering_unrelated_types_does_not_change_the_file()
    {
        var project = BuildSample();
        var before = JsonProjectRepository.SaveToString(project);

        // types are keyed by id and emitted in id order; adding an unrelated type shifts nothing that
        // was there before, so a diff between the two files is one added block, not a full rewrite.
        var extra = new ParameterType(TypeId.New(), "Zeta", PrimitiveKind.U8);
        project.Types.Add(extra);
        var after = JsonProjectRepository.SaveToString(project);

        Assert.NotEqual(before, after);

        // remove the added block and the two files must be identical
        var extraId = extra.Id.ToString();
        var lines = after.Split('\n');
        var idx = Array.FindIndex(lines, l => l.Contains($"\"{extraId}\""));
        Assert.True(idx > 0, "Expected added type block to be present.");
        // remove the entry block: from "<id>": { ... } and its closing brace
        var end = idx;
        var depth = 0;
        for (var i = idx; i < lines.Length; i++)
        {
            depth += lines[i].Count(c => c == '{');
            depth -= lines[i].Count(c => c == '}');
            if (depth == 0) { end = i; break; }
        }
        // remove trailing comma from prior line if we removed the last entry
        var stripped = lines.Take(idx).Concat(lines.Skip(end + 1)).ToArray();
        var withoutExtra = string.Join('\n', stripped);
        // strip a possibly-orphaned trailing comma just before the closing brace of "types"
        withoutExtra = withoutExtra.Replace(",\n  },", "\n  },");
        Assert.Equal(NormalizeTrailingCommas(before), NormalizeTrailingCommas(withoutExtra));
    }

    private static string NormalizeTrailingCommas(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @",(\s*[}\]])", "$1");

    [Fact]
    public void The_written_schema_version_matches_the_current()
    {
        var text = JsonProjectRepository.SaveToString(new Project("X"));
        Assert.Contains($"\"schemaVersion\": {Project.CurrentSchemaVersion}", text);
    }

    [Fact]
    public void A_project_persisted_at_a_future_version_is_rejected()
    {
        var future = $$"""
        {
          "schemaVersion": {{Project.CurrentSchemaVersion + 100}},
          "name": "TooNew",
          "options": {},
          "types": {},
          "buses": []
        }
        """;
        Assert.Throws<NotSupportedException>(() => JsonProjectRepository.LoadFromString(future));
    }

    [Fact]
    public void Save_and_load_via_disk_preserves_the_model()
    {
        var project = BuildSample();
        var path = Path.Combine(Path.GetTempPath(), $"protodesigner-test-{Guid.NewGuid():N}.pdproj");

        try
        {
            var repo = new JsonProjectRepository();
            repo.Save(project, path);
            var reloaded = repo.Load(path);

            Assert.Equal(project.Name, reloaded.Name);
            Assert.Equal(project.Types.Count, reloaded.Types.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- array minimums ----------------------------------------------------------------------------

    [Fact]
    public void A_declared_array_minimum_survives_a_round_trip()
    {
        var project = new Project("Bounds");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var count = new FieldBinding("count", u8.Id);
        var payload = project.Types.Add(new ArrayType(TypeId.New(), "Payload", u8.Id,
            new ArrayLength.CountFromField(count.Id, MaxCount: 10, MinCount: 1)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "M") { WireId = 1 };
        message.Fields.Add(count);
        message.Fields.Add(new FieldBinding("payload", payload.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var reloaded = JsonProjectRepository.LoadFromString(JsonProjectRepository.SaveToString(project));

        var length = Assert.IsType<ArrayLength.CountFromField>(
            reloaded.Types.All.OfType<ArrayType>().Single().Length);
        Assert.Equal(1, length.MinCount);
        Assert.Equal(10, length.MaxCount);
    }

    [Fact]
    public void An_array_with_no_declared_minimum_writes_no_minCount_key()
    {
        // Omitting the default is what keeps every existing project byte-identical after a round trip.
        // Writing `minCount: 0` everywhere would put an unrelated diff in front of every reviewer the
        // first time they opened a file after upgrading.
        var json = JsonProjectRepository.SaveToString(BuildSample());

        Assert.DoesNotContain("minCount", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_written_before_minCount_existed_loads_with_no_minimum()
    {
        // Backward compatibility without a schema bump: an added optional key means an older file simply
        // means what it always meant.
        var project = new Project("Old");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var payload = project.Types.Add(new ArrayType(TypeId.New(), "Payload", u8.Id,
            new ArrayLength.LengthPrefixed(PrefixBits: 8, MaxCount: 16)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "M") { WireId = 1 };
        message.Fields.Add(new FieldBinding("payload", payload.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var json = JsonProjectRepository.SaveToString(project);
        Assert.DoesNotContain("minCount", json, StringComparison.Ordinal);

        var reloaded = JsonProjectRepository.LoadFromString(json);
        Assert.Equal(0, reloaded.Types.All.OfType<ArrayType>().Single().Length.MinimumCount);
    }


    /// <summary>
    /// An alignment policy set at the bus or project level survives a round trip.
    /// </summary>
    /// <remarks>
    /// Both levels are written only when set, like every other nullable option, so the risk is not a
    /// mangled value but a silently dropped one: a project that came back byte-aligned would lay every
    /// message out differently from the one that was saved, and nothing would say so.
    /// </remarks>
    [Fact]
    public void An_alignment_policy_survives_a_round_trip_at_every_level()
    {
        var project = new Project("Align") { Options = { DefaultAlignmentBits = 64 } };
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));

        var message = new Message(MessageId.New(), "M") { WireId = 1 };
        message.Options.DefaultAlignmentBits = 16;
        message.Fields.Add(new FieldBinding(FieldId.New(), "a", u8.Id,
            new FieldEncoding { AlignmentBits = 32 }));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        bus.Options.DefaultAlignmentBits = 32;
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var reloaded = JsonProjectRepository.LoadFromString(JsonProjectRepository.SaveToString(project));
        var loadedBus = reloaded.Buses.Single();
        var loadedMessage = loadedBus.Messages.Single();

        Assert.Equal(64, reloaded.Options.DefaultAlignmentBits);
        Assert.Equal(32, loadedBus.Options.DefaultAlignmentBits);
        Assert.Equal(16, loadedMessage.Options.DefaultAlignmentBits);
        Assert.Equal(32, loadedMessage.Fields.Single().Encoding.AlignmentBits);

        // And the chain still resolves to the innermost answer after the trip.
        Assert.Equal(16, reloaded.OptionsFor(loadedBus, loadedMessage).DefaultAlignmentBits);
    }

    [Fact]
    public void A_project_with_no_alignment_policy_writes_no_alignment_keys()
    {
        // Same reason minCount is omitted: every file predating a nullable option round-trips unchanged.
        var json = JsonProjectRepository.SaveToString(BuildSample());

        Assert.DoesNotContain("defaultAlignmentBits", json, StringComparison.Ordinal);
    }

    // ---- the built-in id enums -------------------------------------------------------------------

    /// <summary>
    /// The marker is declarative intent — the user chose this field to be "the message id" — so it
    /// persists. The member list never does: it is derived per bus at generation, which is the whole
    /// reason renaming a message cannot leave a stale list behind.
    /// </summary>
    [Fact]
    public void A_synthetic_enum_persists_its_marker_and_never_its_members()
    {
        var project = new Project("Ids");
        project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U32)
        {
            Synthetic = SyntheticEnum.MessageId,
            WireBits = 32,
        });

        var json = JsonProjectRepository.SaveToString(project);
        Assert.Contains("\"synthetic\": \"MessageId\"", json, StringComparison.Ordinal);

        var reloaded = JsonProjectRepository.LoadFromString(json);
        var loaded = reloaded.Types.All.OfType<EnumType>().Single();

        Assert.Equal(SyntheticEnum.MessageId, loaded.Synthetic);
        Assert.Empty(loaded.Members);
    }

    /// <summary>
    /// Even if a stale member list somehow reached a file, loading must not resurrect it — the bus is the
    /// only source, and a stored list is exactly the staleness this type exists to remove.
    /// </summary>
    [Fact]
    public void Members_written_into_a_synthetic_enum_do_not_survive_a_round_trip()
    {
        var project = new Project("Ids");
        var ids = project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U32)
        {
            Synthetic = SyntheticEnum.MessageId,
        });
        ids.With("Stale", 3);

        var json = JsonProjectRepository.SaveToString(project);
        var reloaded = JsonProjectRepository.LoadFromString(json);

        Assert.Empty(reloaded.Types.All.OfType<EnumType>().Single().Members);
        Assert.DoesNotContain("Stale", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project saved before the built-in ids existed carries no marker, so it loads as the ordinary
    /// enum it always was. The key is written only when set, which is what let these arrive with no
    /// schema bump.
    /// </summary>
    [Fact]
    public void A_file_written_before_the_built_in_ids_existed_loads_without_them()
    {
        var project = new Project("Old");
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U8));
        mode.With("Idle", 0).With("Run", 1);

        var json = JsonProjectRepository.SaveToString(project);
        Assert.DoesNotContain("synthetic", json, StringComparison.Ordinal);

        var reloaded = JsonProjectRepository.LoadFromString(json);
        var loaded = reloaded.Types.All.OfType<EnumType>().Single();

        Assert.Equal(SyntheticEnum.None, loaded.Synthetic);
        Assert.Equal(new[] { "Idle", "Run" }, loaded.Members.Select(m => m.Name));
    }

    private static Project BuildSample()
    {
        var project = new Project("Sample");
        project.Options.Endianness = Endianness.Big;

        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = project.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));
        var temperature = project.Types.Add(new ParameterType(TypeId.New(), "Temperature", PrimitiveKind.U16, new NumericRange(1000, 1015)));
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
            .With("Idle", 0)
            .With("Running", 5)
            .With("Fault", 10));
        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding("id", u8.Id), new FieldBinding("flags", u8.Id)));
        var samples = project.Types.Add(new ArrayType(TypeId.New(), "Samples", u16.Id, new ArrayLength.Fixed(4)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var sensor = bus.AddModule("Sensor");
        var controller = bus.AddModule("Controller");

        var telemetry = new Message(MessageId.New(), "Telemetry") { WireId = 7 };
        telemetry.Routes.Add(new MessageRoute(sensor.Id, controller.Id));
        telemetry.Fields.Add(new FieldBinding("header", header.Id));
        telemetry.Fields.Add(new FieldBinding("mode", mode.Id, FieldEncoding.Packed(4)));
        telemetry.Fields.Add(new FieldBinding("temperature", temperature.Id,
            new FieldEncoding { BitWidth = 4, AllowBitPacking = true, Transform = new ScalarTransform(1000, 1) }));
        telemetry.Fields.Add(new FieldBinding("samples", samples.Id));
        telemetry.Fields.Add(new FieldBinding("checksum", u16.Id));

        bus.Messages.Add(telemetry);
        project.Buses.Add(bus);
        return project;
    }
}
