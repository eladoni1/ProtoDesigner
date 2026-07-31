# C++ conformance harness

Proves that the C++ the generator emits is not merely *plausible* but *byte-identical* to the
reference implementation.

`main.cpp` compiles against the checked-in golden headers in
`../ProtoDesigner.CodeGen.Tests/Golden/` and asserts:

1. **Round-trip.** `encode` then `decode` returns the original values, for every corpus protocol.
2. **Byte equality with the reference codec.** The exact bytes the generated C++ produces are
   compared against the bytes `ProtoDesigner.CodeGen.Runtime.ReferenceCodec` produced for the same
   inputs. The expected byte patterns are hard-coded in the harness precisely so that a change on
   *either* side of the language boundary trips the test.
3. **The region model holds.** For a dynamic array, the field *after* the variable region must decode
   correctly at every array length — that is the whole reason regions exist, and it is the assertion
   most likely to catch an offset regression.

## Running

```powershell
pwsh tests/cpp-conformance/run.ps1
```

Requires MSVC with the C++ workload; the script locates it via `vswhere`. Compilation uses
`/W4 /std:c++14` and the generated headers are expected to be warning-clean at that level.

Build output lands in `tests/cpp-conformance/build/` and is disposable.

## When this fails

- **Compilation errors** mean the generator emitted invalid C++ — a real bug, fix the generator.
- **Byte mismatches** mean the C++ and C# implementations of the wire format have diverged. The
  runtime header (`CppRuntimeHeader.cs`) and `BitBuffer.cs` are deliberate mirrors of each other;
  if one changes, the other must change with it.
- **Round-trip failures** usually point at transform or sign-extension handling.

Regenerate the goldens first (`PROTODESIGNER_UPDATE_GOLDEN=1 dotnet test`) if the mismatch is an
intended generator change — then re-run this harness to confirm the new output is still correct.
