# ProtoDesigner — Project Guide & Roadmap

> Drop this in the repo root as `CLAUDE.md` and Claude Code loads it every session.
> The `.cs` files are the source of truth for the API; this document is the map, the
> rules, and the task list. When code and this doc disagree, trust the code and fix the doc.

ProtoDesigner is a desktop tool for designing **binary communication protocols** over
Ethernet/UART — think "database schema designer, but for compressed message layouts on the
wire." A user defines reusable types, composes them into messages on a bus, controls exactly
how each field is serialized (down to the bit), and later generates C++/C#/Rust/… encode and
decode code from that definition.

**Stack:** C# / .NET 8 / WPF. Windows-first. xUnit for tests.

**Entity hierarchy:** `Project → Bus (connects modules, Ethernet or UART) → Message → FieldBinding`.
Types live in a project-wide `TypeLibrary` and are referenced by messages, so they're reusable
across every message. Message direction is not modeled (not needed yet).

---

## Table of contents

1. [The rules that must not be broken](#1-the-rules-that-must-not-be-broken)
2. [The core mental model](#2-the-core-mental-model)
3. [Current state — Phase 0 (done)](#3-current-state--phase-0-done)
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
| 6 | Shared storage & collaboration | Not started — see `docs/shared-storage-design.md` |

**1589 automated tests, all passing.** `CCrossCheck` / `CCompositeCrossCheck` compile the generated code
with MSVC — once as C, once as C++ — and assert byte-identical output against the C# reference codec.
They are part of `dotnet test` and **fail** rather than skip when no toolchain is present; set
`PROTODESIGNER_SKIP_CPP_CROSSCHECK=1` to accept generation-only coverage.

```
src/
  ProtoDesigner.Core/            model, layout, validation, IR — no UI, no language bias
    Model/  Layout/  Validation/  Ir/
  ProtoDesigner.Application/     IProjectRepository, IEditCommand + CommandJournal,
                                 GenerationScopes, CodeGenerationService
  ProtoDesigner.Persistence.Json canonical ID-keyed JSON + migration chain
  ProtoDesigner.CodeGen/         IProtocolGenerator, GeneratorCatalog, BitBuffer + ReferenceCodec,
                                 C/ (full codec)     CSharp/ (declarations only)
  ProtoDesigner.Cli/             validate / generate / targets
  ProtoDesigner.Wpf/             the editor
tests/                           one suite per src project
samples/telemetry.pdproj         a worked example exercising most features
docs/shared-storage-design.md    Phase 6 design note — read before building any of it
```

**Model additions since the original Phase 0 sketch** (§4 below predates them):

- `ParameterType.WireBits` / `EnumType.WireBits` — a type's default wire size, propagated onto new
  bindings by `WireEncodingPropagator`. It does **not** violate rule 1: a `FieldBinding`'s encoding
  still wins, this is only the default a new binding starts from.
- `Module` (`ModuleId`, name) on a bus, and `MessageRoute(From, To)` on a message. Direction is
  modelled now — it is what `GenerationScopes.ForModule*` selects on.
- `IrMember` alongside `IrField`: **flat for the wire, nested for the host**. Fields stay flattened
  because offsets are defined over leaves in wire order; members describe the struct the user actually
  declared, so a `Header` used by ten messages is emitted once and referenced by name.

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

**The generated code is C, not C++, and must stay freestanding** — no heap, no exceptions, no std
containers. It targets microcontrollers where an allocation is a fault, not a slowdown, and where C is
often the only option. `CFreestandingTests` enforces the freestanding part; the cross-checks enforce
that one header compiles as C *and* as C++, which is why there is no separate C++ target to maintain.
C has no namespaces, so `GeneratorOptions.Namespace` becomes a symbol prefix, and no overloading, so
every emitted function name must be unique.

**Known gaps, honestly:**
- `FillRemaining` and `Terminated` arrays *decode* by asking the caller for the count rather than
  scanning for the sentinel or consuming the remainder. Encode is correct for both.
- The C# target emits declarations and the wire layout as comments; no encode/decode yet.
- The WPF layer has no automated tests. Everything below it does.

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

**Acceptance.** Extend the round-trip corpus to C++↔C# (encode in one, decode in the other).
Golden files for the C# target.

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
