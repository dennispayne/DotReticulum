# Remaining DotReticulum work

This is the single active progress checklist, extracted from the unchecked
items in the [archived plan](docs/archive/plan.md) on 2026-10-26.
[Development history](.github/development-history.md) supplies evidence;
the [compatibility ledger](docs/compatibility-contracts.md) defines residual
contracts and acceptance boundaries. Completed historical phases are not repeated.

- [ ] Parity 6 - Upstream interface coverage
  - [ ] RNodeInterface and RNodeMultiInterface full software contracts (serial/TCP accepted; Windows BLE software slice implemented, remaining BLE discovery/platform/device acceptance outstanding)
  - [x] Windows RNode BLE selector, native paired-device GATT provider, bounded notification/write stream and simulated reconnect contracts
  - [ ] Desktop RNode BLE live-advertisement selection, Linux/macOS providers and physical platform/device acceptance (Windows cached paired-device provider is partial; local host has no Bluetooth adapter)
  - [ ] I2PInterface
  - [ ] Android-specific adapters or documented native platform equivalents (deferred by user)
  - [ ] Complete UDP/TCP/serial configuration and validate real serial/radio hardware
  - [x] Authorized RAK4631 receive-only managed/native startup, live configuration echoes, radio-off control, serial reopen and unchanged EEPROM
  - [ ] Android USB/Bluetooth permission/adaptor contracts and on-device execution (deferred by user)
  - [x] Actual Java I2P local-destination bidirectional DATA before/after persisted bearer restart, independently for .NET and pinned upstream
  - [x] Windows published-native fresh and reused-configuration I2P encrypted DATA and validated proofs over actual Java local-destination streams
  - [ ] I2P public-tunnel delivery after persisted bearer restart (still fails for both .NET and pinned upstream); separate-router overlay, transport-enabled native persisted startup (fresh succeeds, reused-state probe fails) and broader published-native acceptance remain outstanding
  - [ ] Authorized physical serial/TNC/RNode/Multi/Weave and Android USB/Bluetooth/RF acceptance (one Windows RAK receive-only startup/reconfiguration/off/reopen verified; one radio only, RF acceptance explicitly retained as unverified; remaining devices/platform access/settings unavailable; see development history)
- [ ] Parity 7 - Upstream utility coverage (software accepted; physical device acceptance outstanding)
  - [x] Authorized RAK4631 native rnodeconf USB-triggered DFU, same-version reflash, changed-port recovery and reversible EEPROM-setting persistence/restoration
  - [ ] Remaining physical rnodeconf device/platform and RF acceptance (one RAK4631 verified; one radio cannot establish RF interoperability)
- [ ] Parity 8 - Compatibility and release acceptance
  - [ ] Broader mesh acceptance: broader alternate-route topologies, cold initial-window resource-loss recovery, sustained Channel/resource soak and impaired native-binary/per-bearer matrices (pinned upstream cold-window cancellation is independently reproduced, not passing recovery)
  - [x] Windows/Linux/macOS managed surviving .NET alternate route after actual pinned-primary-router crash: observed next-hop replacement after fresh announcements, distinct links in both initiator roles and exact bidirectional DATA/proofs without restarting either leaf
  - [x] Actual pinned Python-router forced crash/restart with persisted transport identity, surviving leaves, interrupted proved link/forwarding-state cleanup, explicit endpoint teardown, distinct recovered link and bidirectional measured-window impaired resources
  - [x] Windows/Linux/macOS managed .NET/Python endpoints automatically reach TIMEOUT after a remote router crash with surviving local routing, in both initiator roles, then prove a distinct fresh session; impaired native matrices remain outstanding
  - [ ] Windows, Linux and macOS managed/native feature flows; explicitly classify other upstream platform requirements
  - [x] Windows/Linux/macOS separately published public Buffer reader/writer consumer: exact bidirectional compressed/binary streams and EOF on an active link
  - [x] Windows native constrained Buffer streaming: exact 16 MB outbound/8 MB return, actual Channel loss in both directions, one surviving TCP session and active-link EOF
  - [ ] Cross-platform repeated native constrained Buffer streaming acceptance (Linux/Windows 3/3; macOS 2/3, reverse-send timeout remains unresolved); longer Channel/resource soak and broader impaired bearer matrices remain outstanding
  - [x] Darwin rnsh terminal backend and actual macOS feature acceptance (controlling TTY/resize/literal argv/signals/group teardown, all endpoint combinations, delayed first read, mode restoration, native terminal and actual Python roles passed in run 40926579478; upstream EOF race remains an explicit negative result)
  - [ ] Android host/providers, desktop RNode BLE and other missing/partial applicable software identified in the compatibility ledger
  - [ ] Per-interface interoperability and hardware evidence; untested platforms/devices remain unverified
- [ ] CI/CD and automation
  - [x] `ci-build-test.yml` (authorized early on 2026-10-26; green on all three OSes in run 40330280525)
  - [x] Consolidated compatibility report with every contract row and Linux/macOS/Windows columns (run 40521069184)
  - [x] Consolidated Unit/Integration/Native test tables with Linux/macOS/Windows columns (run 40521069184)
  - [x] Provision the separately fetched pinned Python reference for CI interoperability (hosted run 40521067543, verified source imports and 129 passing native/Python cases)
  - [x] Native .NET `rngit` hosted-CI upstream retrieval and separate upstream-drift comparison (runs 40519261522 / 40520520527; disclosed mirror-parent seed, nonzero network objects, no publisher-authentication claim)
  - [x] Initial advertised-core functionality family and directly executed Upstream results alongside the three .NET platforms (eight bounded rows; unmapped acceptance stays explicit; run 40521069184)
  - [x] Separate integration evidence from shared advertised-claim compatibility scenarios; use upstream-to-upstream as the multi-node baseline and a fixed upstream peer for every .NET candidate (Windows native and Python locally verified; cross-platform CI evidence recorded in development history)
  - [x] Make candidate-owned reference retrieval an advertised Git compatibility scenario; reuse one verified source bundle for the subsequent upstream baseline and Test, replacing the standalone fetch workflow (all 12 jobs passed in run 40621550584)
  - [x] Readable Pester compatibility claim tests, citation-only catalog, read-only discovery and individual Pester outcomes across four candidates (all four Compatibility jobs and renderer passed in runs 40626931637 and 40628676024; later discovery-only metadata cleanup locally verified)
  - [x] Table-only Compatibility summary with commit-pinned test-code hyperlinks, no Family column, and isolated child-process Actions output
  - [ ] Expand shared advertised-claim scenarios for heterogeneous routing, broader application request boundaries and Channel/Buffer
  - [x] Shared two-hop UDP/TCP routing in both directions with exact persisted bytes and observed hops (T055/T056; all four candidates passed in hosted run 40890841836)
  - [x] Shared ordered bidirectional Channel stream delivery through actual CLI consumers and pinned upstream message codecs (T057; all four candidates passed in hosted run 40915647175; broader Buffer APIs/loss/soak remain outstanding)
  - [x] Shared small and resource-sized nested management responses (T053/T058; all four hosted candidates accepted)
  - [x] Expand identity/cryptographic failures, forged delivery proofs, authenticated management requests and fixed-upstream-peer file-transfer/IFAC acceptance (37 distinct cases, 17 quoted claims; 37/37 passed for all four candidates, all 12 CI jobs green in run 40634153580)
  - [ ] Verified primary-source citation browsing links (Git destination resolved; derived NomadNet page link failed; external URI scheme and line anchors unverified; no GitHub fallback)
  - [x] Approved Verb-Noun standalone PowerShell entry points, discoverable help for every declared parameter and updated workflow/documentation callers
  - [x] Zero-argument local Compatibility runner with automatic native/reference preparation and fresh results directories
  - [x] `interop-test.yml` (all 12 jobs passed in run 40961479281)
  - [x] `e2e-mesh-test.yml` (all 12 jobs passed in run 40956389129; three repetitions of seven extended cases per platform)
  - [x] Advanced Static analysis workflow: CodeQL (Actions, C#, Python), pinned PSScriptAnalyzer, SARIF and consolidated finding summary; replaces GitHub-managed default setup (all five jobs passed in run 40644131301; C# full-database selection avoids overlay fallback warnings)
  - [x] Separate Unit/Integration production coverage in consolidated Build and Test platform summaries (all 12 jobs passed in run 40644131484; six suite/platform coverage artifacts verified)
  - [x] Basic accessibility goals, limitations and reporting in `ACCESSIBILITY.md`
  - [x] `stale.yml` inactivity reminders without auto-close (hosted dry run 40887769855; no eligible issues to exercise mutation)
