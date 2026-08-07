namespace ProtoDesigner.Persistence.Json.Tests;

/// <summary>
/// The schema migration chain, exercised on a real v1 document.
/// </summary>
/// <remarks>
/// v1 → v2 dropped the per-field <c>crc</c> annotation when CRCs stopped being modelled. The chain was
/// written in Phase 2 and unused until then, so these tests are as much about proving the mechanism works
/// as about this one migration: a project saved by an older build must still open, and must open with its
/// fields intact rather than merely without crashing.
/// </remarks>
public class MigrationTests
{
    /// <summary>A v1 project whose last field carries the CRC annotation that v2 removes.</summary>
    private const string V1Document = """
        {
          "schemaVersion": 1,
          "name": "Legacy",
          "options": {},
          "types": [
            {
              "kind": "parameter",
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "u16",
              "primitive": "U16"
            }
          ],
          "buses": [
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "Main",
              "transport": "Ethernet",
              "options": {},
              "modules": [],
              "messages": [
                {
                  "id": "33333333-3333-3333-3333-333333333333",
                  "name": "Telemetry",
                  "wireId": 7,
                  "options": {},
                  "routes": [],
                  "fields": [
                    {
                      "id": "44444444-4444-4444-4444-444444444444",
                      "name": "payload",
                      "typeId": "11111111-1111-1111-1111-111111111111",
                      "encoding": {}
                    },
                    {
                      "id": "55555555-5555-5555-5555-555555555555",
                      "name": "crc",
                      "typeId": "11111111-1111-1111-1111-111111111111",
                      "encoding": {},
                      "crc": { "algorithm": "Crc16Ccitt" }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void A_v1_project_loads_and_arrives_at_the_current_schema()
    {
        var project = JsonProjectRepository.LoadFromString(V1Document);

        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Equal("Legacy", project.Name);
    }

    [Fact]
    public void The_crc_annotation_is_dropped_but_the_field_survives()
    {
        // The point of the migration: the annotation goes, the field stays. Losing the field would
        // silently change the wire format of every message that had a CRC.
        var project = JsonProjectRepository.LoadFromString(V1Document);
        var message = project.Buses.Single().Messages.Single();

        Assert.Equal(2, message.Fields.Count);

        var crcField = message.Fields[1];
        Assert.Equal("crc", crcField.Name);
        Assert.Equal(new FieldId(Guid.Parse("55555555-5555-5555-5555-555555555555")), crcField.Id);
    }

    [Fact]
    public void Saving_a_migrated_project_writes_the_current_schema_and_no_crc_key()
    {
        var project = JsonProjectRepository.LoadFromString(V1Document);
        var text = JsonProjectRepository.SaveToString(project);

        Assert.Contains($"\"schemaVersion\": {Project.CurrentSchemaVersion}", text, StringComparison.Ordinal);

        // The field is still called "crc" — that is its name. What must be gone is the annotation object.
        Assert.DoesNotContain("\"algorithm\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"crc\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_migrated_project_round_trips_unchanged_on_the_second_save()
    {
        // Once migrated, the document is canonical: saving it again must be byte-identical, or every
        // open-and-save would churn the file in git.
        var project = JsonProjectRepository.LoadFromString(V1Document);
        var once = JsonProjectRepository.SaveToString(project);
        var twice = JsonProjectRepository.SaveToString(JsonProjectRepository.LoadFromString(once));

        Assert.Equal(once, twice, StringComparer.Ordinal);
    }

    /// <summary>
    /// Protobuf field numbers are the one piece of state whose whole value is that it never changes, so
    /// surviving a save/load is the property that matters most about them.
    /// </summary>
    [Fact]
    public void Proto_field_numbers_survive_a_round_trip()
    {
        var project = JsonProjectRepository.LoadFromString(V1Document);
        var fields = project.Buses.Single().Messages.Single().Fields;
        fields[0].ProtoFieldNumber = 4;
        fields[1].ProtoFieldNumber = 9;

        var reloaded = JsonProjectRepository.LoadFromString(JsonProjectRepository.SaveToString(project));
        var back = reloaded.Buses.Single().Messages.Single().Fields;

        Assert.Equal(4, back[0].ProtoFieldNumber);
        Assert.Equal(9, back[1].ProtoFieldNumber);
    }

    [Fact]
    public void A_project_never_exported_carries_no_proto_keys()
    {
        // An optional key needs no schema bump, but it also should not appear in a file that has no use
        // for it — a project that never touched protobuf produces the same bytes it always did.
        var project = JsonProjectRepository.LoadFromString(V1Document);
        var text = JsonProjectRepository.SaveToString(project);

        Assert.DoesNotContain("protoFieldNumber", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_from_a_newer_build_is_refused_rather_than_guessed_at()
    {
        var future = V1Document.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9999", StringComparison.Ordinal);

        var ex = Assert.Throws<NotSupportedException>(() => JsonProjectRepository.LoadFromString(future));
        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
    }
}
