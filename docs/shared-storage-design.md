# Shared storage — design note (Phase 6)

> Status: **design only, nothing built.** This decides the shape before it is expensive to change.
> Written 2026-08-03.

The question: today a project is one `.pdproj` file on one machine. What happens when a team shares it?

---

## 1. Start with what makes this different from a document

The instinct is to reach for how Google Docs or Figma work — every keystroke to the server, everyone
sees everything live. That instinct is wrong here, and the reason is worth stating plainly because it
drives every decision below.

**A protocol definition is a compile-time contract between separately-built binaries.** Five modules
are compiled against it, flashed onto five boards, and expected to talk. That has three consequences a
document does not have:

1. **A half-finished edit is dangerous, not merely untidy.** If someone regenerates C++ while you are
   midway through restructuring a message — three fields moved, the fourth not yet — they get code that
   compiles, runs, and is silently wrong on the wire. A document with a half-written paragraph is just
   a document with a half-written paragraph.
2. **"The current version" has to be a nameable thing.** When a board is flashed, someone must be able
   to say *which* version of the protocol it was built against. A continuously-mutating shared state
   has no version to name.
3. **The blast radius of a mistake is a fleet, not a file.** Narrowing a field from 16 bits to 12 does
   not break the editor. It breaks every deployed decoder, at runtime, in the field.

So: live shared editing is the wrong model. Not because it is hard — because it is wrong for this.

---

## 2. The three options, weighed

The options considered were: write every operation straight to the database; autosave to the database
every few seconds; or an explicit publish, with local work in between.

**Every operation to the database.** Rejected. It makes point 1 above the normal case: every
intermediate state is everyone's state. It also destroys point 2 — there is no version, only a
timeline. And it fails the "safe for someone who might screw up" requirement completely, because there
is nowhere to make a mistake privately. As a bonus problem, it turns a `MoveField` into a network round
trip, so the editor becomes latency-bound for operations that are currently instant.

**Autosave to the database every few seconds.** Rejected for shared state, adopted for local state.
Debouncing does not change *what* is being published, only how often — every problem above survives.
But autosave is exactly right one layer down: the local working file should save itself on an idle
debounce so a crash costs seconds, not an afternoon. That is crash-safety, not collaboration, and
conflating the two is the error.

**Explicit publish, local work in between.** Adopted. It is the git model, and the analogy holds
tightly because the underlying problem is the same one git solves: many people editing a shared
contract, needing private space to work and a deliberate moment to say "this is ready."

---

## 3. The model

```
  local .pdproj  ───edit, autosave (debounced)───▶  local .pdproj
        │
        │  publish  (explicit, reviewed, validated)
        ▼
  ┌─────────────────────────────────────────┐
  │  shared database                        │
  │    • immutable published versions       │
  │    • per-entity revision numbers        │
  │    • who published what, when, and why  │
  └─────────────────────────────────────────┘
        │
        │  pull / update
        ▼
  someone else's local .pdproj
```

Four rules make it work.

### 3.1 The local file is always the working copy

Every edit lands in the local `.pdproj`, autosaved on an idle debounce. Working offline is the normal
mode, not a degraded one. This also means the current product does not change: today's single-file
workflow *is* the working-copy half, already built and tested.

### 3.2 The command journal is already the changeset

Phase 4 routes every mutation through `IEditCommand` / `CommandJournal`. The commands recorded since
the last sync **are** the diff — there is nothing to compute. A changeset is a list of operations, each
naming the entity it touched, which is why CLAUDE.md rejects CRDTs: operations plus a revision check
are sufficient when publishing is a deliberate act rather than a keystroke.

This is the strongest argument for keeping the journal honest. Every mutation must go through it —
"the domain never mutates behind the command bus" is not a tidiness rule, it is the thing that makes
Phase 6 cheap.

### 3.3 Concurrency is per entity, not per file

Every type, message and field carries a `revision`. Publishing sends each changed entity along with
the revision it was based on; the server rejects any whose revision has moved. Two people editing
different messages therefore never conflict, which is the common case on a real bus. Two people editing
the same message conflict on that message alone, and the merge view shows one message, not a file.

The flat, ID-keyed JSON collections from Phase 2 (`"messages": { "<id>": {…} }`) were chosen for
exactly this: each entry becomes a row with a revision column, with no reshaping of the model.

### 3.4 Published versions are immutable

Publishing appends; it never overwrites. "Which protocol was this board built against?" is answerable,
and rolling back is selecting an earlier version rather than reconstructing one.

---

## 4. Protecting the developer who screws up

Explicit publish provides the private workspace. These are the rails on top of it, in the order they
are worth building.

### 4.1 Wire-compatibility checking — the one that actually matters

This is the feature that makes ProtoDesigner worth more than a shared folder, and it is the reason to
be excited about Phase 6 rather than merely resigned to it.

ProtoDesigner can answer a question git structurally cannot: **would this change break a decoder that
is already deployed?** It has the layout engine. Comparing the `MessageLayout` of the published version
against the local one classifies every change:

| Change | Effect on deployed decoders |
|---|---|
| Rename a field, a type, a message | **Safe** — names are display and codegen only; identity is the ID |
| Add a new message | **Safe** — nobody decodes an id they do not know |
| Reorder fields | **Breaking** — every offset after the first move is wrong |
| Narrow a field's width | **Breaking**, and silently: values decode as garbage, not as an error |
| Widen a field's width | **Breaking** — everything after it shifts |
| Add a field to an existing message | **Breaking** for fixed messages; check the frame budget besides |
| Change endianness, bit order, or transform | **Breaking**, and the hardest kind to debug from a capture |
| Change a `WireId` | **Breaking** — the receiver stops recognising the message |

Surfacing that table as a diff at publish time — *"this change breaks 3 messages that Controller and
Logger decode"* — is the whole value proposition. It is a natural extension of the existing validator:
a rule family that takes two projects instead of one, producing the same `Diagnostic` type with the
same stable codes. It needs no new machinery, and it should be built **before** any database exists,
because it is just as useful comparing a local file against the last git commit.

### 4.2 The rails that come with it

- **Nothing with an `Error` diagnostic can be published.** The same rule that already refuses codegen.
- **Review before publish.** Show the entity-level diff and the compatibility verdict, and require a
  message saying why. This is the moment the developer catches their own mistake.
- **Everything is reversible.** Immutable versions mean a bad publish is superseded, never destructive.
- **Optionally, claims.** A soft "Sensor is editing the Telemetry message" advisory. Worth it only if
  conflicts turn out to be frequent in practice — do not build it on speculation.

---

## 5. What this becomes in the schema

The repository port already exists (`IProjectRepository`), so callers do not change. The JSON
implementation stays as the local format; a SQL implementation is added beside it.

```
projects            (id, name, created_at)
project_versions    (id, project_id, version_no, published_by, published_at, message)
entities            (id, project_id, version_id, kind, revision, payload jsonb)
                      kind ∈ { type, bus, message, field }
operations          (id, version_id, sequence, command_kind, entity_id, payload jsonb)
```

`payload` as `jsonb` rather than a column per property is deliberate: the model still evolves, and the
migration chain from Phase 2 already handles schema versioning at the document level. Promote a field
to a real column when something needs to query by it, not before.

Both implementations must pass one shared set of repository contract tests. That is what keeps "swap
JSON for SQL" honest rather than aspirational.

---

## 6. Recommendation on timing

**Build 4.1 (wire-compatibility diagnostics) soon. Defer the database until something forces it.**

For a team of developers who are comfortable with git, a `.pdproj` in a repository is already a
perfectly good shared database: it has history, review, blame, branching and rollback, and the
canonical-form writer from Phase 2 was specifically designed to make it diff and merge cleanly. Adding
a database before that stops working buys a server to maintain and an ergonomic step backwards.

The database earns its place when one of these becomes true, and not before:

- **A non-developer needs to edit.** A systems engineer who owns the protocol but does not use git is
  the single most likely trigger.
- **"What is deployed right now?" needs one authoritative answer**, not a branch name someone
  remembers.
- **An audit trail is required** — who changed this field, when, and why — beyond what commit history
  gives you.

Compatibility checking, by contrast, is valuable on day one and stays valuable either way: against the
last published version, or against the last commit. It is the same rule either way.
