"""Parity 8 leaf endpoint: reuse the verified link adapter with explicit proof policy.

Both incoming and outgoing destinations must use PROVE_ALL when C# asserts
bidirectional packet receipts. The generic adapter only sets that policy on its
incoming destination. No RNS code, timers or protocol methods are modified.
"""
import RNS
import live_link_node


configure_link = live_link_node.configure_link


def configure_proved_link(link, role, identity_box):
    link.destination.set_proof_strategy(RNS.Destination.PROVE_ALL)
    configure_link(link, role, identity_box)


if __name__ == "__main__":
    live_link_node.configure_link = configure_proved_link
    live_link_node.main(loglevel=RNS.LOG_DEBUG)
