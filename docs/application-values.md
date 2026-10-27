# MessagePack application values

`MsgPack` is an explicit, reflection-free/AOT-safe codec, not a CLR object
serializer. Reference: RNS 1.5.7 commit
`a29bfb51c468945c96a1ee22cae0114e993e2391`, `RNS/vendor/umsgpack.py`,
SHA-256 `0710f48f495193a764048c883b27b51cc7e7f7ae12486d4aad859994271cba50`.
Relevant contracts: integer/string/binary packing (283–356), extension/timestamp
packing (370–415), dispatch (451–535), string/extension/timestamp decoding
(646–747), map decoding (772–805).

## Native domain

`Pack` accepts null, bool, signed/unsigned 8/16/32/64-bit integers, float,
double, string, byte[], ReadOnlyMemory<byte>, object?[] (including jagged
arrays), Dictionary<object, object?>, and ordered
IReadOnlyList<KeyValuePair<object, object?>> maps. No object reflection,
registered classes, dynamic code, arbitrary IEnumerable enumeration, decimal,
BigInteger, enum, char, DateTime or DateTimeOffset conversion is performed.
Unsupported types throw ArgumentException.

Integers use the reference's shortest representation. float and double write
float64; both float32 and float64 decode to double. NaN, infinities and signed
zero are preserved as values. Integers decode to long, or ulong above
long.MaxValue. Strings use strict UTF-8: invalid bytes throw FormatException,
and unpaired CLR UTF-16 surrogates throw ArgumentException rather than being
replaced. `ToInt64` retains its existing finite-double truncation toward zero,
but now diagnoses NaN, infinity and overflow rather than producing a sentinel.
This helper does not affect wire integer decoding.

`MsgPackExtension(sbyte type, ReadOnlySpan<byte> data)` copies and owns its
payload. All extension headers are read and the reference's shortest header
is written. Unknown types remain opaque; no handler runs implicitly. Type -1
requires `MsgPackTimestamp(long seconds, uint nanoseconds)`, which reads/writes
the 32/64/96-bit timestamp formats. Its seconds are UTC Unix seconds and its
nanoseconds must be below 1,000,000,000. Timestamp encoding selects the same
shortest layout as upstream datetime encoding.

Unlike Python datetime, the native timestamp retains nanoseconds (Python
truncates to microseconds) and the full signed 64-bit seconds range. Invalid
nanoseconds are rejected, not normalized as Python timedelta may do.
Explicit timestamp values avoid timezone guessing and CLR calendar limits.
Noncanonical valid encodings are accepted and canonicalized when repacked;
float32 is similarly repacked as float64.

## Map compatibility and intentional restrictions

Existing `Unpack`/`TryUnpack` callers keep their binary-key projection to
**uppercase hexadecimal strings**. Runtime, persistence, Git and LXMF consumers
are not migrated implicitly. A binary key colliding with a text key now fails
explicitly rather than silently losing a field. This legacy view is not
lossless and must not be repacked to preserve binary-key wire bytes.

Link clients opt into the lossless response view with
`Request(..., useApplicationValues: true)`. Destination request handlers select
lossless `RequestContext.Data` using the same option on
`RegisterRequestHandler`/`RegisterAsyncRequestHandler`. These options apply to
both small packets and resource-backed requests/responses. Existing handlers and
clients retain the legacy view by default; an unrepresentable legacy map is
diagnosed, not silently projected over a colliding text key. Small-response size
limits use the lossless wire value before legacy projection.

Resource metadata and existing runtime/tool protocols retain their legacy map
view; the request option changes request data/response values, not file-response
metadata. Use text keys for portable metadata. A binary/text projection collision
there is an explicit decoding failure, not a lossless metadata capability.

New application consumers should use `UnpackApplicationValue` or
`TryUnpackApplicationValue`. These return Dictionary<object, object?> maps
whose binary keys stay byte[]; their comparer uses content equality for
byte[]/ReadOnlyMemory<byte> and exact Python-style numeric equality
(true == 1 == 1.0), without rounding large integers through double.
Strings are ordinal and distinct from binary keys. Extensions have value
equality. Do not mutate returned binary keys while they are dictionary keys;
copy them before exposing them to code that may modify them.

Both readers reject duplicate keys, including numeric equivalents and
equal-content binary keys. Writers reject duplicates even in ordered pair
lists or dictionaries constructed with incompatible CLR equality. Maps
support non-null finite scalar, binary, extension and timestamp keys.
Null keys, array/tuple keys, map keys and nonfinite numeric keys are
**diagnosed restrictions in the default view**, even where Python permits them.
User extension registries,
old-spec compatibility mode, permissive invalid-UTF-8 mode, ordered-dict option,
and opt-in float32 writing are not implemented. Ordered input maps preserve
their enumeration order.

Applications requiring the broader Python key domain may opt into
`UnpackApplicationValue(..., allowExtendedMapKeys: true)` or its Try equivalent.
Ordinary maps still return `Dictionary<object, object?>`; maps with nil,
array/tuple or nonfinite numeric keys return ordered `MsgPackMap` entries.
Nested arrays represent Python's recursively converted tuple keys. Maps inside
keys remain unhashable and are rejected. `Pack` accepts an explicit `MsgPackMap`
without changing the accepted keys of ordinary dictionaries or pair lists.
This option is independent of request-handler lossless binary-key selection;
existing request handlers do not implicitly enable extended keys.

Extended maps use structural binary/tuple and exact numeric key equality.
NaN-containing keys stay distinct, matching separately decoded Python NaNs;
they bypass duplicate-key hashing to avoid quadratic same-hash work. Duplicate
non-NaN tuple keys are rejected, unlike the reference decoder's tuple branch,
which can overwrite earlier entries. This is an explicit validation difference,
not lossless support for duplicate-key wire maps. `MsgPackMap` copies its entry
array, not nested mutable values or keys; do not mutate a value while packing.
All existing byte, value, depth and container limits apply to extended maps.

## Bounds and failures

An immutable per-call `MsgPackLimits` policy controls encoded bytes (default
64 MiB), bytes per string/bin/extension payload including timestamps (64 MiB), entries per
container (1,000,000), total visited values including keys (2,000,001), and
nesting edges from the root (32). Depth can be explicitly configured from
0 through 128. A map consumes two child values per entry. Counts and the
remaining input are checked **before** allocating a container or payload.
An output stream enforces its encoded-byte bound during writing; string byte
counts are validated before allocating encoded strings. The byte limits are
wire-size limits, not an exact managed-heap quota: arrays, dictionaries,
UTF-16 strings, transient UTF-8/output copies and stream capacity have bounded
additional overhead. Callers should choose smaller policies for untrusted
small-message protocols and may explicitly raise limits for trusted larger
values. Timestamp payloads are intrinsically bounded to twelve bytes.

Malformed/truncated input, reserved tags, trailing bytes, unsupported map keys
and read-policy failures throw FormatException. Packing unsupported values,
cycles/depth exhaustion and write-policy failures throw ArgumentException.
Invalid policies throw ArgumentOutOfRangeException, including through the
Try API; Try methods return false and null only for data errors. Resource
exhaustion exceptions are not swallowed.

## Verification

The generator `tests/DotReticulum.Core.Tests/GenerateMsgPackApplicationVectors.py` executes the pinned module after `tests/Interop/reference_environment.py` verifies its source path, clean tracked RNS tree, SHA and LXMF 1.2.1. No upstream shim is used. Fixtures cover extension/timestamp bytes, Unicode and floats, length-header boundaries, map keys, observed upstream rejection classes and intentionally restricted keys. Existing MessagePack fixtures retain their original provenance.

`tests/DotReticulum.Core.Tests/MsgPackTests.cs` and `MsgPackApplicationValueTests.cs` cover reference bytes and hashes, lossless application-key round-trips, legacy projections, malformed data and configured read/write bounds. Run the focused suite with:

```powershell
dotnet test tests\DotReticulum.Core.Tests\DotReticulum.Core.Tests.csproj -c Release --filter "FullyQualifiedName~MsgPack"
```

Extended-map vectors execute nil, nested tuples, infinities, distinct NaNs
and mixed ordinary/binary keys against the same pinned source. Focused tests
also exercise duplicate structural keys, cyclic keys, truncated input and
configured depth limits.

Independent Python vectors establish reference bytes but do not by themselves prove live interoperability or NativeAOT execution. Those broader capabilities have separate evidence and platform boundaries.
