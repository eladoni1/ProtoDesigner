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
| 5 | C# generator + advanced protocol features | **Done** |
| 5b | Protobuf schema target + protovalidate | **Done** — gated per message, protoc-verified |
| 5c | Wire-compatibility diffing | **Done** — `compare`, PD0080..PD0088 |
| 6 | Shared storage & collaboration | **Started** — merging save, SQL backend, optimistic concurrency. Operations and a merge view not begun. See `docs/shared-storage-design.md` |

**2061 automated tests, all passing**, plus a Windows-only view-model suite. Three conformance checks run inside `dotnet test` and **fail**
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

**Running the tests.** `dotnet test` on the solution needs Windows, for the view-model suite and the C
cross-check. **Only those two are Windows-bound** — protoc and Go both run on Linux, so setting all three
skip variables there wastes 20 real conformance cases. Stage the toolchain instead and skip one:

```bash
# Linux/macOS: stage protoc + validate.proto once (protobuf/ is gitignored)
curl -sSL -o /tmp/protoc.zip \
  https://github.com/protocolbuffers/protobuf/releases/download/v35.1/protoc-35.1-linux-x86_64.zip
mkdir -p protobuf && unzip -oq /tmp/protoc.zip -d protobuf
curl -sSL -o protobuf/validate.proto \
  https://raw.githubusercontent.com/bufbuild/protovalidate/v1.2.0/proto/protovalidate/buf/validate/validate.proto

export PROTODESIGNER_SKIP_CPP_CROSSCHECK=1      # the only one Linux genuinely needs
dotnet build ProtoDesigner.sln -p:EnableWindowsTargeting=true
for p in Core Application CodeGen Cli Persistence.Json Persistence.Sql; do
  dotnet test tests/ProtoDesigner.$p.Tests/ProtoDesigner.$p.Tests.csproj
done
```

That is 2051 of the 2059 cases genuinely run, plus the 9 Windows-only view-model cases left to CI.
Versions match the CI workflow's `PROTOC_VERSION` / `PROTOVALIDATE_VERSION`; Go comes from the harness's
own `go.mod`. Verified on Linux with Go 1.24.7 against a `go.mod` asking for 1.26.5 — the toolchain
directive upgrades in place, so it works.

**CI runs on `main` and on `claude/**` branches.** That second pattern was added 2026-09-21 after
noticing CI had run exactly *once*, on the commit before a long run of work: the trigger was
`branches: [main]` with no PR open, so roughly fifteen commits' worth of Windows-only coverage — the
view-model suite and the C cross-check — had never executed. A branch CI never sees is a branch whose
Windows coverage is theoretical. If you add a branch prefix, add it here too.

```
src/
  ProtoDesigner.Core/            model, layout, validation, IR — no UI, no language bias
    Model/  Layout/  Validation/  Ir/  Compatibility/  Merge/
  ProtoDesigner.Application/     IProjectRepository, IEditCommand + CommandJournal,
                                 GenerationScopes, CodeGenerationService
  ProtoDesigner.Persistence.Json canonical ID-keyed JSON + migration chain
  ProtoDesigner.Persistence.Sql  the same port over SQL rows — SQLite today, Postgres by dialect
  ProtoDesigner.Application/     …also ProtobufCompatibility (the export gate) and WireSizePolicy
  ProtoDesigner.CodeGen/         IProtocolGenerator, GeneratorCatalog, BitBuffer + ReferenceCodec,
                                 C/ (full codec)   CSharp/ (full codec)   Proto/ (schema)
  ProtoDesigner.Cli/             validate / generate / compare / merge / targets
  ProtoDesigner.Wpf/             the editor
tests/                           one suite per src project
  …Persistence.Contract/         the IProjectRepository contract both backends derive from
  …Wpf.Tests/                    view-model edits reach the journal — Windows only, see above
  …CodeGen.Tests/Golden/         checked-in expected output: C headers *and* .proto schemas
  …CodeGen.Tests/Protovalidate/  harness.go + go.mod/go.sum — the Go runtime check, all in git
samples/telemetry.pdproj         a worked example exercising most features
samples/protobuf-demo.pdproj     aimed at the protobuf target: two exportable messages covering every
                                 constraint shape, plus one bit-packed and one quantized message that
                                 the gate must refuse by name
docs/pdproj-format.md            the on-disk format, for anything writing a .pdproj without the editor
docs/shared-storage-design.md    Phase 6 design note — read before building any of it; §3.3 records
                                 what the merge already does and what it deliberately refuses
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
- ~~The C# target emits declarations only~~ — **it emits a codec now**, and the advanced-features half of
  Phase 5 is done too: `--assign-ids`, `--frame-budget-is-an-error`, and an alignment policy settable at
  every level of the chain.
- The WPF layer is thinly tested. `ProtoDesigner.Wpf.Tests` covers one thing — that every view-model edit
  reaches the command journal — because that is where its only shipped defects were. The views, the
  dialogs and the converters are still uncovered. Dialog logic that is really *policy* should be extracted
  so it can be tested from `Application` — `WireSizePolicy` was pulled out of the primitive editor for
  exactly this reason, after a defect that was untestable where it lived, and the type-edit commands
  followed it out for the same reason.
  **That suite only runs on Windows**: it targets `net8.0-windows` to match the assembly under test, and
  `Microsoft.WindowsDesktop.App` has no Linux runtime. It compiles anywhere with
  `-p:EnableWindowsTargeting=true`; on Linux, run the other five suites by project and leave this one to
  CI. The view models touch no WPF type, which is what makes them testable at all — keep it that way, and
  anything that needs a `Window` belongs in the view.
- Nothing consumes a generated `.proto` end to end (`protoc --csharp_out`, populate, serialize,
  deserialize). The schema is proven valid and its constraints proven to fire; it is not proven *usable*
  by a generated stub. See the note under "Open work" before spending time on it.
- ~~`FillRemaining` and `Terminated` decode by asking the caller for the count~~ — **fixed.** Both work the
  count out for themselves now; see the note below.

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

### Is every rule actually shipped? Sweep it, don't assume

A rule can be correct and still not be in the product. `Validator.DefaultRules()` is the shipped
catalogue, and a test that builds its rule directly (`new Validator(new[] { rule })`) proves the rule
works while saying nothing about whether anyone runs it. **Comment a rule out of the catalogue and
something must go red:**

```bash
# one rule at a time, from the repo root
V=src/ProtoDesigner.Core/Validation/Validator.cs
sed -i "s|        new <RuleName>(),|        // new <RuleName>(),|" $V
dotnet test tests/ProtoDesigner.Core.Tests/ProtoDesigner.Core.Tests.csproj   # must fail
git checkout -- $V
```

Swept over all 21 rules on 2026-09-19. Seventeen were caught by `Core`; `TransportBudgetRule` and
`UnreferencedTypeRule` are caught by `Application`/`Cli` instead, which is fine — they are pinned, just
not where you would look first. **Two were caught by nothing in the repo**: `BusHasNoMessagesRule` and
`MessageHasNoFieldsRule` could be deleted outright and every suite stayed green. `EmptyContainerTests`
closes that, and re-running the sweep on those two now turns it red. Run the sweep after adding a rule;
it is the only thing that distinguishes "written" from "shipped".

**The golden files are load-bearing, and that was checked the same way.** Deleting a golden does not
pass by regenerating — `AssertMatchGoldens` writes the missing file and then `Assert.Fail`s, so the
update is never mistaken for agreement. Renaming an emitted macro in `CGenerator` (`ON_WIRE_BYTES` →
`ON_WIRE_OCTETS`) turns 10 of the 33 golden cases red. Both verified 2026-09-19.

One caution learned from doing it: **check that the mutation landed before believing a green result.**
The first attempt here edited a token that does not appear in `CGenerator` at all, so the suite stayed
green and briefly looked like a hole. A mutation that changes nothing proves nothing.

### Open work, in the order I would take it

**Phases 0 through 5 are complete.** The list below is kept as a record of what was decided and why; the
only unstarted work is Phase 6, and the note under it about doing the schema-v3 flattening first.


1. ~~**C# encode/decode.**~~ — **done.** `CSharpWireCrossCheck` compiles the emitted source with Roslyn
   and checks its bytes against the reference codec.
2. ~~**`FillRemaining` / `Terminated` decode**~~ — **done.** `CTerminatedFillCrossCheck` compiles and runs
   both, decoding into a struct that is never told a count.
3. ~~**Test the built-in ID enums.**~~ — **done.** `SyntheticEnumChangeTests` covers a renamed bus, a
   renamed module, a changed and a cleared `WireId`, a removed module renumbering the rest, and both
   always-on enums; the persistence half is in `RoundTripTests`. Writing it found that the serializer
   *wrote* a synthetic enum's derived members and the reader loaded them back — empty in practice, but
   the one way the type could have gone stale. Both sides now refuse them.
4. ~~**Match the ID enums to the requested spelling**~~ — **decided: keep what is emitted.** See
   "Settled" below.
5. ~~**Wire-compatibility diffing**~~ — **done.** `Core/Compatibility/WireCompatibility.Compare(baseline,
   current)` and `protodesigner compare <a> <b>`. See the section below.

**The advanced protocol features are three small things, and two of them are refusals a build opts into.**

- **`AssignWireIdsCommand`** gives every message without an id the lowest free one *on its bus* — the
  scope a receiver matches in and the scope `PD0004` checks. **An id that already exists is never moved.**
  Renumbering would break every deployed peer silently, because the frame still arrives and is simply not
  recognised; filling gaps is a convenience, rewriting the space is a wire change and stays the user's.
  Zero counts as a gap, since that is what `_NotAssigned` means. `protodesigner generate --assign-ids`,
  and it names only what it actually changed.
- **`FrameBudgetPolicy.Block`** turns `PD0050` into a refusal. The rule stays a `Warning` in the validator
  and that is the point: a budget is a property of the *link*, not of the protocol — a message too big for
  one UART frame is fine on a bus that fragments, and the validator cannot know which it is looking at. A
  build is run for a particular link and its operator can say so, which is why the switch lives on the
  generation call. `--frame-budget-is-an-error`. The promoted diagnostic keeps `PD0050`, so the report and
  the refusal cannot say different things.
- **Alignment was already configurable at every level** — field, message, bus, project, built-in default —
  modelled, resolved by `EffectiveLayoutOptions.Resolve`, persisted on both sides and honoured by the
  engine. What it lacked was coverage: only the field and message levels were tested, so the *precedence*
  was not. `AlignmentPolicyTests` pins the whole chain, and reversing the resolution order turns three of
  its cases red. There is deliberately no editor surface, on the same reasoning as byte and bit order.

**The C# target emits a codec, and it is the best-checked one in the repo.** Same wire format as C — a
frame written by one is read by the other — so `CSharpCodec` deliberately mirrors `CGenerator`'s algorithm
rather than inventing a second one. That is not the mistake the reference codec must not make: the
independence that matters is *reference codec vs generators*, and `CSharpWireCrossCheck` measures against
exactly that.

It is better checked than the C target for one reason that has nothing to do with the code: **Roslyn
compiles the emitted source in memory and reflection runs it**, so the whole thing is verified on any
machine that can run the suite. The C equivalent needs MSVC and only runs on Windows. Arrays of structs go
further still and are compared against the bytes `CStructArrayCrossCheck` derives *by hand* — two
generators, two languages, agreeing with a third answer neither produced.

Two things worth knowing before changing it:

- **A field path is flat; the host shape is nested.** `header.messageId` is declared as `Header.MessageId`,
  a member of a member, so `AccessPath` walks the member tree instead of flattening the path to one
  identifier. Flattening produced code naming something no class had, which is how this was found.
- **An array of structs must arrive constructed.** `new Block[4]` is four nulls, so the declaration uses
  `PdInit.Array<Block>(4)`; a decoder writing into `blocks[i].Field` would otherwise throw before reading
  a byte. That was a latent defect in the declarations-only output too.

**`Terminated` and `FillRemaining` work their own count out, and the count is the decoder's job.**
Both used to read `msg->x_count` and trust it, which made them unusable for the one case they exist for:
a frame whose length you do not know in advance. Encode was always right, so a golden file could not see
the difference — the emitted decode was well-formed C that simply read whatever was already in the struct.

The fix is small because it reuses the element loops rather than replacing them: work out the count first,
then let the ordinary loop read that many. `Terminated` scans ahead for the sentinel with
`pd_br_match_bytes_at`, which never moves the cursor; `FillRemaining` measures `pd_br_bits_remaining`
**less the floor of the regions that follow**, so a trailing fixed field is left intact rather than
swallowed. Both clamp to the declared maximum.

Two consequences worth knowing. A sentinel-terminated array cannot carry its own sentinel value as data —
inherent to the format, not to this decoder. And a `FillRemaining` array followed by a *second* dynamic
array has no decodable answer at all; reserving that array's minimum is the conservative reading, since
under-consuming is recoverable and over-consuming is not.

`CTerminatedFillCrossCheck` is the proof, and the assertion that carries it is the `memset` before decode:
the destination struct is zeroed and never told a count, so a decoder that reads the caller's count reports
zero elements and the test fails. Its expected bytes are hand-derived, and `TerminatedFillLayoutTests`
checks them against the reference codec so a wrong expectation cannot hide on a machine with no MSVC.

**Wire compatibility is the one question git cannot answer, and it is now answerable.**
`WireCompatibility.Compare(baseline, current)` runs the layout engine over both and reports what a peer
built from the baseline would make of the result. It needs no database: the baseline is any other
`Project`, as easily the last git commit as the last published version. `protodesigner compare <a> <b>`
exposes it, and `--breaking-is-an-error` turns a report into a CI gate.

It works because of two rules already in place, and it is worth stating which:

- **Identity is an id**, so fields are matched on a *chain* of `FieldId`s rather than on `Path`. Keying on
  the path would be simpler and wrong — the path carries names, so every rename would report as a removal
  plus an addition, and the one change this tool can promise is free would be the loudest thing in the
  report. The chain rather than a single id, because one struct used twice contributes its members twice
  with the same inner binding id: `from.version` and `to.version` are one binding reached by two paths.
- **Layout is computed**, so the comparison is between two runs of `LayoutEngine` rather than between two
  sets of stored offsets that could disagree with the model that produced them.

**Breaking changes are `Warning`, never `Error`, and this is deliberate.** `Error` blocks code generation,
and breaking the wire on purpose is exactly what a protocol version bump is. The point is that nobody
breaks a fleet without being told, not that it cannot be done. `compare` exits 0 by default for the same
reason: a check that fails the build on every intentional bump is one people route around.

The cascade is the part worth having. Narrowing one field by two bits moves every field after it, and
those are reported by name — `temperature` and `battery` moved because `mode` changed, which is precisely
what a reader of a JSON diff does not see. `PD0080`..`PD0088`, none in `Validator.DefaultRules` (there is
no single model for a rule to run against — it takes two projects).

**Two people's edits merge by identity, and git cannot do this for us.** `ProjectMerge.Merge(baseline,
local, remote)` walks types, buses, modules, messages and fields, comparing each entity's own state
against the common ancestor. `PD0090`..`PD0095`. It was built first of the Phase 6 work because it needs
no database — the baseline is any other `Project` — so it is useful before the storage exists and
unchanged after.

The motivating case is measured, not assumed: two branches each adding a different message to one bus
**conflict under a real `git merge`**, because both insertions land at the same textual anchor. Matching
on ids makes that the trivial "take both" it always was.

Four decisions in it are load-bearing:

- **A field list touched on both sides is a conflict even when the two edits are disjoint** (`PD0095`).
  Field order is wire order, so appending one field on each side yields a layout neither person designed
  and nothing downstream would report it. This is the only conflict the merge could resolve and must not.
- **Nothing is applied unless everything can be.** The merge is planned in full and written into `local`
  only if it is clean; a half-applied merge leaves a mixture neither person wrote, that no undo describes.
- **A clean merge is not a valid one.** Two people adding a message each with the same wire id conflict
  nowhere, because they touched different entities. The result is validated and its `Error` diagnostics
  come back in `MergeResult.Validation`; `Succeeded` means clean *and* valid.
- **The same change made independently on both sides is not a conflict.** People reach the same edit more
  often than is comfortable, and stopping them would be noise.

`EntityState` renders the state string the whole thing turns on, and **a property missing from it is
silent data loss** — an edit the merge cannot see is one it discards as "unchanged", leaving a valid
project that is nobody's. `EntityStateCoverageTests` reflects over the model rather than trusting a list,
so a new property on `FieldBinding` fails the suite until it is either covered or written down as
carrying no state. It asserts both halves separately, because they fail separately: that the merge *saw*
the edit, and that the copy routine *carried* it.

**The save button merges, and that is the whole of what "multi-person" means today.** `WorkingCopy`
(Application) holds the open project *and* the baseline it was read from; `Save()` re-reads storage,
merges by entity and writes. It runs over `IProjectRepository`, so it is a file today and a database
later with no change here. `protodesigner merge <baseline> <ours> <theirs> [--out]` is the headless
half, for a git merge driver or a CI job.

Three things about it were decided rather than fallen into:

- **Every save merges, including when nothing came in.** No "has the file changed?" shortcut: a
  timestamp or hash read before the load is stale by the time the load happens, and a merge against an
  identical copy applies nothing anyway. One path that is always right beats two that are usually right.
- **A conflicted save writes nothing and changes nothing.** The working copy is left exactly as the user
  had it, so the conflict list is something to act on rather than a state to climb out of.
- **A merged save resets the undo history**, and says so. The journal describes operations against a
  project that changed underneath them, so replaying one backwards is undefined. The editor rebuilds the
  view models for the same reason — the merge edits the model in place and the old ones wrapped what it
  used to be.

**One fact, one field, in both halves of this.** `WorkingCopy` holds the path and the baseline as a single
nullable pair, because they *are* one fact: a project with nowhere to save has no ancestor to merge
against, and a stored one always has both. Held apart they could disagree, and the merge would run against
the wrong ancestor with nothing to show for it. `Path is null` is therefore the whole of "never stored",
and `Save()` refuses rather than guessing a destination — picking one is the caller's job.

`MainViewModel.Show(copy)` is the same move one layer up: **the only place `_workingCopy` and `Project` are
assigned.** They are two views of one fact, and the failure mode if they drift is the worst kind — the app
saves the wrong project and reports success. Setting them together in one three-line method makes that
impossible instead of merely unlikely, which matters more here than a test would, because this suite only
runs on Windows. Do not assign either one anywhere else; `Show` also covers the post-merge rebuild.

`MergingSaveTests` lives in the *persistence* suite on purpose. `WorkingCopy` is only correct if a
project written to disk and read back is the same project as far as the merge can tell, and a fake
repository handing back objects would prove the save logic while assuming the half most likely to be
wrong. `Opening_a_file_and_saving_it_straight_back_merges_nothing` is that assumption as a test: if the
serializer ever drops something `EntityState` renders, the field symptom is a save reporting phantom
incoming changes from a file nobody edited.

**The repository port has a written contract now, and one clause of it is not guessable.**
`ProjectRepositoryContract` (Persistence.Json tests) is an abstract suite a new backend derives from —
the Phase 6 acceptance criterion, written while there was still one implementation to check it against.
Four clauses: a round trip preserves the project, a save replaces what was there, loading something
never stored **throws rather than inventing an empty project**, and:

> **Every `Load` must return a fresh object graph.**

That one is the reason the contract exists. `WorkingCopy` loads a project *twice* on purpose — once as
the copy being edited, once as the untouched baseline its next save merges against. An implementation
that cached and returned the same instance would share those, so the baseline would mutate along with
the edits, every merge would compare a project against itself, find nothing, take nothing and report
success. Nothing downstream would notice, and the JSON implementation satisfies it by accident rather
than by design — it reparses every call. A SQL or in-memory backend is exactly where it would break.

Verified by writing a deliberately bad repository (cached instance, empty project for a missing key) and
watching the contract turn those two clauses red while the other two correctly stayed green.

**The SQL backend is the second implementation, and the contract is what made it safe to write.**
`SqlProjectRepository` (Persistence.Sql) stores a project as rows over SQLite. The database is named once
in the constructor and the port's `path` is **the key of one project inside it** — that is the whole
difference a shared backend makes: one store holds everyone's projects where one file holds one.

**Rows for identity, columns for values.** Everything the model gives an id becomes a row, because that
is what makes an entity separately addressable — two people editing different messages touch different
rows, which is the reason for a database rather than a document. Value objects with no identity that are
never referenced (`FieldEncoding`, `LayoutOptions`, `NumericRange`) are columns on their owner.
`ArrayLength` is the single exception and stored as compact JSON: it is a five-variant union, so
relationally it is five nullable column groups or five sub-tables, for a query nothing here makes.

**Decimals are TEXT, not REAL.** SQLite's REAL is a double, which loses the top of the 64-bit integer
domain — the exact reason `ScalarTransform` uses `decimal`. This is checked, not assumed: routing `Text()`
through a double turns the cross-check red.

**A save replaces that project's rows in one transaction.** Writing only what changed needs a per-entity
revision to compare against, which is the next piece and not this one; until then, replacing wholesale is
the version that cannot leave a half-written project behind.

It shares no code with the JSON serializer, deliberately. Two implementations of one port are only worth
having if they are independent — a shared mapping would mean a bug in it passed both their tests.

**`SqlMatchesJsonTests` asks the merge whether the two agree, rather than asserting field by field.** A
new mapping fails by *dropping* things, and a hand-written assertion only ever checks what its author
remembered. So one rich project goes through both backends and `ProjectMerge` is asked whether they are
the same project — the sharpest question available, because `EntityStateCoverageTests` already pins the
merge against every property of the model by reflection. Four mutations were watched to go red (a dropped
wire offset, dropped routes, a dropped protobuf number, decimals through a double), and each names the
entity it lost. A second test compares SQL against the original rather than against JSON, so a blind spot
the two share is still caught.

One caution, learned the hard way here: the fixture mints fresh ids on every call, so using it as both
baseline and round-trip input reports **every** entity as added. That was this suite's first red run, and
the test was what was wrong, not the mapping.

**The merging save had a lost-update window, and it was demonstrated before it was fixed.**
Every `Save()` re-reads storage first, which closes the ordinary case — two people editing different
messages both survive whichever order they save in. What it could not close is the window *inside* one
save, between the read it merges against and the write that follows. `LostUpdateTests` reproduced it
deterministically, with a decorating repository writing at the instant the window opens: the
interloper's whole message vanished and both saves reported success.

`IStampedProjectRepository` closes it. A stamped `Load` reports a token for the version it read, and
`SaveIfUnchanged` compares and writes **as one transaction** — checking the stamp separately just before
writing would re-create the same window one level down. SQLite carries it as a `version` column bumped on
every write.

Three decisions in it:

- **It is a separate interface, not a widening of `IProjectRepository`.** A backend that cannot do it
  atomically should not implement it: `WorkingCopy` reads its absence as "this store has one writer" and
  carries on, which is honest, where a stamp that only *narrowed* the window would look like a guarantee.
  The file backend deliberately does not implement it.
- **Being overtaken is retried, not reported.** It means the merge had stale input, so the only useful
  response is to read again and redo it — and the second attempt starts from whatever overtook it. Four
  attempts, then it throws rather than writing anyway or spinning.
- **The interloper in the tests always edits a *different* entity.** Two people touching one message is
  an ordinary merge conflict, reported long before a stamp matters, so an interloper aimed at our own
  message would test the merge instead of the window.

That test misled its author three times before it was right, and each way is worth knowing: an
interloper firing during `WorkingCopy.Open` lands in the *baseline* (Open reads twice) and is merged in
as ordinary work; a decorator implementing only `IProjectRepository` silently downgrades the store to the
unguarded path; and a fixture generating an id that collides with an existing one is refused by the
schema with a UNIQUE violation — correctly, and a duplicate the file backend would have written without
complaint.

**Settled, so that they are not reopened as questions:**

- **The built-in id enums keep the C target's own spelling**, `<ns>_<Bus>MessageId_NotAssigned` and
  `<ns>_<Bus>ModuleId_<Module>`, not the `<BUS_NAME>_MESSAGE_ID_NA` shape originally asked for. Decided
  2026-09-18, and the deciding fact is not taste: that spelling is exactly what `CNaming.EnumMemberName`
  produces for *every* enum, so a user's `Mode` emits `telem_Mode_Idle` beside it. Switching would make
  the two built-ins the only enums in the header written differently — the inconsistency would be visible
  in every generated file, where the current inconsistency with the original request is visible nowhere.
  The bus scope the request was actually after is already there. It would also churn 10 golden files and
  break any deployed switch on these names, but those are the cheap reasons, not the real one.

- **Byte order and bit order are both bus-level, and nowhere else.** One bus is one agreement about wire
  format; two modules on it disagreeing is a broken link, not a configuration. `LayoutOptions` still
  carries a message- and field-level override and `EffectiveLayoutOptions.Resolve` still walks the whole
  chain — the editor simply does not offer them. The resolved pair is stated **once**, as a chip in the
  message header, not per field: every row carried the same answer and only differed in which half applied,
  which the width column already says. A hand-edited file *can* still set an override, so the chip marks
  itself with a red `*` and names the offenders in its tooltip rather than quietly showing the bus's answer
  while the generator uses another. If per-link byte order is ever wanted, the modelling answer is two
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
  Emit header(s) with a struct per message plus `encode`/`decode`
  (and `parse`-from-interface) functions honoring width, endianness, bit order, transform, and
  each array length rule.

**Generators are hand-written `StringBuilder`, not templates.** An earlier draft of this plan specified
Scriban; no generator ever used it and the golden files pin the output of the code that exists. Do not
introduce a template engine to close a gap the goldens already close.

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

**Build.** ~~A C# `IProtocolGenerator` behind the same interface~~ — **done**, encode and decode
included. The IR earned its keep: supporting C# needed no change to it at all, only a second reading
of the same `Fields`/`Members` split the C target already uses.

~~**What remains in this phase is the advanced-features half**~~ — **done as well.**
`AssignWireIdsCommand` fills the gaps in a bus's id space without moving an id anyone already has;
`FrameBudgetPolicy.Block` promotes `PD0050` to a refusal for a build that knows its link;
alignment was already modelled, resolved and persisted at all four levels, so what it needed was the
coverage that proves the precedence — see the notes in §3.

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
  and the C generator so this stops being a guess. Their decode strategy was the last gap and is closed.
- **`MenuItem.Header` ignores a binding's `StringFormat`.** `Header` is typed `object`, and WPF drops
  `StringFormat` silently when the target is not a string — you get the bare value, no error. Use
  `HeaderStringFormat` (and `ContentStringFormat` on a `ContentControl`). This bit the "Edit {0}..." item
  on the field row's context menu, which rendered as just the type name.
- **The count field is chosen in the array editor, and it lives on the *type*.** Picking "Variable" offers
  either a count field earlier in the same message or an inline prefix, and `CountFieldCandidates`
  (Application, tested) decides what is eligible so the list cannot offer something `LayoutEngine` then
  throws on. The wrinkle is that `ArrayLength.CountFromField` holds one `FieldId` while an `ArrayType` is
  project-wide: an array used by two messages can only point into one of them, and the other reports
  PD0030. The dialog warns and names the messages rather than refusing — same line the editor takes on a
  duplicate message id. The clean fix, if it ever matters, is to move the choice onto `FieldBinding`, which
  is where a per-occurrence decision belongs.
- **An array element may be a struct, and PD0036 now refuses a much narrower set than it used to.** The
  element is described by `IrArrayInfo.ElementFields` — one entry per value inside one element, offset from
  that element's own start — because `ElementPrimitive` can name only one scalar kind. Check
  `IrArrayInfo.HasCompositeElement` before reading `ElementPrimitive`, `ElementEnumIndex`, or treating
  `ElementBits` as one value's width: for a struct element those are a placeholder, null, and the whole
  stride. Reading the kind of an element that has none is exactly how an array of structs silently became
  an array of `uint8_t` the first time round.
- **Removing that blanket refusal exposed three shapes for the first time, and all three are PD0036's job,
  not the builder's.** An array of arrays, a *dynamic* array inside an element, and a fixed array *of
  composites* inside an element. The layout engine lays the last one out perfectly well, so it is
  wrong-but-computable and belongs to the validator under rule 5 — left to `IrBuilder` it surfaced as an
  `InvalidOperationException` at generate time instead of a diagnostic naming the member. The rule recurses
  through nested structs, since a nested struct otherwise hides the offender.
- **The reference codec deliberately refuses composite elements rather than implementing them.** Its array
  path describes an element by one `ElementPrimitive`, so it would have to grow the feature before it could
  disagree with the C generator about it — and writing it by mirroring `CGenerator` would destroy the
  independence that makes the cross-check worth anything, the same mistake as a C# reimplementation of
  protovalidate. Ground truth for this shape is `CStructArrayCrossCheck`, whose expected bytes are derived
  from the declared layout **by hand** and cannot be made to pass by changing both implementations the same
  wrong way. It pins three counts — two elements, zero, and full — because a stride added once too often
  still produces the right bytes in the middle and the wrong ones at the ends, and what moves is the
  trailing field nobody touched.
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
