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
| 3 | Resolved IR + C++14 generator | **Done** — 34 tests + C++ conformance |
| 4 | WPF editor | **Usable** — tree, field grid, live byte map, diagnostics, generate dialog |
| 5 | C# generator + advanced protocol features | **Started** — C# emits declarations only |
| 6 | Shared storage & collaboration | Not started — [design note](docs/shared-storage-design.md) |

**1526 automated tests, all passing.** Plus a cross-language conformance harness that compiles the
generated C++ under MSVC and checks it produces byte-identical output to the C# reference codec.

### Wire sizes, and finding a field without storing an offset

Checksums, CRCs, framing and sequence handling are **not modelled**. A checksum is an ordinary field you
fill in — the width, polynomial and technique differ from message to message, and most teams already have
vetted code or a hardware unit. What the generator gives you is the size information, regenerated on
every edit so it can never go stale:

```cpp
static constexpr size_t Header_OnWireBytes = 5;    // per type
size_t len = Batch_OnWireLength(msg);              // per message, exact for variable-length ones
size_t trailer_at = len - u16_OnWireBytes;         // a trailing field, without a hardcoded offset
```

`Msg_OnWireLength` is a constant fold for a fixed-size message and computed from the array count for a
variable one.

### Not done yet
- **`FillRemaining` and sentinel-terminated arrays decode by asking the caller for the count** rather
  than scanning for the sentinel or consuming the remainder. Encoding both is correct.
- **The C# target emits declarations only** — classes, enums and each message's wire layout as a
  comment. Encode/decode is C++-only today.

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
                                 Cpp/ (full codec), CSharp/ (declarations only)
  ProtoDesigner.Cli/             validate / generate / targets
  ProtoDesigner.Wpf/             the editor

tests/
  ProtoDesigner.Core.Tests/            layout, validation, IR
  ProtoDesigner.Persistence.Json.Tests round-trip + canonical form
  ProtoDesigner.CodeGen.Tests/         round-trip through the codec + golden files
  ProtoDesigner.Cli.Tests/             exit codes and diagnostic output
  cpp-conformance/                     compiles the generated C++ and cross-checks the bytes

samples/telemetry.pdproj         a worked example exercising most features
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

To also verify the generated C++ compiles and round-trips (requires MSVC with the C++ workload):

```bash
powershell -File tests/cpp-conformance/run.ps1
```

---

## The CLI

```bash
dotnet run --project src/ProtoDesigner.Cli -- validate samples/telemetry.pdproj
```

```bash
dotnet run --project src/ProtoDesigner.Cli -- generate samples/telemetry.pdproj --out ./generated --target cpp --namespace telemetry
```

Targets: `cpp` (full encode/decode) and `csharp` (declarations only). The editor exposes the same thing
under **Build ▸ Generate code…** (Ctrl+G), with a preview of every file before anything is written.

Exit codes: `0` success, `1` validation errors (generation refused), `2` usage error, `3` I/O error.

Generation is **refused** when the model has any `Error` diagnostic — a broken protocol fails at the
CLI with a code you can grep for, not at the C++ compiler with a mystery.

---

## The region model (the one non-obvious idea)

A fixed-size message is one `Fixed` region, so every offset is absolute. A dynamic array splits the
message: a `Fixed` prefix, a `Variable` region holding the array, then another `Fixed` region for
whatever follows — and that trailing region's offsets are relative to where the variable part ends.

This is what lets code generation emit **compile-time-constant offsets on both sides** of a
variable-length field and only run a cursor through the middle. The generated C++ shows it plainly:

```cpp
// --- region 0 (Fixed) ---
const size_t r0 = w.bit_length();
w.write_unsigned(static_cast<uint64_t>(msg.count), 8, protodesigner::Endian::Little);
w.pad_to(8);

// --- region 1 (Variable) ---
const size_t r1 = w.bit_length();
for (size_t i = 0; i < static_cast<size_t>(msg.count) && i < 32; ++i) { ... }

// --- region 2 (Fixed) ---
const size_t r2 = w.bit_length();
w.write_unsigned(static_cast<uint64_t>(msg.crc), 16, protodesigner::Endian::Little);
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
  sentinel, or fill-remaining).
- Rejects, with the offending path: recursive types, a count field that doesn't precede its array, a
  too-narrow length prefix, a dynamic array inside a dynamic array, and a sub-byte stride under byte
  padding.

## What validation catches

Everything that is *wrong but computable* — the engine only throws when a layout is *impossible*.
Duplicate names and wire IDs, width-too-small-for-range, enum members that don't fit, out-of-range
defaults, count fields that are signed or too narrow, and frame-budget overruns against the transport's
MTU. Codes are stable and never renumbered; a retired code is never reused.
