"""Verify and print provenance for a separately provisioned upstream environment."""
import json
import RNS

from reference_environment import verify_reference

verify_reference(require_lxmf=True)
print(json.dumps({"rns_module": RNS.__file__, "rns_version": RNS.__version__}))
