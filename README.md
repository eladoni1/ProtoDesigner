# ProtoDesigner

A desktop tool for designing **binary communication protocols** over Ethernet/UART — think "database
schema designer, but for compressed message layouts on the wire." Define reusable types, compose them
into messages on a bus, control exactly how each field is serialized down to the bit, then generate
encode/decode code from that definition.

The design bet: **what a value means** (the type) is kept separate from **how it goes on the wire**
(the encoding). The same `Mode` enum can be 4 bytes in one message and 4 bits in another; only the
binding's encoding differs. And **layout is computed, never stored** — no offset lives on the model,
so resizing or reordering a field is just an edit followed by a recompute.

**Stack:** C# / .NET 8 / WPF. Windows-first. xUnit. No external runtime dependencies.

---

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Domain model + layout engine | **Done** — 110 tests |
| 1 | Validation (16 rules, stable `PDxxxx` codes) | **Done** — 38 tests |
| 2 | JSON persistence + repository port + CLI | **Done** — 22 tests |
| 3 | Resolved IR + C generator | **Done** — compiled and cross-checked as C and C++ |
| 4 | WPF editor | **Usable** — tree, field grid, live byte map, diagnostics, generate dialog |
| 5 | C# generator + advanced protocol features | **Started** — C# emits declarations only |
| 5b | Protobuf schema target + protovalidate | **Done** — gated per message, protoc- and runtime-verified |
| 6 | Shared storage & collaboration | Not started — [design note](docs/shared-storage-design.md) |

**1730 automated tests, all passing.** That includes a cross-language check that compiles the generated
code under MSVC — once as C, once as C++ — and asserts it produces byte-identical output to the C#
reference codec over the whole wire matrix. Two independent implementations: wherever they disagree, one
of them is wrong. The protobuf target gets the same treatment: its schema is compiled by real `protoc`,
and its constraints are then run against a real protovalidate runtime that has to *reject* the values
they forbid.

### Wire sizes, and finding a field without storing an offset

Checksums, CRCs, framing and sequence handling are **not modelled**. A checksum is an ordinary field you
fill in — the width, polynomial and technique differ from message to message, and most teams already have
vetted code or a hardware unit. What the generator gives you is the size information, regenerated on
every edit so it can never go stale:

```c
#define PROTO_HEADER_ON_WIRE_BYTES 5               /* per type */
size_t len = proto_Batch_OnWireLength(&msg);       /* per message, exact for variable-length ones */
size_t trailer_at = len - PROTO_U16_ON_WIRE_BYTES; /* a trailing field, without a hardcoded offset */
```

`Msg_OnWireLength` is a constant fold for a fixed-size message and computed from the array count for a
variable one.

### Not done yet
- **`FillRemaining` and sentinel-terminated arrays decode by asking the caller for the count** rather
  than scanning for the sentinel or consuming the remainder. Encoding both is correct.
- **The C# target emits declarations only** — classes, enums and each message's wire layout as a
  comment. Encode/decode is C-only today.
- **Endianness and bit order have no UI.** Both are modelled and both are honoured by the layout engine,
  but nothing in the editor sets either, and the generated runtime only ever packs MSB-first.

---

## Solution layout

```
src/
  ProtoDesigner.Core/            model, layout engine, validation, IR — no UI, no language bias
    Model/                       TypeDefinition hierarchy, FieldBinding, Project/Bus/Message
    Layout/                      BitMath, LayoutEngine, MessageLayout (the region model)
    Validation/                  Diagnostic, Validator, Rules/ (one file per rule family)
    Ir/                          ProtocolIr + IrBuilder — the one-way contract generators read
  ProtoDesigner.Application/     IProjectRepository port, IEditCommand + CommandJournal (undo/redo),
                                 GenerationScopes, CodeGenerationService (the generate use case)
  ProtoDesigner.Persistence.Json canonical ID-keyed JSON with a migration chain
  ProtoDesigner.CodeGen/         IProtocolGenerator, GeneratorCatalog, BitBuffer + ReferenceCodec,
                                 C/ (full codec), CSharp/ (declarations only), Proto/ (schema)
  ProtoDesigner.Cli/             validate / generate / targets
  ProtoDesigner.Wpf/             the editor

tests/
  ProtoDesigner.Core.Tests/            layout, validation, IR
  ProtoDesigner.Persistence.Json.Tests round-trip, canonical form, schema migrations
  ProtoDesigner.CodeGen.Tests/         golden files, plus the C cross-checks that compile the
                                       generated code (as C and as C++) and diff the bytes
    Golden/                            expected output: C headers and .proto schemas
    Protovalidate/                     the Go harness that enforces the emitted constraints
  ProtoDesigner.Cli.Tests/             exit codes and diagnostic output
  ProtoDesigner.Application.Tests/     edit commands, generation scopes, the generate use case

samples/telemetry.pdproj         a worked example exercising most features
samples/protobuf-demo.pdproj     two exportable messages plus one bit-packed and one quantized that
                                 the protobuf gate must refuse by name
```

Dependencies point inward: UI → Application → Core. Persistence and CodeGen depend only on Core.
Nothing depends on the UI.

---

## Build and test

```bash
dotnet build
```

```bash
dotnet test
```

Requires the .NET 8 SDK (or newer — .NET 10 builds it fine).

Three conformance checks run as part of `dotnet test`, not as separate steps. All **fail** rather than
pass quietly when their toolchain is missing — a green suite that never compiled anything is the worst
outcome available.

| Check | Needs | Skip with |
|---|---|---|
| Generated C compiled as C *and* C++, bytes diffed against the C# reference codec | MSVC | `PROTODESIGNER_SKIP_CPP_CROSSCHECK=1` |
| Generated `.proto` compiled by real `protoc`, **including** its `buf.validate` constraints | protoc + Buf's `validate.proto` | `PROTODESIGNER_SKIP_PROTOC=1` |
| Those constraints enforced by a real protovalidate runtime — out-of-range values must be *rejected* | the above, plus Go | `PROTODESIGNER_SKIP_PROTOVALIDATE=1` |

The third is not a repeat of the second. protoc proves a constraint *parses* and resolves against the
real extension; it never evaluates one, so a rule bound to the wrong field or scoped to an array where it
belongs on the items compiles perfectly and protects nothing. The runtime check states a bound and then
sends a message that breaks it. It found exactly that bug the first time it ran.

No toolchain is vendored — protoc alone is a 12 MB platform binary git would keep forever. Lay it out as
`protobuf/{bin,include,validate.proto}` (gitignored) or set `PROTODESIGNER_PROTOC`. The Go harness and
its `go.mod` *are* checked in, under `tests/ProtoDesigner.CodeGen.Tests/Protovalidate/`; its dependencies
come from Go's own module cache.

---

## The CLI

```bash
dotnet run --project src/ProtoDesigner.Cli -- validate samples/telemetry.pdproj
```

```bash
dotnet run --project src/ProtoDesigner.Cli -- generate samples/telemetry.pdproj --out ./generated --target c --namespace telemetry
```

Targets:

| Id | Output |
|---|---|
| `c` | Full encode/decode. The header compiles as C or as C++. |
| `csharp` | Declarations and the wire layout as comments; no encode/decode yet. |
| `proto` | A protobuf schema, with protovalidate constraints. **A different wire format** — see below. |

Per-target settings go through `--option key=value` (repeatable); `targets` lists what each one accepts.
The editor exposes all of it via the **Generate code** button (Ctrl+G), with a preview of every file
before anything is written.

### The protobuf target

`proto` emits a `.proto` schema so one definition can serve two consumers — the embedded link on the C
codec, a backend or dashboard on protobuf — instead of two hand-written schemas that drift.

It is **not** an alternative encoding of the same bytes. Protobuf is tag-length-value with varints, so a
message through this schema and the same message through the C codec do not interoperate.

Because of that it is gated per message. Anything protobuf cannot represent is **refused and named**,
never silently translated:

- sub-byte field widths — protobuf has no 4-bit field
- a scalar transform with a scale other than 1 — quantization is lossy, so a protobuf peer and a C peer
  would disagree about the number

An *offset* is fine, because protobuf carries the value rather than the wire code, and the declared range
survives as a `buf.validate` constraint. That is what protovalidate buys: without it a 1000..1015 field
is bare `uint32` and a 32-element capacity is bare `repeated`, and every limit you designed is lost.

Both the schema and its constraints are compiled by real `protoc` in the test suite, against Buf's actual
`validate.proto` rather than a stub — so a misspelled or wrongly nested rule fails here rather than at
whatever consumer eventually builds the schema. The constraints are then handed to a real protovalidate
runtime and given values they forbid, which is the only way to tell a rule that works from one that
merely compiles.

```bash
dotnet run --project src/ProtoDesigner.Cli -- generate samples/telemetry.pdproj --out ./out --target proto --option protovalidate=false
```

Exit codes: `0` success, `1` validation errors (generation refused), `2` usage error, `3` I/O error.

Generation is **refused** when the model has any `Error` diagnostic — a broken protocol fails at the
CLI with a code you can grep for, not at the C compiler with a mystery.

---

## The region model (the one non-obvious idea)

A fixed-size message is one `Fixed` region, so every offset is absolute. A dynamic array splits the
message: a `Fixed` prefix, a `Variable` region holding the array, then another `Fixed` region for
whatever follows — and that trailing region's offsets are relative to where the variable part ends.

This is what lets code generation emit **compile-time-constant offsets on both sides** of a
variable-length field and only run a cursor through the middle. The generated C shows it plainly:

```c
/* --- region 0 (Fixed) --- */
{ const size_t r0 = pd_bw_bit_length(&w);
pd_bw_write_unsigned(&w, (uint64_t)(msg->count), 8, PD_ENDIAN_LITTLE);
pd_bw_pad_to(&w, 8);
}

/* --- region 1 (Variable) --- */
{ const size_t r1 = pd_bw_bit_length(&w);
for (size_t i = 0; i < (size_t)(msg->count) && i < 32; ++i) { ... }
}

/* --- region 2 (Fixed) --- */
{ const size_t r2 = pd_bw_bit_length(&w);
pd_bw_write_unsigned(&w, (uint64_t)(msg->crc), 16, PD_ENDIAN_LITTLE);
}
```

---

## What the layout engine handles

- Field order is wire order; resize/reorder/rename/remove all work by recompute.
- Byte-aligned by default, **not** naturally aligned — a wire format is not a C struct. Natural
  alignment is opt-in per field via `AlignmentBits`.
- Bit packing in two modes: `Contiguous` (fields may straddle byte boundaries) and `StorageUnit`
  (C-style bitfields that start a new unit rather than straddle).
- User-defined width down to the bit, with `ScalarTransform` (`wire = (v - offset) / scale`)
  covering enums that start at 100, range compression (1000..1015 → 4 bits), and float quantization.
- Structs (inline, nested, reused) and arrays (static; dynamic via count-field, length-prefix,
  sentinel, or fill-remaining), with an optional declared minimum element count — so a variable array
  can be "1 to 10", not just "up to 10".
- Rejects, with the offending path: recursive types, a count field that doesn't precede its array, a
  too-narrow length prefix, a dynamic array inside a dynamic array, and a sub-byte stride under byte
  padding.

## What validation catches

Everything that is *wrong but computable* — the engine only throws when a layout is *impossible*.
Duplicate names and wire IDs, width-too-small-for-range, enum members that don't fit, out-of-range
defaults, count fields that are signed or too narrow, and frame-budget overruns against the transport's
MTU. Codes are stable and never renumbered; a retired code is never reused.
