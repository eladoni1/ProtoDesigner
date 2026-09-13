# Writing a `.pdproj` by hand

A `.pdproj` is a single UTF-8 JSON file describing one ProtoDesigner project. This document is the
contract for producing one without the editor — everything here was checked against
`ProjectSerializer.cs` and confirmed by running the CLI, not inferred from the GUI.

Nothing in the file is derived. There are no offsets, no computed sizes, no cached layouts: you state
what the protocol *is*, and the tool computes where every bit lands. If you find yourself wanting to
write a byte offset, you are fighting the format.

---

## 1. Verify what you produce — the loop matters more than this document

Do not hand-inspect generated files. Two commands settle everything:

```bash
dotnet run --project src/ProtoDesigner.Cli -- validate yourfile.pdproj
```

```bash
dotnet run --project src/ProtoDesigner.Cli -- generate yourfile.pdproj --target c --out ./out
```

Anything that survives both is a real project. Treat a generator that has not run `validate` on its
output as untested.

### The two ways a file fails, and they are not alike

| | When | Exit | Looks like |
|---|---|---|---|
| **Load failure** | before the model exists | `3` | `Could not read 'x.pdproj': ...` |
| **Validation failure** | after loading | `1` | `PD0011 error: Field 'seq' references type ... [bus 'Link' / message 'Ping' / field 'seq']` |

A **load failure** means the JSON is the wrong *shape*: a misspelled enum value, a number where a string
belongs, a malformed GUID, a missing required key. The message names the mismatch but not the location,
so these are painful to debug — get the shape right by construction.

A **validation failure** means the JSON was fine and the *protocol* is wrong. These carry a stable
`PDxxxx` code and name the exact field. §9 lists them.

The exit code is reliable — `0` clean, `1` validation errors, `3` unreadable file, `2` usage — so
script against it. One caveat if you do: piping the command through `tail` or `head` gives you the
*pipe's* status, not the CLI's. Redirect instead of piping when you check `$?`.

---

## 2. The one rule that catches everybody

> **Wire settings must be written on the field binding's `encoding`. A type's `wireBits` does not
> reach the wire on its own.**

A type may carry `wireBits`, `wireForm`, `wireOffset`, `wireScale`. Those are **defaults the editor
copies onto new bindings** — applied by `WireEncodingPropagator`, which runs *only inside the WPF
editor*. The loader does not run it. The CLI does not run it. The layout engine reads
`FieldBinding.Encoding` and nothing else.

Measured, with two `U8` fields of a type declaring `"wireBits": 4`:

| Field `encoding` | Generated message size |
|---|---|
| `{}` | **2 bytes** — the type's 4 bits ignored, natural 8-bit width used |
| `{ "bitWidth": 4, "allowBitPacking": true }` | **1 byte** — as designed |

Set both if you want the file to look right in the editor too, but **the `encoding` is the one that
decides the bytes.** Emit it always.

The same applies to `endianness` and `bitOrder`: on a type they mean nothing at all; on a binding they
override; and the usual place to set them is the **bus** (see §6).

---

## 3. Skeleton

A template, not a valid file — the angle-bracket placeholders stand for real GUIDs:

```json
{
  "schemaVersion": 2,
  "name": "ProjectName",
  "options": { },
  "types": { "<type-guid>": { }, "…": { } },
  "buses": [ { } ]
}
```

- `schemaVersion` — **write `2`.** Version 1 files still load (a migration drops retired CRC keys), but
  emit current.
- `name` — required, free text. Becomes the default C namespace/symbol prefix if none is passed.
- `options` — optional, may be omitted entirely. Project-wide layout defaults (§7).
- `types` — an **object keyed by type GUID**, not an array. The key *is* the id; there is no `id`
  property inside a type.
- `buses` — an **array**, and each bus carries its `id` as a property.

That asymmetry is deliberate and is the most common structural mistake. Types are keyed; everything else
is a list.

A minimal file that validates clean:

```json
{
  "schemaVersion": 2,
  "name": "Minimal",
  "types": {
    "11111111-1111-1111-1111-111111111111": {
      "name": "u8", "kind": "parameter", "primitive": "U8"
    }
  },
  "buses": [{
    "id": "aaaaaaaa-0000-0000-0000-000000000001",
    "name": "Link", "transport": "Ethernet",
    "modules": [],
    "messages": [{
      "id": "bbbbbbbb-0000-0000-0000-000000000001",
      "name": "Ping", "wireId": 1,
      "fields": [{
        "id": "dddddddd-0000-0000-0000-000000000001",
        "name": "seq",
        "typeId": "11111111-1111-1111-1111-111111111111",
        "encoding": {}
      }]
    }]
  }]
}
```

`options`, `routes`, `description` and every other optional key may be omitted outright — an absent
object is not the same as an empty one only in that it is shorter.

---

## 4. JSON type discipline — strings vs numbers

This is strict and mismatches fail at **load**, not validation. There is no coercion.

**Written as JSON strings** (they are `decimal` in the model, and a JSON number would lose precision
past 2^53):

- `rangeMin`, `rangeMax`
- `wireOffset`, `wireScale`
- `transform.offset`, `transform.scale`
- `defaultValue` — always a string, even for a number; the loader parses it back to `decimal` when it
  can and keeps it as text otherwise

**Written as JSON numbers:**

- `schemaVersion`, `wireBits`, `bitWidth`, `alignmentBits`, `prefixBits`
- `wireId`, `protoFieldNumber`, `count`, `maxCount`, `minCount`
- enum member `value`, and each byte of a `sentinel` array

**Written as JSON booleans:** `isFlags`, `allowBitPacking`, `padToByteBoundary`.

Every enum-valued key is a **case-sensitive string** matching the C# member name exactly.

```jsonc
"rangeMin": 0      // ✗ load error: 'Number' cannot be converted to 'System.String'
"rangeMin": "0"    // ✓
"wireBits": "8"    // ✗ load error: 'String' cannot be converted to 'System.Int32'
"wireBits": 8      // ✓
"primitive": "uint8"  // ✗ load error: Requested value 'uint8' was not found
"primitive": "U8"     // ✓
```

---

## 5. Identity

Every id is a **GUID in the standard 8-4-4-4-12 hyphenated form**. Anything `Guid.Parse` accepts will
load, but emit lowercase hyphenated.

Ids are the only thing references use — names are display and codegen only, and renaming anything never
breaks a reference. Correspondingly, **an id must be stable across regenerations of your file.** If your
generator mints fresh GUIDs on every run, every regeneration looks like a total rewrite to git, and any
`countFieldId` pointing into the file breaks the moment the two sides disagree.

Derive them deterministically from your own source (a UUIDv5 over a stable path is ideal), or use
readable placeholders as the checked-in samples do:

```
10000000-0000-0000-0000-000000000001    types
30000000-0000-0000-0000-000000000001    structs
60000000-0000-0000-0000-000000000001    fields
```

Which ids must be unique:

| Id | Must be unique across |
|---|---|
| type key | the whole `types` object |
| `FieldId` | the whole project — `countFieldId` resolves against a message's fields, and a duplicate makes the wrong one win |
| `MessageId`, `BusId`, `ModuleId` | the whole project |

Simplest safe rule: **make every GUID in the file distinct.**

---

## 6. Buses, modules, messages, fields

### Bus

```json
{
  "id": "<guid>",
  "name": "Field",
  "transport": "Uart",
  "options": { "endianness": "Big" },
  "modules": [ { "id": "<guid>", "name": "Sensor" } ],
  "messages": [ ]
}
```

`id`, `name`, `transport` are required. `transport` is `Ethernet` or `Uart` — it sets the frame budget
a message is warned against (1500 and 256 payload bytes respectively).

**`options` on the bus is where byte order and bit order belong.** One bus is one agreement about wire
format; the editor offers them nowhere else. Two modules disagreeing is two buses with a gateway between
them, not an override.

`modules` is an array of `{ id, name }`. Bare strings also load (a legacy shape that mints ids), but
then nothing can route to them — **emit objects.** An empty array is fine.

### Message

```json
{
  "id": "<guid>",
  "name": "Report",
  "wireId": 1,
  "description": "…",
  "options": { },
  "routes": [ { "from": "<module-guid>", "to": "<module-guid>" } ],
  "fields": [ ]
}
```

`id`, `name` and `fields` are required; the rest are optional. `wireId` is how a receiver identifies the
message on the wire — unique within a bus, and its absence is reported as `PD0063`.

`routes` name **module ids**, not names, and both must be modules of the same bus. Routes are what
`--module` generation selects on; a message with none still generates under `--bus`.

### Field binding

```json
{
  "id": "<guid>",
  "name": "temperature",
  "typeId": "<type-guid>",
  "encoding": { },
  "defaultValue": "0",
  "description": "…",
  "protoFieldNumber": 3
}
```

`id`, `name`, `typeId` are required. `encoding` may be `{}` but **emit the key**.

**Field order is wire order.** The serializer preserves it exactly and must — unlike types, buses and
messages, which it re-sorts by id. Never reorder fields to tidy a diff.

`protoFieldNumber` only matters for the `proto` target, must be stable once assigned, and can be left
out entirely.

### `encoding`

Every key is optional; an absent one inherits field → message → bus → project → built-in default.

| Key | JSON | Meaning |
|---|---|---|
| `bitWidth` | number | Serialised width, 1..64. **On an array binding this is the width of one *element*.** |
| `endianness` | `"Little"` \| `"Big"` | Byte order. Prefer setting it on the bus. |
| `bitOrder` | `"MsbFirst"` \| `"LsbFirst"` | Which end of the value's bits goes first. Prefer the bus. |
| `allowBitPacking` | bool | Lets this field share a byte with its neighbours. Without it the field starts on a byte boundary. |
| `alignmentBits` | number > 0 | Opt-in natural alignment. Rarely needed. |
| `transform` | object | `{ "offset": "<decimal-string>", "scale": "<decimal-string>" }` |

`transform` is `wire = (value - offset) / scale`. It is the single primitive behind range compression
(`offset` alone: 1000..1015 in 4 bits with `offset: "1000"`) and quantization (`scale` other than 1).
Use `scale: "1"` when you only want an offset — and note that a scale below 1 on an *integer* host is
almost always a mistake, since there is nothing between 1000 and 1001 to resolve.

Built-in defaults if nothing sets them: `Little`, `MsbFirst`, 8-bit alignment, contiguous packing,
pad to byte boundary.

---

## 7. `options` (project, bus, message)

The same object at all three levels; every key optional, most-specific wins.

| Key | JSON | Values |
|---|---|---|
| `endianness` | string | `Little`, `Big` |
| `bitOrder` | string | `MsbFirst`, `LsbFirst` |
| `defaultAlignmentBits` | number | usually `8` |
| `packingMode` | string | `Contiguous`, `StorageUnit` |
| `padToByteBoundary` | bool | default `true` |

---

## 8. Types

All four share `name` (required), `kind` (required) and `description` (optional). `kind` is one of
`parameter`, `enum`, `struct`, `array`.

### `parameter` — a named scalar

```json
{
  "name": "Temperature",
  "kind": "parameter",
  "primitive": "F32",
  "rangeMin": "-40",
  "rangeMax": "60",
  "wireBits": 10,
  "wireForm": "Unsigned"
}
```

`primitive` is required, one of:

```
Bool  Char  I8  U8  I16  U16  I32  U32  I64  U64  F32  F64
```

`rangeMin`/`rangeMax` are optional but come as a **pair** — one alone is ignored. A declared range is
what makes compression possible and is the only thing that becomes a protovalidate constraint; an
undeclared one emits nothing, deliberately.

`wireForm` is `Unsigned`, `Signed` or `Float` (the legacy spelling `Integer` still loads as `Unsigned`).
`wireBits`, `wireOffset`, `wireScale` are the editor-side defaults from §2.

### `enum`

```json
{
  "name": "Quality",
  "kind": "enum",
  "underlying": "U8",
  "isFlags": false,
  "wireBits": 2,
  "wireForm": "Unsigned",
  "members": [
    { "name": "Unknown", "value": 0 },
    { "name": "Good",    "value": 2 }
  ]
}
```

`underlying` is a `PrimitiveKind`. `members` must be non-empty (`PD0012`) with distinct names and values
(`PD0013`), and every value must fit the declared width (`PD0021`).

`synthetic` is optional and takes `MessageId` or `ModuleId`. It marks an enum whose members **the bus
fills in at generation** — leave `members` empty for one, because anything you write there is ignored
and would only go stale. Use it for a header field that carries the message id. Omit the key entirely
for an ordinary enum.

### `struct`

```json
{
  "name": "Header",
  "kind": "struct",
  "fields": []
}
```

`fields` holds full field bindings — the same shape as a message's, with their own ids and encodings.
Order is wire order.

### `array`

```json
{
  "name": "SampleBlock",
  "kind": "array",
  "elementTypeId": "<type-guid>",
  "length": { "kind": "countFromField", "countFieldId": "<field-guid>", "maxCount": 12, "minCount": 1 }
}
```

The element may be a **parameter, enum or struct**. It may *not* be another array (`PD0036`).

`length.kind` is one of:

| `kind` | Extra keys | Notes |
|---|---|---|
| `fixed` | `count` | Constant size. `minCount` is not written — `count` is both ends. |
| `countFromField` | `countFieldId`, `maxCount` | The count field must be an **unsigned integer laid out earlier in the same message**, and wide enough for `maxCount`. |
| `lengthPrefixed` | `prefixBits`, `maxCount` | The array writes its own count immediately before its elements. `prefixBits` must express `maxCount`; use a whole byte. |
| `terminated` | `sentinel` (array of byte numbers), `maxCount` | Encode works; **decode still asks the caller for a count.** |
| `fillRemaining` | `maxCount` | Same decode caveat. |

`minCount` is optional on every kind except `fixed`, defaults to `0`, and is **declarative intent only** —
it raises the reported minimum message size and becomes protobuf's `min_items`; the generated C does not
enforce it.

`countFieldId` points at a `FieldId` — including one nested inside an *earlier struct* in the same
message, which is the usual place to put it.

Because the count reference lives on the **type** and a type is project-wide, an array used by two
messages can only point into one of them; the other reports `PD0030`. Give each message its own array
type when they need different count fields.

---

## 9. What the validator enforces

Errors block generation. Warnings and info do not.

| Code | Sev | Condition |
|---|---|---|
| `PD0001`–`PD0003`, `PD0005` | Error | Duplicate field / message / bus / type name in one scope |
| `PD0004` | Error | Two messages on one bus share a `wireId` |
| `PD0007`–`PD0009` | Error | Route names an unknown module, is duplicated, or points at itself |
| `PD0010` | Error | A type contains itself, directly or transitively |
| `PD0011` | Error | `typeId` / `elementTypeId` not in the type library |
| `PD0012`, `PD0013` | Error | Enum with no members; duplicate member name or value |
| `PD0020` | Error | `bitWidth` too small for the declared range after the transform |
| `PD0021` | Error | An enum member does not fit the declared width |
| `PD0022` | Error | `defaultValue` outside the range |
| `PD0023` | Error | `bitWidth` outside 1..64 |
| `PD0024` | Error | `alignmentBits` not positive |
| `PD0030`–`PD0033` | Error | Count field missing, after the array, not an unsigned integer, or too narrow |
| `PD0034` | Error | `prefixBits` cannot express `maxCount` |
| `PD0035` | Error | A dynamic array nested inside another dynamic array |
| `PD0036` | Error | Array of arrays; or a struct element containing a variable array or an array of composites |
| `PD0037` | Error | `minCount` negative or above capacity |
| `PD0050` / `PD0051` | Warn / Info | Message can exceed, or reaches 80% of, the transport's payload budget |
| `PD0060`–`PD0063` | Info | Unreferenced type; bus with no messages; message with no fields; message with no `wireId` |

`PD0006` (`InvalidName`) is **declared but not implemented** — no rule checks name syntax today. Do not
rely on the tool to reject a bad name. Names are sanitised into legal C identifiers on the way out
(non-alphanumerics dropped, a leading digit prefixed, C/C++ keywords suffixed with `_`), so odd names
generate rather than fail — but two names that sanitise to the same identifier will collide in the
emitted header, and nothing warns. **Emit identifier-safe names.**

`PD0070`–`PD0075` are the protobuf export gate. They are *not* in the default rule set and never block C
generation. They only matter if you intend `--target proto`, which additionally requires every integer
and enum to be 32 or 64 bits wide.

---

## 10. Worked example

Validates clean and generates C. It exercises a bit-packed struct used as a dynamic array element, a
transform, a fixed array, a bus-level endianness override, and a route.

```json
{
  "schemaVersion": 2,
  "name": "WeatherStation",
  "options": { "endianness": "Little", "bitOrder": "MsbFirst" },
  "types": {
    "10000000-0000-0000-0000-000000000001": {
      "name": "u8", "kind": "parameter", "primitive": "U8",
      "wireBits": 8, "wireForm": "Unsigned"
    },
    "10000000-0000-0000-0000-000000000002": {
      "name": "u16", "kind": "parameter", "primitive": "U16",
      "wireBits": 16, "wireForm": "Unsigned"
    },
    "10000000-0000-0000-0000-000000000003": {
      "name": "u32", "kind": "parameter", "primitive": "U32",
      "wireBits": 32, "wireForm": "Unsigned"
    },
    "10000000-0000-0000-0000-000000000004": {
      "name": "Temperature", "kind": "parameter",
      "description": "Celsius, quarter-degree resolution.",
      "primitive": "F32", "rangeMin": "-40", "rangeMax": "60",
      "wireBits": 10, "wireForm": "Unsigned"
    },
    "20000000-0000-0000-0000-000000000001": {
      "name": "Quality", "kind": "enum", "underlying": "U8", "isFlags": false,
      "wireBits": 2, "wireForm": "Unsigned",
      "members": [
        { "name": "Unknown", "value": 0 },
        { "name": "Poor", "value": 1 },
        { "name": "Good", "value": 2 },
        { "name": "Calibrated", "value": 3 }
      ]
    },
    "30000000-0000-0000-0000-000000000001": {
      "name": "Header", "kind": "struct",
      "fields": [
        { "id": "40000000-0000-0000-0000-000000000001", "name": "version",
          "typeId": "10000000-0000-0000-0000-000000000001",
          "encoding": { "bitWidth": 8 } },
        { "id": "40000000-0000-0000-0000-000000000002", "name": "uptime",
          "typeId": "10000000-0000-0000-0000-000000000003",
          "encoding": { "bitWidth": 32 } }
      ]
    },
    "30000000-0000-0000-0000-000000000002": {
      "name": "Sample", "kind": "struct",
      "fields": [
        { "id": "40000000-0000-0000-0000-000000000003", "name": "temperature",
          "typeId": "10000000-0000-0000-0000-000000000004",
          "encoding": { "bitWidth": 10, "allowBitPacking": true,
                        "transform": { "offset": "-40", "scale": "0.25" } } },
        { "id": "40000000-0000-0000-0000-000000000004", "name": "quality",
          "typeId": "20000000-0000-0000-0000-000000000001",
          "encoding": { "bitWidth": 2, "allowBitPacking": true } },
        { "id": "40000000-0000-0000-0000-000000000005", "name": "pad",
          "typeId": "10000000-0000-0000-0000-000000000001",
          "encoding": { "bitWidth": 4, "allowBitPacking": true } }
      ]
    },
    "50000000-0000-0000-0000-000000000001": {
      "name": "SampleBlock", "kind": "array",
      "elementTypeId": "30000000-0000-0000-0000-000000000002",
      "length": { "kind": "countFromField",
                  "countFieldId": "60000000-0000-0000-0000-000000000002",
                  "maxCount": 12, "minCount": 1 }
    },
    "50000000-0000-0000-0000-000000000002": {
      "name": "StationId", "kind": "array",
      "elementTypeId": "10000000-0000-0000-0000-000000000001",
      "length": { "kind": "fixed", "count": 4 }
    }
  },
  "buses": [{
    "id": "70000000-0000-0000-0000-000000000001",
    "name": "Field", "transport": "Uart",
    "options": { "endianness": "Big" },
    "modules": [
      { "id": "80000000-0000-0000-0000-000000000001", "name": "Sensor" },
      { "id": "80000000-0000-0000-0000-000000000002", "name": "Logger" }
    ],
    "messages": [{
      "id": "90000000-0000-0000-0000-000000000001",
      "name": "Report", "wireId": 1,
      "description": "Periodic batch of samples.",
      "options": {},
      "routes": [{ "from": "80000000-0000-0000-0000-000000000001",
                   "to": "80000000-0000-0000-0000-000000000002" }],
      "fields": [
        { "id": "60000000-0000-0000-0000-000000000001", "name": "header",
          "typeId": "30000000-0000-0000-0000-000000000001", "encoding": {} },
        { "id": "60000000-0000-0000-0000-000000000002", "name": "sampleCount",
          "typeId": "10000000-0000-0000-0000-000000000001",
          "encoding": { "bitWidth": 8 } },
        { "id": "60000000-0000-0000-0000-000000000003", "name": "samples",
          "typeId": "50000000-0000-0000-0000-000000000001", "encoding": {} },
        { "id": "60000000-0000-0000-0000-000000000004", "name": "stationId",
          "typeId": "50000000-0000-0000-0000-000000000002",
          "encoding": { "bitWidth": 8 } },
        { "id": "60000000-0000-0000-0000-000000000005", "name": "crc",
          "typeId": "10000000-0000-0000-0000-000000000002",
          "encoding": { "bitWidth": 16 } }
      ]
    }]
  }]
}
```

It produces:

```c
typedef struct proto_Report {
    proto_Header header;
    uint8_t sampleCount;   /* 8 bits */
    /* 'samples': at least 1, up to 12, count from an earlier field */
    proto_Sample samples[12];
    /* 'stationId': exactly 4 elements */
    uint8_t stationId[4];
    uint16_t crc;   /* 16 bits */
} proto_Report;
```

`/* Message 'Report' (wire id 1) - 112..288 bits / 14..36 bytes. */`

Note `samples` on the field binding carries `encoding: {}` while `stationId` carries
`"bitWidth": 8` — on an array binding the width is *one element's*, and `Sample`'s members carry their
own. Note also that the three `Sample` members pack into two bytes (10 + 2 + 4) because each sets
`allowBitPacking`; without it each would start on a byte boundary and the element would be three bytes.

---

## 11. Traps, shortest form

1. **Put the width on the binding's `encoding`, not only on the type.** §2. This is the one that
   silently produces a working file describing the wrong protocol.
2. **`types` is keyed by GUID; `buses`, `messages`, `fields` are arrays with an `id` inside.**
3. **`rangeMin`/`rangeMax`/`transform`/`wireOffset`/`wireScale`/`defaultValue` are JSON strings.**
   Widths and counts are JSON numbers.
4. **Enum spellings are exact and case-sensitive** — `U8` not `uint8`, `MsbFirst` not `MSBFirst`,
   `Uart` not `UART`, `countFromField` not `CountFromField`.
5. **A count field must appear before its array in the same message.** Later is `PD0031`; a struct field
   earlier in the message counts as earlier.
6. **Field order is wire order.** Never re-sort. Types, buses and messages *are* re-sorted by id when
   the tool saves — do not fight that, and expect a round-trip through the editor to reorder them.
7. **Keep GUIDs stable across regenerations**, or every rewrite is a whole-file diff and
   `countFieldId` links rot.
8. **Sub-byte array element strides are rejected** while byte padding is on, because offsets after the
   array would not be byte-addressable. Make an element a whole number of bytes.
9. **`allowBitPacking` is opt-in.** Fields are byte-aligned by default; a 4-bit field without it still
   occupies a byte.
10. **Do not model CRCs.** A checksum is an ordinary field the caller fills in. The generated
    `<Msg>_OnWireLength()` is what locates a trailing one.

---

## 12. Round-tripping

The writer is canonical: stable key order, 2-space indent, LF endings, invariant number formatting,
UTF-8 without BOM. If your generator's output differs cosmetically that is harmless — but loading and
re-saving through the tool normalises it, and that diff is a useful self-check that nothing was
misread.

Optional keys are omitted rather than written as `null`. Follow that: a `null` where an object is
expected is not the same as an absent key.
