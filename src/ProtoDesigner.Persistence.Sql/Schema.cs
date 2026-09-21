namespace ProtoDesigner.Persistence.Sql;

/// <summary>
/// The tables a project explodes into. One row per entity that has an id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows for identity, columns for values.</b> Everything the model gives an id — types, buses,
/// modules, messages, fields — is a row, because that is what makes an entity separately addressable:
/// two people editing different messages touch different rows, which is the whole reason for a database
/// rather than a document. Value objects that have no identity and are never referenced —
/// <c>FieldEncoding</c>, <c>LayoutOptions</c>, <c>NumericRange</c> — are columns on their owner, since
/// they are always read and written with it.
/// </para>
/// <para>
/// <c>array_length</c> is the one exception, and it is deliberate: <c>ArrayLength</c> is a five-variant
/// union, so relationally it is either five nullable column groups or five sub-tables, for a query
/// nothing in this application makes. It is stored as the same compact JSON the file format uses.
/// </para>
/// <para>
/// <b>Decimals are TEXT, not REAL.</b> SQLite's REAL is a double, which loses the top of the 64-bit
/// integer domain — the exact reason <c>ScalarTransform</c> uses <c>decimal</c> in the first place. A
/// transform offset of 9007199254740993 must come back as itself.
/// </para>
/// <para>
/// <c>fields.owner_id</c> is a message id or a struct type id without saying which, because ids are
/// GUIDs and cannot collide. A field belongs to whichever one claims it.
/// </para>
/// </remarks>
internal static class Schema
{
    public const string Create = """
        CREATE TABLE IF NOT EXISTS projects (
          project_key     TEXT PRIMARY KEY,
          name            TEXT NOT NULL,
          schema_version  INTEGER NOT NULL,
          -- Bumped on every write. A save carries the version it read and the update is conditional on
          -- it, so a writer that was overtaken in the gap is refused rather than silently winning.
          version         INTEGER NOT NULL DEFAULT 0,
          endianness      TEXT, bit_order TEXT, alignment_bits INTEGER, packing_mode TEXT, pad_to_byte INTEGER
        );

        CREATE TABLE IF NOT EXISTS types (
          project_key     TEXT NOT NULL,
          id              TEXT NOT NULL,
          kind            TEXT NOT NULL,
          name            TEXT NOT NULL,
          description     TEXT,
          primitive_kind  TEXT,
          range_min       TEXT, range_max TEXT,
          wire_bits       INTEGER, wire_form TEXT, wire_offset TEXT, wire_scale TEXT,
          is_flags        INTEGER, synthetic TEXT,
          element_type_id TEXT, array_length TEXT,
          PRIMARY KEY (project_key, id)
        );

        CREATE TABLE IF NOT EXISTS enum_members (
          project_key TEXT NOT NULL, type_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
          name TEXT NOT NULL, value INTEGER NOT NULL,
          PRIMARY KEY (project_key, type_id, ordinal)
        );

        CREATE TABLE IF NOT EXISTS buses (
          project_key TEXT NOT NULL, id TEXT NOT NULL, ordinal INTEGER NOT NULL,
          name TEXT NOT NULL, transport TEXT NOT NULL,
          endianness TEXT, bit_order TEXT, alignment_bits INTEGER, packing_mode TEXT, pad_to_byte INTEGER,
          PRIMARY KEY (project_key, id)
        );

        CREATE TABLE IF NOT EXISTS modules (
          project_key TEXT NOT NULL, bus_id TEXT NOT NULL, id TEXT NOT NULL,
          ordinal INTEGER NOT NULL, name TEXT NOT NULL,
          PRIMARY KEY (project_key, id)
        );

        CREATE TABLE IF NOT EXISTS messages (
          project_key TEXT NOT NULL, bus_id TEXT NOT NULL, id TEXT NOT NULL, ordinal INTEGER NOT NULL,
          name TEXT NOT NULL, wire_id INTEGER, description TEXT,
          endianness TEXT, bit_order TEXT, alignment_bits INTEGER, packing_mode TEXT, pad_to_byte INTEGER,
          PRIMARY KEY (project_key, id)
        );

        CREATE TABLE IF NOT EXISTS routes (
          project_key TEXT NOT NULL, message_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
          from_module TEXT NOT NULL, to_module TEXT NOT NULL,
          PRIMARY KEY (project_key, message_id, ordinal)
        );

        CREATE TABLE IF NOT EXISTS fields (
          project_key TEXT NOT NULL, owner_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
          id TEXT NOT NULL, name TEXT NOT NULL, type_id TEXT NOT NULL, description TEXT,
          default_value TEXT, proto_field_number INTEGER,
          bit_width INTEGER, endianness TEXT, bit_order TEXT,
          allow_bit_packing INTEGER, alignment_bits INTEGER,
          transform_offset TEXT, transform_scale TEXT,
          PRIMARY KEY (project_key, owner_id, ordinal)
        );
        """;

    /// <summary>Every table, child-first, for replacing one project's rows.</summary>
    public static readonly string[] Tables =
        { "fields", "routes", "messages", "modules", "buses", "enum_members", "types", "projects" };
}
