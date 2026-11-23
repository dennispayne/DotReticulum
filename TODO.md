# Remaining DotReticulum work

This is the single active checklist. The [compatibility ledger](docs/compatibility-contracts.md)
details the remaining contracts, and the [development history](.github/development-history.md)
records their evidence.

DotReticulum covers the RNS stack and its bundled utilities. LXMF, LXST,
NomadNet, Sideband and telemetry belong to [DotReticulum.Ecosystem](https://github.com/dennispayne/DotReticulum.Ecosystem).
Acceptance is limited to software that can be exercised on the existing Windows
x64, Linux x64 and macOS ARM64 Actions runners. Physical-device testing and
other platforms are out of scope and remain unverified; software tests do not
establish hardware interoperability.

## Reference and compatibility

- [ ] Move to the next verified upstream release: fetch and review its changes, update the reference pin and version, and revalidate affected contracts.
- [ ] Expand shared executable compatibility cases for advertised capabilities, including heterogeneous routing, application requests and Channel/Buffer.
- [ ] Exercise the remaining managed and native feature flows and testable interface interoperability on all three CI platforms.

## Network reliability

- [ ] Broaden mesh acceptance for alternate routes, sustained Channel/resource operation, and impaired native and per-bearer paths.
- [ ] Investigate cold initial-window resource-loss recovery; pinned upstream also cancels in the recorded impaired case.
- [ ] Diagnose the intermittent macOS reverse-send timeout that did not recur in the five-repeat run.

## Interfaces

- [ ] Complete remaining UDP/TCP/serial configuration and other testable software contracts in the compatibility ledger.
- [ ] Complete RNode/Multi software, including desktop BLE advertisement selection and Linux/macOS providers.
- [ ] Complete I2P software and acceptance for separate routers, public-tunnel and persisted transport recovery, and broader native execution. Pinned upstream also fails the recorded public-tunnel restart and reused-state transport cases.
