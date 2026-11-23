# DotReticulum

DotReticulum is a native C# / .NET 10 implementation of the [Reticulum Network Stack](https://reticulum.network) (RNS), aiming for feature and wire parity with the pinned Python reference implementation.

> **Warning:** DotReticulum is an experiment in having an LLM build a Reticulum implementation. Almost all of its code, tests and documentation were written by AI coding agents under human direction. It is experimental and **not** for production or security-critical use.
>
> It is **not** an official, endorsed or community-recognized Reticulum implementation, and it is not the work of the Reticulum project or its maintainer.

| Package | Contents |
|---|---|
| `DotReticulum.Crypto` | Ed25519, X25519, HKDF and Reticulum tokens |
| `DotReticulum.Core` | Identities, destinations, packets and the interface manager |
| `DotReticulum.Transport` | Announces, routing, path discovery, links, channels and resources |
| `DotReticulum.Interfaces` | UDP, TCP, serial and other upstream interfaces |
| `DotReticulum.Applications` | Runtime host (`ReticulumNode`) and utility implementations |

Interoperability status, documentation and evidence are in the [repository](https://github.com/dennispayne/DotReticulum).

Licensed under the [Reticulum License](https://github.com/dennispayne/DotReticulum/blob/main/LICENSE), which, among other conditions, prohibits use in systems able to purposefully harm human beings and use in AI, machine learning or language model training datasets.
