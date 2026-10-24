"""Execute pinned Resource.py for file segmentation and bounded compression fixtures.

Uses the existing vector harness' real Link setup and disclosed reversible XOR
encryption shim; it does not establish live encrypted interoperability.
"""
import bz2
import hashlib
import json
import os
import importlib
from pathlib import Path
import shutil
import sys
from types import SimpleNamespace
import uuid

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "Interop"))
from reference_environment import verify_reference, PINNED_RNS_SHA
verify_reference()
import RNS
from GenerateResourceVectors import make_link, fixed_random_hash, counter, xor


def main():
    link = make_link()
    RNS.Identity.get_random_hash = staticmethod(fixed_random_hash)
    link.encrypt = xor
    path = HERE / "Fixtures" / "parity5-resource-input.bin"
    payload = bytes(index % 251 for index in range(2 * RNS.Resource.MAX_EFFICIENT_SIZE + 37))
    path.write_bytes(payload)
    segments = []
    original_hash = None
    metadata_size = 0
    try:
        source = path.open("rb")
        for index in range(1, 4):
            counter[0] = 0
            resource = RNS.Resource(source, link, metadata={"name": "source.bin"} if index == 1 else None,
                                    advertise=False, auto_compress=False, segment_index=index,
                                    original_hash=original_hash, sent_metadata_size=metadata_size)
            original_hash = resource.original_hash
            metadata_size = resource.metadata_size
            plain = xor(b"".join(part.data for part in resource.parts))[4:]
            segments.append({"index": index, "segments": resource.total_segments, "totalSize": resource.total_size,
                             "bodySize": len(plain), "bodySha256": hashlib.sha256(plain).hexdigest(),
                             "metadataSize": metadata_size, "hasMetadata": resource.has_metadata})
        source.close()
    finally:
        path.unlink(missing_ok=True)
    root = Path(os.environ["DOTRETICULUM_UPSTREAM"])
    compressed = bz2.compress(b"R" * 65537)
    compression_limits = []
    for size in [0, 65536, 65537]:
        decoder = bz2.BZ2Decompressor()
        expanded = decoder.decompress(compressed, max_length=size)
        compression_limits.append({"limit": size, "length": len(expanded), "eof": decoder.eof})
    truncated = bz2.BZ2Decompressor()
    truncated.decompress(compressed[:-1], max_length=65537)
    cache_root = HERE.parent.parent / "TestResults" / ("reference-resource-cache-" + uuid.uuid4().hex)
    cache_resources = cache_root / "resources"
    cache_packets = cache_root / "packets"
    cache_resources.mkdir(parents=True)
    cache_packets.mkdir()
    cleanup_cases = [("expired", "a" * 64, 86401), ("boundary", "b" * 64, 86400),
                     ("fresh", "c" * 64, 86399), ("unrelated", "user-file.bin", 86401)]
    module = importlib.import_module("RNS.Reticulum")
    original_time = module.time.time
    try:
        for label, name, age in cleanup_cases:
            path = cache_resources / name
            path.write_bytes(b"reference-owned-test")
            os.utime(path, (1700000000 - age, 1700000000 - age))
        module.time.time = lambda: 1700000000
        RNS.Reticulum._Reticulum__clean_caches(SimpleNamespace(resourcepath=str(cache_resources), cachepath=str(cache_packets)))
        cleanup_results = [{"label": label, "age": age, "retained": (cache_resources / name).exists()}
                           for label, name, age in cleanup_cases]
    finally:
        module.time.time = original_time
        shutil.rmtree(cache_root)
    result = {"pinned": PINNED_RNS_SHA, "resourceSourceSha256": hashlib.sha256((root / "RNS" / "Resource.py").read_bytes()).hexdigest(),
              "module": str(Path(RNS.__file__).resolve()), "payloadSha256": hashlib.sha256(payload).hexdigest(),
              "segments": segments, "compressedExpansion": compressed.hex(), "compressionLimits": compression_limits,
              "truncatedCompressionEof": truncated.eof,
              "reticulumSourceSha256": hashlib.sha256((root / "RNS" / "Reticulum.py").read_bytes()).hexdigest(),
              "resourceCacheAge": RNS.Reticulum.RESOURCE_CACHE, "resourceCacheCleanup": cleanup_results}
    (HERE / "Fixtures" / "parity5-resource-vectors.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
