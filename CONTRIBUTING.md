# Contributing

Discuss substantial protocol or architecture changes in an issue first. Keep
pull requests focused and follow the PR checklist and Code of Conduct.

## Development

Install the .NET 10 SDK and use C# 13, nullable reference types, file-scoped
namespaces, and the repository's `.editorconfig`. Nullable diagnostics are errors.
Use `dotnet build DotReticulum.sln -c Release` and
`dotnet test DotReticulum.sln -c Release` before submitting changes.

Every behavior change needs tests, including malformed inputs, boundary lengths,
and failure paths. Protocol changes require independently generated reference
Python vectors with the exact upstream version/revision and generation method.
Round-trip tests alone are not interoperability evidence. Do not commit real
private keys, captured private traffic, or credentials.

## NativeAOT and portability

Production projects enable AOT/trimming analysis. Avoid reflection-based
serialization, dynamic code generation, and platform-specific assumptions.
Prefer spans/memory, cancellation-aware asynchronous operations, and explicit
wire formats. Use platform cryptography where supported; never invent primitives.
Curve25519 currently uses Bouncy Castle because portable .NET APIs do not expose
the required raw Ed25519/X25519 operations.

Verify native publishing with:

```sh
dotnet publish src/DotReticulum.Applications/DotReticulum.Applications.csproj \
  -c Release -r linux-x64 \
  -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

Use `win-x64` on Windows or `osx-arm64` on Apple Silicon. NativeAOT needs the
platform's native compiler/linker toolchain. Mobile, FreeBSD, embedded, and
additional architecture support must be validated separately; do not claim
coverage merely because managed code compiles.

## Review

Explain upstream wire compatibility and license implications. Keep the current
interoperability table honest: unit vectors are not live-node interop. Heavy mesh
and security workflows belong on schedules/manual triggers, not every push.
