# ProtoDesigner — Project Guide & Roadmap

> Drop this in the repo root as `CLAUDE.md` and Claude Code loads it every session.
> The `.cs` files are the source of truth for the API; this document is the map, the
> rules, and the task list. When code and this doc disagree, trust the code and fix the doc.

ProtoDesigner is a desktop tool for designing **binary communication protocols** over
Ethernet/UART — think "database schema designer, but for compressed message layouts on the
wire." A user defines reusable types, composes them into messages on a bus, controls exactly
how each field is serialized (down to the bit), and later generates C/C#/Rust/… encode and
decode code from that definition — or a protobuf schema, for the messages protobuf can express.

**Stack:** C# / .NET 8 / WPF. Windows-first. xUnit for tests.

**Entity hierarchy:** `Project → Bus (connects modules, Ethernet or UART) → Message → FieldBinding`.
Types live in a project-wide `TypeLibrary` and are referenced by messages, so they're reusable
across every message. Message direction is not modeled (not needed yet).

---

## Table of contents

1. [The rules that must not be broken](#1-the-rules-that-must-not-be-broken)
2. [The core mental model](#2-the-core-mental-model)
3. [Current state](#3-current-state--phases-0-through-4)
4. [API surface that already exists](#4-api-surface-that-already-exists)
5. [Conventions](#5-conventions)
6. [Roadmap — phases 1 through 6](#6-roadmap--phases-1-through-6)
7. [Deferred vs rejected](#7-deferred-vs-rejected)
8. [Gotchas](#8-gotchas)

---

## 1. The rules that must not be broken

These are load-bearing. Every phase depends on them; violating one to make a task easier
creates rework later. If a change seems to require breaking one, stop and reconsider the design.

1. **Type ≠ encoding.** A *type* (`ParameterType`, `EnumType`, `StructType`, `ArrayType`) says
   what a value *means*. A *`FieldEncoding`* (on each `FieldBinding`) says how it goes on the
   wire — width, endianness, bit order, packing, alignment, transform. The same `EnumType` can
   be 4 bytes in one message and 4 bits in another. **Never fold encoding into a type**; that
   spawns duplicate types and blocks codegen from telling a `uint16_t` from a 12-bit field.

2. **Layout is computed, never stored.** No bit offset ever lives on the model — not in a class,
   not in a saved file. `LayoutEngine` derives offsets on demand. This is *why* resize/reorder/
   rename/remove "just work": there's nothing stored to shift. If you're tempted to cache an
   offset on `FieldBinding`, don't — cache the whole `MessageLayout` against a revision counter
   instead.

3. **Identity is an ID, never a name.** Every entity has an immutable `Guid`-backed ID
   (`TypeId`, `FieldId`, `MessageId`, `BusId`). Every reference — a field's type, an array's
   count field — is by ID. `Name` is display/codegen only and is freely
   mutable. Renaming must never break a reference.

4. **The Core is language-agnostic.** `ProtoDesigner.Core` knows nothing about C++, C#, JSON,
   or WPF. It's the neutral representation every generator and every storage backend reads.
   Dependencies point inward: UI → Application → Core; Persistence and CodeGen → Core. Nothing
   depends on the UI.

5. **The engine assumes a valid model; the validator guarantees it.** `LayoutEngine` throws a
   `LayoutException` only when a layout is *impossible* (recursion, a count field that doesn't
   precede its array). Everything that is *wrong but computable* — a width too small for a
   range, a duplicate wire ID, out-of-range defaults — is the **validator's** job and produces
   a `Diagnostic`, not an exception. Don't move policy into the engine or mechanism into the
   validator.

6. **Persist declarative intent only.** Saved files contain what the user chose. No computed
   sizes, no offsets, no cached layouts. Storage uses flat, ID-keyed collections (see Phase 2)
   so a file entry maps 1:1 to a future database row.

---

## 2. The core mental model

**A `FieldBinding` is a binding of a type to an encoding.** It names an occurrence of a type
inside a message or struct and owns the `FieldEncoding` for that occurrence. Order within the
containing `Fields` list *is* the wire order.

**`FieldEncoding` is all-nullable and inherits.** Any property left null resolves outward:
field → message → bus → project → built-in default. `EffectiveLayoutOptions.Resolve(...)` does
this for message-level options; per-field encoding falls back to the resolved message options
inside the engine.

**`ScalarTransform` (`wire = (value − Offset) / Scale`)** is the single primitive behind three
features: enums that start at 100 (offset 100), range compression (1000–1015 → 4 bits with
offset 1000), and float quantization (scale 0.5). `BitMath.RequiredBits(range, transform)`
computes the minimum width after the transform — that's the number the Phase 1 "impossible
compression size" rule compares a user's requested width against.

**The region model** is the one non-obvious output. `LayoutEngine` flattens nested structs and
arrays into `LayoutNode`s and groups them into `LayoutRegion`s:

- A **fixed-size** message is exactly one `Fixed` region, so every offset is absolute.
- A **dynamic array** splits the message: a `Fixed` prefix, a `Variable` region holding the
  array, then a new `Fixed` region for whatever follows. The suffix's offsets are relative to
  where the variable part ends.

Node `BitOffset` is relative to its region's start, and relative to one *element* for nodes
beneath an array (paths like `samples[]`, `samples[].x`). `MessageLayout` reports `MinBits`/
`MaxBits` and, per variable region, the element stride, capacity, the count field, or an inline
length prefix. This is precisely the information codegen needs to emit **compile-time-constant
offsets on both sides** of a variable field and run a cursor only through the middle.

---

## 3. Current state — phases 0 through 4

| Phase | Scope | State |
|---|---|---|
| 0 | Domain model + layout engine | **Done** |
| 1 | Validation — rules with stable `PDxxxx` codes | **Done** |
| 2 | JSON persistence + repository port + CLI | **Done** |
| 3 | Resolved IR + C generator | **Done** |
| 4 | WPF editor | **Usable** — tree, field grid, live byte map, diagnostics, generate dialog |
| 5 | C# generator + advanced protocol features | **Started** — C# emits declarations only |
| 5b | Protobuf schema target + protovalidate | **Done** — gated per message, protoc-verified |
| 6 | Shared storage & collaboration | Not started — see `docs/shared-storage-design.md` |

**1816 automated tests, all passing.** Three conformance checks run inside `dotnet test` and **fail**
rather than skip when their toolchain is absent — a green suite that compiled nothing is worse than a
red one. None of the toolchains is vendored.

| Check | Proves | Needs | Opt out with |
|---|---|---|---|
| `CCrossCheck` / `CCompositeCrossCheck` | the generated C compiles as C *and* as C++ and produces bytes identical to the C# reference codec | MSVC | `PROTODESIGNER_SKIP_CPP_CROSSCHECK=1` |
| `ProtocConformanceTests` | the generated `.proto` and its `buf.validate` options **compile** | protoc + Buf's `validate.proto` | `PROTODESIGNER_SKIP_PROTOC=1` |
| `ProtovalidateConformanceTests` | those constraints **actually reject** what they claim to | the above, plus Go | `PROTODESIGNER_SKIP_PROTOVALIDATE=1` |

The protoc check runs against Buf's actual `validate.proto` rather than a stub — a stub would only check
the generator against a guess at protovalidate's shape and would pass for exactly the reason it was
wrong. Lay the toolchain out as the gitignored `protobuf/{bin,include,validate.proto}`, or set
`PROTODESIGNER_PROTOC`.

**The third check is not redundant with the second, and the difference is the whole point.** protoc
proves an option parses and resolves against the real extension — that the rule exists and is spelled
correctly. It never *evaluates* one. A constraint bound to the wrong field, carrying a bound off by a
factor of ten, or scoped to the array where it belongs on the items, compiles perfectly and protects
nothing. So `ProtovalidateConformanceTests` states a bound and then sends a message that breaks it.
It found a real defect the moment it first ran (see the `repeated.items` gotcha in §8).

The runtime is Go because protovalidate has no .NET implementation, and reimplementing it in C# would
only prove the generator agrees with *our reading* of the spec — which is the assumption under test.
`tests/ProtoDesigner.CodeGen.Tests/Protovalidate/` holds `harness.go` plus its `go.mod`/`go.sum`, all
checked in; the harness loads a descriptor set, builds the message with `dynamicpb` and validates it, so
no `protoc-gen-go` step exists and it never needs regenerating alongside a schema. Its dependencies live
in Go's global module cache, **not** in this repo — so `protobuf/protovalidate-go/` is a staging
directory and deleting it costs nothing. Deleting all of `protobuf/` also removes protoc and
`validate.proto`, which turns the second and third checks red until you set their skip variables.

```
src/
  ProtoDesigner.Core/            model, layout, validation, IR — no UI, no language bias
    Model/  Layout/  Validation/  Ir/
  ProtoDesigner.Application/     IProjectRepository, IEditCommand + CommandJournal,
                                 GenerationScopes, CodeGenerationService
  ProtoDesigner.Persistence.Json canonical ID-keyed JSON + migration chain
  ProtoDesigner.Application/     …also ProtobufCompatibility (the export gate) and WireSizePolicy
  ProtoDesigner.CodeGen/         IProtocolGenerator, GeneratorCatalog, BitBuffer + ReferenceCodec,
                                 C/ (full codec)   CSharp/ (declarations only)   Proto/ (schema)
  ProtoDesigner.Cli/             validate / generate / targets
  ProtoDesigner.Wpf/             the editor
tests/                           one suite per src project
  …CodeGen.Tests/Golden/         checked-in expected output: C headers *and* .proto schemas
  …CodeGen.Tests/Protovalidate/  harness.go + go.mod/go.sum — the Go runtime check, all in git
samples/telemetry.pdproj         a worked example exercising most features
samples/protobuf-demo.pdproj     aimed at the protobuf target: two exportable messages covering every
                                 constraint shape, plus one bit-packed and one quantized message that
                                 the gate must refuse by name
docs/shared-storage-design.md    Phase 6 design note — read before building any of it
protobuf/                        gitignored toolchain: bin/protoc, include/, validate.proto
```

**Model additions since the original Phase 0 sketch** (§4 below predates them):

- `ParameterType.WireBits` / `EnumType.WireBits` — a type's default wire size, propagated onto new
  bindings by `WireEncodingPropagator`. It does **not** violate rule 1: a `FieldBinding`'s encoding
  still wins, this is only the default a new binding starts from.
- `Module` (`ModuleId`, name) on a bus, and `MessageRoute(From, To)` on a message. Direction is
  modelled now — it is what `GenerationScopes.ForModule*` selects on.
- `IrMember` alongside `IrField`: **flat for the wire, nested for the host**. Fields stay flattened
  because offsets are defined over leaves in wire order; members describe the struct the user actually
  declared, so a `Header` used by ten messages is emitted once and referenced by name. **A generator
  emitting declarations walks `Members`; one emitting a codec walks `Fields`.**
- `IrPrimitive` — named primitives reach the IR now, carrying `WireBits` and `Range`. They declare no
  type in any target (the host kind is a built-in) but their size and limits are the things a caller
  cannot otherwise derive.
- `IrMember.Range` / `IrMember.ProtoFieldNumber` — a target that cannot reproduce a narrow *encoding*
  can often still state the *constraint*, and protobuf field numbers must survive regeneration.
- `ArrayLength.MinimumCount` (persisted as an optional `minCount`) — a declared floor on a variable
  array. It is not a wire mechanism: it raises `MessageLayout.MinBits` and becomes protovalidate's
  `min_items`, and the C codec ignores it. `IrMember.ArrayMinCount` carries it, which is what let
  `ProtoGenerator` stop deciding "is this exact?" by string-matching a human-readable note.
- `FieldBinding.ProtoFieldNumber` (`int?`, persisted). Not a violation of rule 2: a field number is not
  a position on the wire — protobuf puts it in the tag explicitly — so it is declarative intent the user
  owns, in the same category as `Message.WireId`.

**Target-specific generator options.** `GeneratorOptions` carries `TargetOptions`, a string bag, plus
`Flag()`/`Value()`/`With()` helpers. Each `IProtocolGenerator` *declares* what it accepts via
`Options` (`GeneratorOption` records), so the CLI (`--option key=value`, repeatable) and the Generate
dialog render them without knowing which target is selected. A generator reads its own keys and ignores
the rest. This exists because widening the shared record per language is exactly what the "no language
bias" boundary forbids. `IProtocolGenerator.CoversEveryMessage` is the other declaration: false means
the target borrows a foreign wire format and the caller must narrow the scope.

**`EnumType.Synthetic` is a type you can put on a field whose members the bus fills in.**
`SyntheticEnum.MessageId` becomes every message's `WireId`; `SyntheticEnum.ModuleId` becomes every module,
numbered from 1. `Members` stays empty on the model and `IrBuilder` fills it per bus, so a shared `Header`
carrying a message-id field means the right thing on every bus that uses it, and renaming a message can
never leave a hand-written list stale. The marker persists (declarative intent — the user chose this field
to be "the message id"); the member list never does (derived). **Seeded into every project by `SeedBuiltIns`** — there is
no button, because the bus's own identities are not something a user should have to know to create. There
is deliberately no editor dialog either: the members are not yours to edit, and offering that back would
restore the staleness the type removes.

The generated name takes the bus with it (`MessageId` → `MainMessageId`), because the type is
project-wide but its contents are per-bus: one C project including headers from two buses would otherwise
have two different enums with one name. So **renaming the bus, a message, a module, or changing a
`WireId` all change the emitted enum**, and nothing is stored that could disagree. When a field is typed
as one, the always-on `<Bus>MessageId` in the bus header is skipped — same identifier, and defining it
twice would not compile.

**Two identity enums are also synthesized per bus whether or not any field uses them, and neither is stored.** `<ns>_<Bus>MessageId` lists every
message carrying a `WireId`, with a `_NotAssigned = 0` sentinel and a `<Bus>_MessageIdFromWire()` lookup;
`<ns>_<Bus>ModuleId` does the same for the bus's modules, plus a `<Bus>_ModuleName()` lookup. Both are
derived at generation, so renaming the bus, a message or a module — or changing a wire id — updates them
with nothing left to go stale. Both are **bus-scoped on purpose**: one C project may include headers from
several buses, and a `Sensor` on each would otherwise be the same identifier twice.

The two differ in one way that matters. A message id is `Message.WireId` — declared state a deployed peer
reads off the wire. A **module id is declaration order**, because nothing ever puts one in a frame:
`MessageRoute` is a design-time statement about who talks to whom. So removing a module renumbers the
ones after it, which is harmless between two ends built from the same header. If a module id ever needs
to survive a reorder, it has to become declared state on `Module` — that is a new decision, not a bug.

**Checksums and CRCs are not modelled — do not add them back.** They were removed deliberately in
schema v2. Width, polynomial and technique vary per message, teams already have vetted routines or a
hardware unit, and every CRC-aware abstraction attempted (compute-it-for-you, coverage spans, a callback
hook, a placement warning) added surface without adding certainty. A checksum is an ordinary field the
caller fills in.

What the generators *do* emit is size information, and nothing beyond it:
`<Type>_OnWireBits`/`_OnWireBytes` per type, and `<Msg>_OnWireLength(msg)` per message. A trailing field
is then `OnWireLength(msg) - <Type>_OnWireBytes`, with no offset stored anywhere.
`Core/Ir/WireLength.cs` is the single definition both generators mirror; `WireLengthTests` pins it
against what the reference codec actually writes, so the emitted formula cannot drift from the encoder.

**The protobuf target is gated, and the gate is the feature.** `proto` emits a `.proto` schema so one
definition serves both the embedded link and a protobuf backend. It is a *different wire format*, not a
second encoding of ours, so it is refused per message for anything protobuf cannot represent:
sub-byte widths, and transforms with `Scale != 1`. An **offset is fine** — protobuf sends the value, not
the wire code, so the offset was only a width trick and the range survives as a protovalidate
constraint. `ProtobufRules` is deliberately **not** in `Validator.DefaultRules`: a bit-packed message is
exactly what this tool is for and must never fail ordinary generation. `ProtobufCompatibility` is the
single place the gate lives; `CodeGenerationService` narrows the scope for any generator whose
`CoversEveryMessage` is false and reports what it left out. `FieldBinding.ProtoFieldNumber` is persisted
and assigned through an undoable command, never by the generator — a number that moves breaks every
deployed peer silently.

**The generated code is C, not C++, and must stay freestanding** — no heap, no exceptions, no std
containers. It targets microcontrollers where an allocation is a fault, not a slowdown, and where C is
often the only option. `CFreestandingTests` enforces the freestanding part; the cross-checks enforce
that one header compiles as C *and* as C++, which is why there is no separate C++ target to maintain.
C has no namespaces, so `GeneratorOptions.Namespace` becomes a symbol prefix, and no overloading, so
every emitted function name must be unique.

**Known gaps, honestly:**
- The C# target emits declarations and the wire layout as comments; no encode/decode yet.
- The WPF layer has no automated tests. Everything below it does. Dialog logic that is really *policy*
  should be extracted so it can be — `WireSizePolicy` was pulled out of the primitive editor for exactly
  this reason, after a defect that was untestable where it lived.
- The Generate dialog reports which messages a target left out, but does not yet let you tick individual
  messages for export.
- Nothing consumes a generated `.proto` end to end (`protoc --csharp_out`, populate, serialize,
  deserialize). The schema is proven valid and its constraints proven to fire; it is not proven *usable*
  by a generated stub. See the note under "Open work" before spending time on it.
- `FillRemaining` and `Terminated` *decode* by asking the caller for the count rather than scanning for the
  sentinel or consuming the remainder. Encode is correct for both, and both reach the IR and the C generator
  correctly — only the decode strategy is missing.

The corpus is shaped for wire layouts and declares almost no ranges, so it would show nothing if every
protovalidate constraint vanished. `ProtovalidateFixture.BuildProject()` is the fixture shaped for the
rule groups instead — one message per constraint family, covering `uint32`/`int32`/`uint64`/`int64`/
`float`/`double`, `bool`, `char`, scalar and `repeated` enums, fixed and dynamic arrays, an offset-only
field and a deliberately unconstrained full-span one. It is goldened as `Golden/constraints.proto`, which
is what makes a dropped or rescoped rule a visible diff. Add new rule shapes there, not to `Corpus` —
widening the corpus drags the C golden and cross-check suites along for a different axis entirely.

### Verifying the claims (these are not obvious, and were hard-won)

A conformance test that cannot fail is decoration. Each of these was proven by deliberately breaking
something and watching it go red — do the same before trusting a change here.

- **Is the C cross-check really compiling?** Point `PROTODESIGNER_PROTOC`-style env vars at nothing, or
  temporarily corrupt the emitted code. With MSVC present and no skip variable, `CCrossCheck` compiles
  the *same driver source* twice (`/TC` then `/TP`) and runs both.
- **Is protoc really running?** `PROTODESIGNER_PROTOC=C:/nonexistent/protoc.exe dotnet test --filter
  ProtocConformanceTests` must turn every case red. If it stays green, the test is skipping.
- **Are the protovalidate constraints real, or just syntactically accepted?** Compile to a descriptor
  set and decode it — this proves the options are *attached with the right rule tags*, not merely
  parsed:

  ```bash
  protoc --proto_path=OUT --proto_path=protobuf/include --descriptor_set_out=OUT/x.desc OUT/*.proto
  protoc --decode=google.protobuf.FileDescriptorSet --proto_path=protobuf/include google/protobuf/descriptor.proto < OUT/x.desc
  ```

  `validate.proto` must be staged at `OUT/buf/validate/validate.proto` first, because protoc resolves
  imports by path, not package. Verified tags in `FieldRules`: `int32 = 3`, `uint32 = 5`, `enum = 16`,
  `repeated = 18`; inside a scalar rule set, `lte = 3` and `gte = 5`. A negative `gte` appears as a
  two's-complement varint (`-90` → `18446744073709551526`), which is correct, not a bug.
- **Do the constraints actually reject anything?** The descriptor decode above proves the right tag with
  the right value; it still does not prove the rule *fires*. Three mutations, each verified to go red:

  | Mutation | Expected |
  |---|---|
  | `PROTODESIGNER_GO=C:/nonexistent/go.exe` | all 35 runtime cases red (the protoc cases stay green — they do not need Go) |
  | the same, plus `PROTODESIGNER_SKIP_PROTOVALIDATE=1` | green, and nothing validated |
  | widen a bound: `Number(range.Max + 100, …)` in `ProtoGenerator.Constraints` | 7 cases red across three rule groups |

  If the first stays green the test is skipping when it should be failing, which is the failure mode the
  whole fail-loudly policy exists to prevent.

### Open work, in the order I would take it

1. **Right-click a field to edit its type**, offering what right-clicking the type in the library does.
2. **Per-message export checklist** in the Generate dialog. It currently reports what a target left out;
   it does not let you tick individual messages.
3. **C# encode/decode.** Declarations and `OnWireLength` exist; the codec does not.
4. **`FillRemaining` / `Terminated` decode** — scan for the sentinel or consume the remainder instead of
   asking the caller for a count. This is the *only* remaining gap in those two: they reach the IR and the
   C generator correctly, which `DynamicArrayKindTests` now pins.
5. **Test the built-in ID enums.** Deferred deliberately, not forgotten: `MessageId`/`ModuleId` are seeded
   into every project and their members are derived per bus, so the cases to cover are a renamed bus, a
   renamed message, a changed `WireId`, a renamed module, and a project saved before they existed loading
   without them. None of that is covered yet.
6. **Match the ID enums to the requested spelling, or decide not to.** The shape asked for was
   `<BUS_NAME>_MESSAGE_ID_NA` / `<BUS_NAME>_<MODULE_NAME>`; what is emitted is
   `<ns>_<Bus>MessageId_NotAssigned` / `<ns>_<Bus>ModuleId_<Module>`, which is the C target's own naming
   convention and already carries the bus scope the request was after. Renaming would churn every golden
   and break any deployed code that switches on these. Worth a decision, not an assumption.
7. **Wire-compatibility diffing** — compare two versions' `MessageLayout`s and report which changes
   break a deployed decoder (reorder, narrow, widen, endianness, `WireId` change) versus which are safe
   (rename anything — identity is an ID). This needs no database, works against the last git commit, and
   is the thing git structurally cannot do for a binary protocol. Recommended before any of Phase 6.

**Settled, so that they are not reopened as questions:**

- **Byte order and bit order are both bus-level, and nowhere else.** One bus is one agreement about wire
  format; two modules on it disagreeing is a broken link, not a configuration. `LayoutOptions` still
  carries a message- and field-level override and `EffectiveLayoutOptions.Resolve` still walks the whole
  chain — the editor simply does not offer them, and the view models read the resolved answer so the grid
  cannot drift from what is generated. If per-link byte order is ever wanted, the modelling answer is two
  buses with the shared module as a gateway, not an override: a receiver identifies a frame by its message
  id and cannot know which sender produced it, so an edge-scoped format is undecodable.
- **A duplicate message `WireId` is reported, not refused.** The edit stands and an inline red note appears
  under the Message ID box. This follows rule 5 — the validator reports, it does not block — and the
  editor never silently overrides a keystroke.

**Compiling the schema is a step after generation, never inside a generator.**
`ProtocCompiler` (Application) runs the real protoc over the written `.proto` files, producing C++, C#,
Java or Python on request — `--protoc-out <lang>` on the CLI, checkboxes in the Generate dialog. It lives
outside the generator on purpose: an `IProtocolGenerator` is a pure function from IR to text, which is
what makes golden files meaningful and lets the whole suite run with no toolchain installed. Shelling out
from inside one would give up both. **There is no C backend** — protobuf has never had one; `--cpp_out`
emits C++ needing libprotobuf and the heap, and a C consumer wants either this project's own freestanding
`c` target or a third-party generator like nanopb. When constraints are on, Buf's `validate.proto` is
staged and compiled too, because the emitted `main.pb.h` carries
`#include "buf/validate/validate.pb.h"` and would not build without it; that is reported, since its C#
form alone is close to a megabyte.

**The protobuf target is finished and tested; do not reopen it looking for gaps.** Schema compiles,
constraints compile against the real extension, constraints are *enforced* by a real runtime, the gate
refuses and names, field numbers survive reorder and round-trip, goldens make output changes visible, and
both samples run in CI. The one thing deliberately left undone is a round-trip through a generated
language (`protoc --csharp_out`, populate, serialize, deserialize): it would prove the schema is
*consumable*, which nothing currently checks, but every constraint and every field number is already
pinned by something sharper. Do it only if a consumer actually reports trouble.

**A note on endianness, since it comes up:** the *host's* byte order never matters and you never need to
know it. The generated runtime assembles bytes with shifts, not `memcpy` of a machine word, so the same
header produces identical bytes on a big- or little-endian CPU. What matters is the *wire* endianness,
which is a property of the protocol that both sides already agree on by sharing the generated header.

---

## 4. API surface that already exists

Signatures you'll build against. Read the files for the full contract; this is the shape.

**Types** (`Model/Types.cs`)
```csharp
abstract class TypeDefinition { TypeId Id; string Name; string? Description;
                                abstract TResult Accept<TResult>(ITypeVisitor<TResult>); }
interface ITypeVisitor<out TResult> { TResult VisitParameter/VisitEnum/VisitStruct/VisitArray(...); }

class ParameterType : TypeDefinition { PrimitiveKind Kind; NumericRange? Range; bool IsConstant; }
class EnumType      : TypeDefinition { PrimitiveKind UnderlyingKind; bool IsFlags;
                                       List<EnumMember> Members; EnumType With(string,long);
                                       NumericRange? MemberRange; }
class StructType    : TypeDefinition { List<FieldBinding> Fields; StructType With(params FieldBinding[]); }
class ArrayType     : TypeDefinition { TypeId ElementTypeId; ArrayLength Length; }

abstract record ArrayLength { int Capacity; bool IsDynamic;
    record Fixed(int Count);
    record CountFromField(FieldId CountFieldId, int MaxCount);
    record LengthPrefixed(int PrefixBits, int MaxCount);
    record Terminated(IReadOnlyList<byte> Sentinel, int MaxCount);
    record FillRemaining(int MaxCount); }
```

**Fields & encoding** (`Model/Fields.cs`)
```csharp
class FieldEncoding { int? BitWidth; Endianness? Endianness; BitOrder? BitOrder;
                      bool AllowBitPacking; int? AlignmentBits; ScalarTransform? Transform;
                      static Natural()/Packed(int)/Sized(int)/AlignedTo(int); FieldEncoding Clone(); }
class FieldBinding  { FieldId Id; string Name; TypeId TypeId; FieldEncoding Encoding;
                      object? DefaultValue; string? Description; }

```

**Structure** (`Model/Structure.cs`)
```csharp
class Project { string Name; int SchemaVersion (const CurrentSchemaVersion=1);
                LayoutOptions Options; TypeLibrary Types; List<Bus> Buses;
                EffectiveLayoutOptions OptionsFor(Bus, Message); }
class Bus     { BusId Id; string Name; Transport Transport; LayoutOptions Options;
                List<string> Modules; List<Message> Messages; }
class Message { MessageId Id; string Name; int? WireId; LayoutOptions Options;
                List<FieldBinding> Fields; Message With(...); FieldBinding? Find(FieldId);
                void MoveField(FieldId,int); bool RemoveField(FieldId); }
class TypeLibrary { T Add<T>(T); TypeDefinition this[TypeId]; bool TryGet(...); IReadOnlyCollection<TypeDefinition> All; }
record EffectiveLayoutOptions(Endianness, BitOrder, int DefaultAlignmentBits,
                              BitPackingMode, bool PadToByteBoundary) {
    static Default; static Resolve(params LayoutOptions?[] mostSpecificFirst); }
```

**Layout** (`Layout/*.cs`)
```csharp
static class BitMath { const MaxScalarBits=64;
    int AlignUp(int,int); int BitsForUnsignedMax(ulong); int BitsForSignedRange(long,long);
    ulong MaxUnsigned(int); int RequiredBits(NumericRange, ScalarTransform?);
    int RequiredBits(EnumType, ScalarTransform?); }

class LayoutEngine { MessageLayout Compute(Project, Bus, Message);
                     MessageLayout Compute(Message, TypeLibrary, EffectiveLayoutOptions?); }

class MessageLayout { IReadOnlyList<LayoutRegion> Regions; IReadOnlyList<LayoutNode> Nodes;
                      int MinBits/MaxBits/MinBytes/MaxBytes; bool IsFixedSize/HasVariableRegions;
                      IEnumerable<LayoutNode> Flatten()/Values()/Padding();
                      LayoutNode this[string path]; bool TryGet(string, out LayoutNode?); }
class LayoutNode   { string Path; LayoutNodeKind Kind; TypeId? TypeId; FieldId? FieldId;
                     int RegionIndex/BitOffset/BitWidth/ElementBits; int? ElementCount;
                     Endianness; BitOrder; ScalarTransform Transform; IReadOnlyList<LayoutNode> Children; }
class LayoutRegion { int Index; LayoutRegionKind Kind (Fixed|Variable); int MinBits/MaxBits;
                     int ElementBits/MaxElements; FieldId? CountFieldId; int PrefixBits; }
class LayoutException : Exception { string? Path; }
```

---

## 5. Conventions

- **Project layout.** New assemblies go under `src/` and mirror the dependency direction in
  section 1. Suggested names as they land: `ProtoDesigner.Application`,
  `ProtoDesigner.Persistence.Json`, `ProtoDesigner.CodeGen`, `ProtoDesigner.Cli`,
  `ProtoDesigner.Wpf`. Don't split `Core` further unless coupling forces it — folders already
  separate `Model` from `Layout`.
- **Walk the type tree with the visitor.** Layout, validation, IR building, and every generator
  visit the same structure. Implement `ITypeVisitor<T>` rather than writing a fifth `switch` on
  type kind — adding a type kind should become a compile error across all visitors, not a bug
  hunt.
- **Tests: extend `ModelBuilder`, not the setup.** `ModelBuilder` builds types/messages tersely;
  `LayoutAssert` (`.At`, `.Sized`, `.Spans`, `.Order`) phrases assertions in layout terms. Add
  helpers there so new tests stay one-liners. Every test starts from a fresh builder → tests are
  independent.
- **Diagnostics carry stable numeric codes** (`PD0142`) from day one — they're how the CLI
  reports, how tests assert, and how a rule gets suppressed later. Never renumber a shipped code.
- **Prefer minimal, direct implementations.** No speculative generality, no patterns without a
  present need. The architecture is already factored for extension; individual classes should be
  plain.
- **Wire format ≠ C struct.** Fields are byte-aligned by default, not naturally aligned. Natural
  alignment is opt-in per field via `AlignmentBits`. Keep this in mind when writing generators —
  don't assume `#pragma pack` semantics.

---

## 6. Roadmap — phases 1 through 6

Ordered by dependency. Each phase should stay shippable and fully tested before the next starts.
This ordering supersedes any earlier sketch: **validation first** (needed before codegen and for
live UI feedback), then a headless product (persistence + CLI), then codegen, then the UI, then
the second generator + advanced features, then collaboration.

### Phase 1 — Validation

**Goal.** Turn "is this model correct?" into a first-class, testable pass that produces
diagnostics. This is the policy layer the engine deliberately lacks.

**Build.** In `Core/Validation/`:
```csharp
enum Severity { Info, Warning, Error }
record EntityPath(...);                 // e.g. bus/message/field chain, human-readable
record QuickFix(string Description, Action<Project> Apply);   // optional
record Diagnostic(string Code, Severity Severity, string Message, EntityPath Target, QuickFix? Fix);
interface IValidationRule { IEnumerable<Diagnostic> Validate(ValidationContext ctx); }
class ValidationContext { Project Project; /* resolved lookups, layout access, cache */ }
class Validator { IReadOnlyList<Diagnostic> ValidateFull(Project);
                  IReadOnlyList<Diagnostic> ValidateEntity(Project, /*changed entity*/); }
```
Two passes: **incremental** (rules declare what entity/field they depend on; run on the edited
entity for live UI feedback) and **full** (on save, before codegen, in CI). `Error` blocks
codegen; `Warning`/`Info` don't.

Rule catalogue (assign codes as you go):
- Duplicate `Name` within a scope; duplicate `WireId` within a bus.
- Cycles in the type reference graph (DFS over type refs — a fixed layout can't contain itself).
  The engine already rejects these; the validator should report them *before* the engine runs.
- Encoding feasibility: `BitMath.RequiredBits(range-after-transform) <= BitWidth`; every enum
  member representable in the declared bits; `DefaultValue` inside the range.
- Dynamic arrays: count field exists, **precedes** the array, is an unsigned integer, and its max
  value ≥ declared capacity; length-prefix wide enough for capacity.
- Alignment vs packing conflicts.
- Frame budget: `MessageLayout.MaxBits` vs the bus transport's frame size (Ethernet MTU, UART
  frame). Report as `Warning` unless hard-over.
- Unreferenced types → `Info`.

**Acceptance.** A rule-per-file test suite: each rule has a project that trips it (asserting the
`Code`) and a clean project that doesn't. A "valid corpus" of a dozen realistic projects that
must produce zero `Error`s. Round-trip with the engine: anything the validator passes, the engine
must lay out without throwing.

**Don't.** Don't throw for validation failures — return diagnostics. Don't duplicate the engine's
recursion check logic; share it (extract a graph walk into `Core` if needed) or call it.

---

### Phase 2 — Persistence + CLI

**Goal.** A usable, scriptable product with no UI: load a project file, validate it, (later)
generate from it.

**Build.**
- **JSON storage** in `Persistence.Json`. Recommendation stands: **JSON, not YAML or SQLite**
  for now (diffable/mergeable in git, zero-dep via `System.Text.Json` source generators, no
  ambiguous-scalar footguns). Rules:
  1. **Flat, ID-keyed collections**, not a deep tree: `"types": { "<id>": {…} }`,
     `"messages": { "<id>": {…} }`. Each entry becomes a DB row later with no reshaping.
  2. `schemaVersion` at the top + a **migration chain** applied on load:
     ```csharp
     interface IProjectMigration { int From { get; } int To { get; } JsonNode Migrate(JsonNode); }
     ```
     Write it at v1 now, while trivial.
  3. **Canonical writer** — stable key order (by ID), 2-space indent, LF, invariant number
     formatting. Unrelated entities never move → merge conflicts only where two people touched
     the same thing.
  4. **No offsets, no computed sizes, no cached layouts** in the file.
  5. One file per project (`.pdproj`) to start.
- **Repository port** in `Application`:
  ```csharp
  interface IProjectRepository { Project Load(string path); void Save(Project, string path); }
  ```
  Everything above it (CLI, later WPF) depends on the port, so swapping JSON for SQL in Phase 6
  is invisible to callers.
- **CLI** (`ProtoDesigner.Cli`): `validate <file>` (exit non-zero on `Error`, print diagnostics
  with codes) and `generate <file> --target cpp --out <dir>` (wired once Phase 3 lands).

**Acceptance.** Round-trip tests: load → save → load produces an identical model (compare by
value, ignoring nothing that matters). A canonical-form test: saving the same model twice is
byte-identical, and reordering unrelated entities in memory doesn't change the file. A v0→v1
migration test on a fixture. CLI smoke tests asserting exit codes and that codes appear in output.

**Don't.** Don't persist derived data. Don't let the CLI reach into `LayoutEngine`/generators
except through the application use cases.

---

### Phase 3 — IR + C++ generator

**Goal.** Generate C++14 encode/decode/parse code. Prove the whole chain end to end against
your own primary target first.

**Build.**
- **The IR** in `Core/Ir/` — a resolved, validated, layout-annotated snapshot with **no editing
  concepts** (no library refs, no overrides, no unresolved names, no `null` inheritance). It is
  the one-way contract generators consume.
  ```csharp
  record ProtocolIr(string Name, IReadOnlyList<IrMessage> Messages, /* shared type decls */ …);
  record IrMessage(string Name, int? WireId, int BitsMin, int BitsMax,
                   IReadOnlyList<IrField> Fields, IReadOnlyList<IrRegion> Regions);
  record IrField(string Path, /* concrete offset expr */, int BitWidth, Endianness, BitOrder,
                 ScalarTransform Transform, /* length rule for arrays */ …);
  class IrBuilder { ProtocolIr Build(Project, Bus); }   // runs the layout engine, flattens, resolves
  ```
  IR offset expressions are compile-time constants inside `Fixed` regions and cursor-relative
  inside `Variable` regions — carry the `LayoutRegion` structure through so the generator knows
  which is which.
- **Generator interface** in `CodeGen`:
  ```csharp
  interface IProtocolGenerator { string Id { get; }
      GeneratedFileSet Generate(ProtocolIr ir, GeneratorOptions options); }
  ```
  Use **Scriban** templates. Emit header(s) with a struct per message plus `encode`/`decode`
  (and `parse`-from-interface) functions honoring width, endianness, bit order, transform, and
  each array length rule.

**Absolute rule for generators.** A generator reads **only** the IR. It never reaches back into
`Project`/`Message`/`FieldBinding`. That one-way boundary is what makes "add Rust later without
touching the editor" true.

**Acceptance.** **Golden-file tests**: a corpus of projects → generate → diff against checked-in
expected output; changes to output are visible in review. **Round-trip tests**: encode a value
with the generated C++, decode with a hand-written reference (and later with the C# generator),
assert equality — especially for the nasty cases (bit-packed enum crossing a byte boundary,
dynamic array followed by a fixed field, bit-reversed field).

**Don't.** Don't generate from an unvalidated model. Don't leak editor types into templates.

---

### Phase 4 — WPF editor

**Goal.** The interactive editor. This is the least reversible layer, which is why it comes after
the model, storage, validation, and codegen are settled.

**Build.**
- **MVVM in the WPF layer only.** The domain stays plain POCOs; view models wrap them.
- **Command journal from day one** in `Core/Commands/` (or `Application`):
  ```csharp
  interface IEditCommand { void Apply(Project); void Undo(Project); string Describe(); }
  class CommandJournal { void Do(IEditCommand); void Undo(); void Redo(); bool IsDirty; }
  ```
  Every mutation goes through it → undo/redo, dirty tracking, an audit trail, and the operation
  stream Phase 6 collaboration needs. **The domain never mutates behind the command bus.**
- Views: a tree of buses/messages, a field grid (add/delete/rename/resize/reorder), and — the
  feature that makes this better than a spreadsheet — a **live byte/bit map** of the computed
  `MessageLayout` that updates on every edit. Run the incremental validator and surface
  diagnostics inline against the offending field.

**Acceptance.** View-model tests (headless) covering command apply/undo/redo and that edits route
through the journal. The bit-map renders from `MessageLayout.Flatten()`/`Regions` — test the
mapping function, not the pixels.

**Don't.** Don't mutate model objects directly from event handlers. Don't recompute layout by
hand in the UI — call `LayoutEngine` and render its output.

---

### Phase 5 — C# generator + advanced protocol features

**Goal.** Second generator (proves the IR is truly language-agnostic) plus the remaining protocol
options.

**Build.** A C# `IProtocolGenerator` behind the same interface (this is where the IR earns its
keep — if it needs changes to support C#, the abstraction was leaky, so fix the IR, not the
editor). The declarations and the on-wire length API already exist; what remains is encode/decode.
Advanced features: automatic `WireId`/Message-ID assignment, MTU/frame-budget checks promoted from
warnings to codegen options, configurable alignment policies.

**Acceptance.** Extend the round-trip corpus to C↔C# (encode in one, decode in the other).
Golden files for the C# target.

---

### Phase 5b — Protobuf schema target (done)

Built out of a long design conversation whose conclusions matter more than the code:

**Should you just use protobuf instead of this tool?** If you control both ends, can choose the wire
format, and bandwidth is not tight — **yes, and this tool is over-engineering.** Protobuf's tag-length-
value + varint design costs ~2 bytes for a bool and gives no stable byte offsets, no fixed message size,
and no sub-byte fields. This tool exists for the cases protobuf structurally cannot serve: a fixed frame
budget, a hardware-defined frame format you do not get to choose, or a link where per-field overhead is
the problem. Say so plainly if asked again rather than defending the codebase.

**Why the target exists anyway:** one definition, two consumers — the embedded link on the C codec, a
backend or dashboard on protobuf, without a second hand-written schema that drifts.

**What was rejected on the way here:** packing sub-byte fields into an opaque `bytes` blob with
generated accessors (works, but the blob is unreadable to every other protobuf consumer and gets no
schema evolution — you pay protobuf's cost for none of its benefit); emitting `.pb.cc`/`.pb.h`
ourselves; bundling protoc; CEL expressions. If protobuf interop is genuinely wanted for dense
messages, the coherent shape is protobuf as the *envelope* (`bytes payload = 1;`) with our codec as the
payload — and that needs no generator at all, just one hand-written `.proto`.

---

### Phase 6 — Shared storage & collaboration

**Goal.** Move from local files to a shared database with multi-user versioning.

**Build.** A SQL `IProjectRepository` implementation — the flat ID-keyed JSON collections explode
into `types`/`messages`/`fields` tables (or land in a `jsonb` column) with **no model reshaping**,
because Phase 2 designed for exactly this. Add a per-entity `revision` for optimistic concurrency.
Ship *operations* (the `IEditCommand` stream) rather than whole documents for merge; a diff/merge
view over entities. This is sufficient for a schema editor — **you do not need CRDTs**.

**Acceptance.** Repository contract tests that both the JSON and SQL implementations pass.
Concurrency tests on the revision check. Merge tests on divergent edit streams.

---

## 7. Deferred vs rejected

**Deferred (belongs to a later phase, not the engine):** everything in §6. Notably, the engine
throws on *impossible* layouts but leaves *wrong-but-computable* to the Phase 1 validator —
duplicate names/IDs, width-too-small-for-range, out-of-range defaults, MTU
overflow.

**Rejected (intentionally not in the design — don't add):**
- Offsets stored on the model or in files (breaks rule 2).
- Encoding baked into types (breaks rule 1).
- Name-based references (breaks rule 3).
- Any modelling of checksums or CRCs — computing them, coverage spans, callback hooks, or placement
  rules. Removed in schema v2; see §3. Per-type wire sizes are the whole of what the tool contributes.
- Generators reading the editing model instead of the IR (breaks the codegen boundary).
- CRDTs for collaboration (over-engineered for a schema editor; operations + per-entity revision
  suffice).
- YAML/SQLite as the initial local format (chose JSON for diff/merge + zero-dep; SQLite arrives
  as the *shared* backend in Phase 6, not the local file).
- A separate C++ generator. The C target's header compiles as C *and* C++, so a second one would be a
  near-identical thing to keep correct for no capability the first lacks.
- Emitting protobuf output that silently drops what it cannot express. Anything unrepresentable is
  refused per message with the offending field named — see `ProtobufRules`.
- Vendoring toolchains (MSVC, protoc, Go). Tests locate them and **fail loudly** when absent; a green
  suite that compiled nothing is worse than a red one.
- A hand-written stub of `buf/validate/validate.proto` to make the constraint test runnable without the
  real file. A stub only checks the generator against a guess at protovalidate's shape, and passes for
  exactly the reason it is wrong.
- A C# reimplementation of protovalidate to avoid the Go dependency. It would only prove the generator
  agrees with our reading of the spec, which is the assumption the check exists to test — the same
  mistake as the stub, one layer up.
- Treating "protoc accepted it" as proof a constraint works. It is proof the option *parses*, nothing
  more, and the `repeated.items` bug in §8 got through exactly that way.

---

## 8. Gotchas

- **It never compiled.** Run `dotnet build && dotnet test` before touching anything; fix red
  first. (See the warning in §3.)
- **Byte-aligned by default, not naturally aligned.** A `u32` after a `u8` sits at bit 8, not
  bit 32. Natural alignment is opt-in per field. Tested in `FixedLayoutTests`.
- **`FieldEncoding.BitWidth` on an array binding is the width of one *element***, not the whole
  array. Tested in `CompositeTests.An_enum_array_packs_per_element`.
- **Region indices are contiguous and match `LayoutNode.RegionIndex`.** Two adjacent dynamic
  arrays produce two `Variable` regions with **no empty `Fixed` region between them** — the
  engine suppresses zero-width fixed regions except the single region of an otherwise-empty
  message. Tested in `DynamicArrayTests`.
- **A length prefix is `LayoutNodeKind.LengthPrefix`, not a `Parameter`, and is not a field.** The engine
  inserts an `x.__length` node for a `LengthPrefixed` array. It occupies wire space but the user never
  declared it, cannot name it and cannot encode it — it is framing, in the same category as padding, and it
  carries no `TypeId`. `MessageLayout.Values()` excludes it and `IrBuilder` skips it; the information lives
  on the array instead, as `IrArrayInfo.PrefixBits`. Modelling it as a `Parameter` is what made
  `LengthPrefixed` arrays unusable for a long time: `IrBuilder` threw on the missing `TypeId`, and had it
  not thrown it would have become a phantom `IrField`, shifting every `CountFieldIndex` after it by one.
  The protobuf rules would also have reported a width against a field called `payload.__length`.
- **`Terminated` and `FillRemaining` were never broken the way `LengthPrefixed` was** — only
  `LengthPrefixed` emits a synthetic node. `DynamicArrayKindTests` pins all five variants through the IR
  and the C generator so this stops being a guess. Their real gap is decode strategy, nothing else.
- **A count field must be laid out *before* its dynamic array** (earlier in the same message,
  including inside an earlier struct). The engine enforces this via a "seen fields" set;
  forward references throw. The validator should catch it first with a friendly message.
- **Sub-byte element strides are rejected while byte padding is on**, because offsets after the
  array wouldn't be byte-addressable. They're fine in a pure bit-stream layout
  (`PadToByteBoundary = false`). Tested both ways in `DynamicArrayTests`.
- **`ScalarTransform` uses `decimal`** so the full 64-bit integer domain is exact (a `double`
  loses precision past 2^53). Keep wire-code math in `decimal`/integer, not `double`.
- **`LayoutNode` and `LayoutRegion` use `required` members** (C# 11 / .NET 7+). Every
  construction site must set them; that's by design, not a bug to "fix" by making them nullable.
- **The serializer writes messages in canonical (ID) order, not declaration order.** Message order is
  not wire-significant so this is deliberate and diff-friendly — but do not be surprised when a
  round-tripped project emits its messages in a different order than you added them. **Field** order is
  preserved, and must be: field order *is* wire order.
- **`ProtobufRules` is not in `Validator.DefaultRules`.** Run it with `new Validator(ProtobufRules.All)`,
  or through `ProtobufCompatibility`. Registering it in the default set would make a bit-packed
  message — the thing this tool is for — fail ordinary C generation.
- **The protobuf gate reads the computed layout, not `FieldEncoding` directly**, because a binding's
  width is usually null and inherited from the message, bus or project. `ProtobufRules.ValueNodes`
  walks `MessageLayout.Values()`, which has the resolved widths and reaches inside structs and arrays.
- **Protobuf gets 32- and 64-bit integers only.** `ProtoNarrowIntegerRule` (PD0074) refuses any integer
  field whose wire width is not 32 or 64 — `u8`, `u16`, `i16`, `char` at its natural width, and anything
  narrowed. Nothing is *lost* by widening a `u16` to `uint32`, and the range still survives as a
  constraint, but the field stops being the two bytes it was designed as, and wire widths meaning
  something is the premise of this tool. **Enums are not exempt** — an enum keeps its named members
  across the export, which makes it look like a real protobuf type, but on the wire protobuf sends it as
  a varint in the `int32` domain, so a 2-byte enum is widened exactly as a `u16` is. Exempting it let the
  obvious case straight through. `bool` and the floats *are* exempt: a float is 32 or 64 bits in both
  worlds, and protobuf's `bool` has no width to choose, so refusing one would mean an export could never
  carry a boolean. The fix for a refused field is to set its wire size to
  4 or 8 bytes; otherwise the message stays on the C target.
- **Nearly the whole `Corpus` is withheld from protobuf as a result**, because it is built from `u8`/`u16`
  to exercise wire layouts. Only `raw-floats` exports. Do not widen the corpus to change that — it is
  shared with the C golden and cross-check suites, which are a different axis. Rich protobuf coverage
  lives on `ProtovalidateFixture` (`Golden/constraints.proto`), which is all-32-bit by design.
- **A refused corpus entry still has a checked-in expected result**, as `Golden/<name>.refused.txt`
  holding the exact diagnostics. Both outcomes are output worth pinning: if only the survivors were
  tested, a rule that started refusing everything — or stopped refusing anything — would leave the suite
  green and merely quieter. `The_protobuf_result_matches_the_golden_file` runs over every entry and
  compares one or the other.
- **An offset-only transform is protobuf-exportable; a scale is not.** `wire = (value - Offset) / Scale`.
  The offset only narrows the width and protobuf sends the value itself, so the range survives as a
  constraint. A scale is lossy quantization, and a protobuf peer would carry full precision while a C
  peer would not — the two would disagree about the number. Getting this backwards either blocks good
  messages or ships wrong ones.
- **protoc resolves imports by path, not package.** `validate.proto` must sit at
  `<dir>/buf/validate/validate.proto` for `import "buf/validate/validate.proto";` to work, however the
  toolchain is laid out on disk. `ProtocToolchain.Compile` stages a copy per run for that reason.
- **Only emit a protovalidate range when it narrows the *protobuf* scalar, not the host.** A `u16`
  becomes `uint32`, so its 0..65535 span is real information the type no longer carries. A `u32`'s full
  span is exactly `uint32`'s, and restating it reads as a designed limit when it is the absence of one.
- **Byte order and bit order need two protobuf warnings, not one.** PD0072 fires on big-endian, PD0075 on
  LSB-first, and they cannot be merged because they do not cover the same fields: byte order is only
  observable above 8 bits, whereas bit order reverses the bits of any field of two or more. An 8-bit
  LSB-first field has no byte order to lose and very much has a bit order, so folding the two behind the
  `BitWidth <= 8` guard would let an LSB-first bus export in silence. Both are `Warning` — protobuf fixes
  both by specification and two protobuf peers still agree with each other, so nothing is lost in the data,
  only in the design's intent.
- **A generated `.proto` must never carry `OnWireLength` or wire-size macros.** Those are our byte
  counts, and protobuf's are different; printing them side by side is the confusion the gate exists to
  prevent.
- **Emitting an import that nothing uses is a protoc warning.** The generator therefore builds the body
  first and decides on `import "buf/validate/validate.proto";` by inspecting it (`Compose`).
- **`CoversEveryMessage` is a default interface member**, so it is only reachable through an
  `IProtocolGenerator`-typed reference — casting a concrete generator is required in tests.
- **Every rule on a `repeated` field belongs under `repeated.items.`** — `repeated.items.enum.defined_only`,
  not `enum.defined_only`. This was a live bug: the numeric branch of `ProtoGenerator.Constraints` had the
  `scope` prefix and the enum branch did not. **protoc accepts the wrong form happily**, because the option
  is well-formed and the extension resolves; protovalidate then refuses to compile the *entire message*
  (`expected rule "buf.validate.FieldRules.repeated", got "buf.validate.FieldRules.enum"`), so every
  constraint on it silently stops working at the consumer. Nothing short of a runtime notices this — it is
  the reason `ProtovalidateConformanceTests` exists.
- **A violation on an array element carries no field descriptor.** `Violation.FieldDescriptor` is nil
  there; the location is in `Violation.Proto.GetField()`, rendered by `protovalidate.FieldPathString` as
  `samples[2]`. Reading only the descriptor reports an empty field name for exactly the cases where
  knowing which element failed matters most.
- **Only a *declared* range becomes a constraint.** A `u8` with no `NumericRange` emits nothing, even
  though `uint32` cannot express 0..255. That is deliberate — inventing a limit the user never declared
  would contradict rule 6 — but it means two `u8` fields can emit different options depending on whether
  the type declares its range. `samples/protobuf-demo.pdproj` declares 0..255 on its `u8`; the
  `ProtovalidateFixture` does not. Neither is a bug.
- **An array's lower bound is `ArrayLength.MinimumCount`, and it defaults to 0.** Every variant except
  `Fixed` takes an optional `MinCount`; `Fixed` returns its `Count` for both ends. Without a declared
  minimum an empty array is legal and the schema emits `repeated.max_items` alone — `min_items: 0` would
  be noise. With one, both ends are emitted together. `IsExactCount` is the "min equals max" test; do not
  reach for `is Fixed`, since a length-prefixed array pinned at 4..4 is exact too.
- **An integer never derives a factor below 1.** `BitMath.MinimumScale` answers "the finest step these
  bits allow across this range", which for a `u16` of 1000..1015 is 15/31 at 5 bits and 15/65535 at 16 —
  correct arithmetic, wrong question. There is nothing between 1000 and 1001 to resolve, and dividing by
  0.4838 makes a stored 1001 come back as 1000.96. Go through `WireSizePolicy.FittedScale`, which clamps
  at 1 for an integer host and leaves a float alone (quantizing a continuous quantity is the point
  there). A factor *above* 1 is kept for both: that is the genuinely lossy case the width asked for.
  Getting this wrong is not only arithmetic — a spurious 0.48 makes the protobuf gate refuse the message,
  since `ProtoScaledFieldRule` blocks any `Scale != 1`.
- **A minimum is declarative intent, not a wire mechanism.** Nothing about the encoding changes because a
  caller promised at least one element, and **the generated C does not enforce it** — it enforces no
  bound today. What a minimum does is raise `MessageLayout.MinBits` (so a frame budget is measured
  against the real floor rather than an empty array) and emit `repeated.min_items` for protobuf. If you
  ever want C-side enforcement, that is a new decision, not a bug.
- **`minCount` is written only when non-zero**, so every file predating it round-trips byte-identically
  and no schema bump was needed. `ArrayLengthFromJson` reads a missing key as 0, which is what those
  files always meant.
- **`biased-signed` in the corpus is quantized, despite the name.** Its transform is
  `BitMath.MinimumScale(-100..100, 8)`, which is `200/255`, not `1` — so the gate refuses it along with
  `packed-bits` and `quantized`, and it has no `.proto` golden. An offset *alone* would be exportable;
  reaching for `MinimumScale` is what makes it a scale.
