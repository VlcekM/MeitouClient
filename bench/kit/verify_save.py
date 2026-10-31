#!/usr/bin/env python3
"""verify_save.py: check a benchmark save folder against save-checksums.sha256 (same files, same SHA-256 each). Read-only.
Usage: python3 verify_save.py <save folder>      Exit 0 if it matches, 1 if not, 2 on bad usage.
"""
import os
import sys

import kitlib as K


def main(argv):
    if len(argv) != 1 or not os.path.isdir(argv[0]):
        print("usage: verify_save.py <save folder>", file=sys.stderr)
        return 2
    sums = os.path.join(K.KIT, "save-checksums.sha256")
    if not os.path.isfile(sums):
        print(f"verify_save.py: {sums} missing", file=sys.stderr)
        return 2
    ok, problems = K.verify_save(argv[0], sums)
    if not ok:
        for p in problems[:30]:
            print(p, file=sys.stderr)
        if len(problems) > 30:
            print(f"... and {len(problems) - 30} more", file=sys.stderr)
        print("MISMATCH: files differ or are missing (see above)", file=sys.stderr)
        return 1
    print(f"OK: {len(K.read_manifest(sums))} files match save-checksums.sha256")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
