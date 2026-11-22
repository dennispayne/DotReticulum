# MessagePack application values

Reticulum uses MessagePack wherever structured application data crosses the network: request paths and data, responses, resource metadata and the utilities built on them. The pinned reference uses its vendored `umsgpack` module, which packs and unpacks ordinary Python values.

DotReticulum provides `MsgPack`, an explicit codec for a fixed set of .NET value types. It is not a CLR object serializer. It uses no reflection, registered classes or dynamic code, so it is safe for trimming and NativeAOT.

## Packing and unpacking values

```csharp
byte[] packed = MsgPack.Pack(new Dictionary<object, object?>
{
    ["name"] = "node",
    [new byte[] { 0x01, 0x02 }] = 42L,
});

var map = (Dictionary<object, object?>)MsgPack.UnpackApplicationValue(packed)!;
```

New application code should use `UnpackApplicationValue` or `TryUnpackApplicationValue`. These keep binary map keys as `byte[]`, which is what the Python reference produces. The older `Unpack` and `TryUnpack` methods return a legacy view, described in [Map keys](#map-keys).

## Supported values

`Pack` accepts:

* null and `bool`
* signed and unsigned 8, 16, 32 and 64-bit integers
* `float` and `double`
* `string`, `byte[]` and `ReadOnlyMemory<byte>`
* `object?[]`, including jagged arrays
* `Dictionary<object, object?>` and ordered `IReadOnlyList<KeyValuePair<object, object?>>` maps
* `MsgPackExtension`, `MsgPackTimestamp` and `MsgPackMap`

Other types throw `ArgumentException`. In particular, the codec does not enumerate arbitrary `IEnumerable` values or convert `decimal`, `BigInteger`, enums, `char`, `DateTime` or `DateTimeOffset`.

Values follow these rules:

* **Integers** use the reference's shortest representation. They decode to `long`, or to `ulong` above `long.MaxValue`.
* **Floating point** values are always written as float64. Both float32 and float64 decode to `double`. NaN, infinities and signed zero are preserved.
* **Strings** use strict UTF-8. Invalid bytes throw `FormatException`. Unpaired UTF-16 surrogates throw `ArgumentException` instead of being replaced.
* **Ordered maps** keep their enumeration order.

The `ToInt64` helper truncates finite doubles toward zero, as before, and diagnoses NaN, infinity and overflow instead of returning a sentinel. It does not affect how integers are decoded from the wire.

## Extensions and timestamps

`MsgPackExtension(sbyte type, ReadOnlySpan<byte> data)` copies and owns its payload. Every extension header size can be read, and the reference's shortest header is written. Unknown types stay opaque, and no handler runs implicitly.

Extension type -1 is the MessagePack timestamp and is represented by `MsgPackTimestamp(long seconds, uint nanoseconds)`. Seconds are UTC Unix seconds, and nanoseconds must be below 1,000,000,000. The codec reads and writes the 32, 64 and 96-bit layouts and chooses the same shortest layout as upstream datetime encoding.

Explicit timestamp values avoid time-zone guessing and CLR calendar limits.

## Map keys

Python allows a wider range of map keys than a CLR dictionary handles naturally, and the reference produces binary keys. DotReticulum therefore offers three views of decoded maps.

| View | Methods | Binary keys | Other keys |
| --- | --- | --- | --- |
| Legacy | `Unpack`, `TryUnpack` | Uppercase hexadecimal strings | Finite scalar, extension and timestamp keys |
| Application | `UnpackApplicationValue`, `TryUnpackApplicationValue` | `byte[]` | Finite scalar, extension and timestamp keys |
| Extended | `UnpackApplicationValue(..., allowExtendedMapKeys: true)` and its Try equivalent | `byte[]` | Adds nil, array or tuple and nonfinite numeric keys |

Unless you opt into extended keys, map keys must be non-null finite scalar, binary, extension or timestamp values. Null, array or tuple, map and nonfinite numeric keys are diagnosed restrictions in the default views, even where Python permits them.

Both readers reject duplicate keys, including numerically equivalent keys and binary keys with equal content. Writers reject duplicates too, even in ordered pair lists or in dictionaries constructed with an incompatible CLR comparer.

### Legacy view

Existing runtime, persistence, Git and LXMF consumers keep the legacy projection and are not migrated implicitly. A binary key that collides with a text key fails explicitly instead of silently losing a field. The legacy view is not lossless, so you must not repack it to reproduce binary-key wire bytes.

### Application view

The application view returns `Dictionary<object, object?>` maps whose comparer uses:

* content equality for `byte[]` and `ReadOnlyMemory<byte>`
* exact Python-style numeric equality (`true == 1 == 1.0`), without rounding large integers through `double`
* ordinal string equality, with strings distinct from binary keys
* value equality for extensions

Do not mutate a returned binary key while it is a dictionary key. Copy it before handing it to code that might modify it.

Link requests can opt into this view:

* Clients use `Request(..., useApplicationValues: true)` to receive lossless response values.
* Handlers use the same option on `RegisterRequestHandler` or `RegisterAsyncRequestHandler` to receive lossless `RequestContext.Data`.

The option applies to both small packets and resource-backed requests and responses. Existing handlers and clients keep the legacy view by default, and a map that cannot be represented in the legacy view is diagnosed rather than projected over a colliding text key. Small-response size limits are measured on the lossless wire value, before any legacy projection.

Resource metadata and existing runtime and tool protocols keep the legacy view. The request option changes request data and response values, not file-response metadata. Use text keys for portable metadata. A binary and text key collision in metadata is an explicit decoding failure.

### Extended view

Applications that need Python's broader key domain can opt into extended keys. Ordinary maps are still returned as `Dictionary<object, object?>`. Maps containing nil, array or tuple, or nonfinite numeric keys are returned as ordered `MsgPackMap` entries.

* Nested arrays represent Python's recursively converted tuple keys.
* Maps inside keys are unhashable and are rejected.
* Keys use structural binary and tuple equality and exact numeric equality.
* NaN-containing keys stay distinct, matching separately decoded Python NaNs. They bypass duplicate-key hashing to avoid quadratic same-hash work.
* `Pack` accepts an explicit `MsgPackMap` without widening the keys accepted for ordinary dictionaries or pair lists.
* `MsgPackMap` copies its entry array, but not nested mutable keys or values. Do not mutate a value while it is being packed.

Extended keys are independent of the request-handler option above. Existing request handlers do not enable extended keys implicitly. All byte, value, depth and container limits apply to extended maps.

## Limits and failures

Each call takes an immutable `MsgPackLimits` policy:

| Limit | Property | Default |
| --- | --- | --- |
| Encoded bytes | `MaxEncodedBytes` | 64 MiB |
| Bytes per string, binary or extension payload, including timestamps | `MaxValueBytes` | 64 MiB |
| Entries per container | `MaxContainerEntries` | 1,000,000 |
| Total visited values, including keys | `MaxValues` | 2,000,001 |
| Nesting edges from the root | `MaxDepth` | 32, configurable from 0 to 128 |

A map consumes two child values per entry. Counts and the remaining input are checked before a container or payload is allocated. An output stream enforces its encoded-byte limit while writing, and string byte counts are validated before encoded strings are allocated. Timestamp payloads are intrinsically limited to 12 bytes.

The byte limits bound wire size, not managed-heap use. Arrays, dictionaries, UTF-16 strings, transient UTF-8 and output copies, and stream capacity add bounded overhead. Choose smaller policies for untrusted small-message protocols, and raise them explicitly only for trusted larger values.

Failures are reported by exception type:

| Exception | Cause |
| --- | --- |
| `FormatException` | Malformed or truncated input, reserved tags, trailing bytes, unsupported map keys, read-policy failures |
| `ArgumentException` | Unsupported values, cycles or depth exhaustion while packing, write-policy failures |
| `ArgumentOutOfRangeException` | Invalid policies, including through the Try methods |

The Try methods return false and null only for data errors. Resource-exhaustion exceptions are not swallowed.

## Differences from upstream

* Timestamps keep nanoseconds, where Python datetime truncates to microseconds, and cover the full signed 64-bit seconds range. Invalid nanoseconds are rejected rather than normalized as Python `timedelta` may do.
* Noncanonical but valid encodings are accepted and canonicalized when repacked. Float32 values are likewise repacked as float64.
* Null, array or tuple, map and nonfinite numeric keys are rejected outside the extended view.
* Duplicate non-NaN tuple keys are rejected. The reference decoder's tuple branch can overwrite earlier entries instead. This is a validation difference, not lossless support for duplicate-key maps.

## Limitations

These `umsgpack` features are not implemented:

* user extension registries
* old-spec compatibility mode
* permissive invalid-UTF-8 mode
* the ordered-dict option
* opt-in float32 writing

## Verification

The reference is RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`, `RNS/vendor/umsgpack.py`, SHA-256 `0710f48f495193a764048c883b27b51cc7e7f7ae12486d4aad859994271cba50`. The relevant ranges are integer, string and binary packing (lines 283–356), extension and timestamp packing (370–415), dispatch (451–535), string, extension and timestamp decoding (646–747) and map decoding (772–805).

`tests\DotReticulum.Core.Tests\GenerateMsgPackApplicationVectors.py` executes the pinned module after `tests\Interop\reference_environment.py` verifies its source path, a clean tracked RNS tree, the commit and LXMF 1.2.1. No upstream shim is used. The fixtures cover:

* extension and timestamp bytes
* Unicode and floats
* length-header boundaries
* map keys, observed upstream rejection classes and intentionally restricted keys
* extended maps with nil, nested tuples, infinities, distinct NaNs and mixed ordinary and binary keys

Existing MessagePack fixtures keep their original provenance.

`tests\DotReticulum.Core.Tests\MsgPackTests.cs` and `MsgPackApplicationValueTests.cs` cover reference bytes and hashes, lossless round trips, legacy projections, malformed and truncated data, duplicate structural keys, cyclic keys and configured read, write and depth limits. Run the focused suite with:

```powershell
dotnet test tests\DotReticulum.Core.Tests\DotReticulum.Core.Tests.csproj -c Release --filter "FullyQualifiedName~MsgPack"
```

Independent Python vectors establish reference bytes. They do not by themselves prove live interoperability or NativeAOT execution, which have separate evidence and platform boundaries.
