## Summary

<!-- What does this change and why? -->

## Checklist

- [ ] Tests added/updated, including malformed-input, boundary and failure paths; `dotnet test DotReticulum.slnx -c Release` passes
- [ ] Wire-format changes use independently generated Python-reference vectors (upstream revision and method recorded)
- [ ] Interoperability claim confirmed (unit vectors are not live-node interop; README table, `TODO.md` checkboxes and `.github/development-history.md` evidence updated honestly)
- [ ] NativeAOT/trimming check passes: `dotnet publish src/DotReticulum.Applications -c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true`
- [ ] No private keys, credentials, or captured private traffic committed
- [ ] Reticulum License and copyright notices preserved
