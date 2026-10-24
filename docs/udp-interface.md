# UDP interface contract

Reference: RNS 1.5.7, `a29bfb51c468945c96a1ee22cae0114e993e2391`,
`RNS/Interfaces/UDPInterface.py:45-145`. The constructor independently enables
receive and forwarding when each address/port pair is complete. A `port`
supplies only missing per-direction ports. `device` supplies its first IPv4
broadcast address only where an address was omitted. Source hostnames resolve
through IPv4 sockets; one datagram carries one packet, without extra framing.
The hardware MTU is 1,064 bytes and nominal bitrate is 10 Mbit/s.
`Reticulum.py:1030-1032,1133-1135` separately applies the `outgoing` policy.

Acceptance requires receive-only, forward-only and combined operation;
explicit port precedence; host/device/address-family resolution; independent
send/receive sockets; bounded input/output and complete datagram writes;
malformed/oversized input recovery; and start, cancellation and disposal races.
`GenerateParity6UdpVectors.py` executes the actual constructor with real
ephemeral loopback listeners and captures configuration state. No reference
method is replaced. It does not establish physical-interface interoperability.

The native API retains the existing endpoint-pair constructor. The options
API additionally permits independent receive/forward configuration and resolves
names during explicit asynchronous startup. The default configuration family
is IPv4, as upstream; direct endpoint callers retain the native IPv6 extension.
An unknown device, unresolvable address or configuration with no complete
direction fails explicitly. Incomplete unused pairs are diagnosed rather than
silently enabling defaults. A receive-only interface cannot transmit; its send
attempt fails explicitly instead of upstream's caught missing-field exception.
Forward-only receive enumeration waits for cancellation or disposal, rather
than falsely signaling a daemon interface failure.

Native buffers bound raw datagrams to hardware MTU plus IFAC overhead. Sending
borrows caller memory until completion, serializes in-flight datagrams and uses
cancellable backpressure rather than an unbounded library queue. Unix, Windows,
real broadcast devices and IPv6 are distinct acceptance surfaces; loopback
tests are not physical LAN/radio evidence.
