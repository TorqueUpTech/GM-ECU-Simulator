"""
Reference implementation of GM DPS 4.52 "Algo 92" (E92 family) seed-to-key.

Extracted from C:\\DPS\\sa015bcr.dll (the per-algorithm key engine) and
verified against DPS's own output by:

  1. Hooking sa015bcr.dll's `sa015bcr` export via a logging proxy.
  2. Capturing the password blob DPS passes for algoId=0x92.
  3. Running this implementation end-to-end and matching the captured key.

The password blob is sale.dll-decrypted-in-place at session start; it lives
in IVCS5B.dll's .data section at offset 0x3398 + algoId*62 (62-byte ASCII
entries). To get the blob for a different algorithm, re-run the hook with
an archive that triggers that algoId.

Usage:
  python algo92_verify.py
"""

import base64, hashlib
from Crypto.Cipher import AES

# === Captured from sa015bcr_hook.txt during 2018 Silverado E92 unlock ===
ALGO_92_PASSWORD = "01sgqbD6nsKDz8SawCanylLyqwtoFUeMsY2Y6FxEi4rP0A9QCSAP8Ivi0OzQk="


def gm_seed_to_key(seed: bytes, algo_id: int, password: str) -> bytes:
    """
    Compute the GM Service-$27 response key.

    seed     : 5-byte challenge from ECU's $67 01 response
    algo_id  : algorithm id (e.g. 0x92 for E92)
    password : 62-char ASCII password blob for this algorithm
               (2-char decimal length marker '01' or '03' + 60 base64 chars)

    Returns the 5-byte response key.
    """
    assert len(seed) == 5, "seed must be 5 bytes"
    assert len(password) == 62, "password must be exactly 62 chars (2 prefix + 60 base64)"

    length_marker = int(password[0:2])
    assert length_marker in (1, 3), f"invalid length marker {length_marker!r}"

    blob = base64.b64decode(password[2:62])  # 60 chars -> 45 bytes; we use first 44
    payload = blob[0:32]
    a = int.from_bytes(blob[32:34], "big")
    b = int.from_bytes(blob[34:36], "big")
    # blob[36:44] is the 8-byte RSA/HMAC signature (verifyPassword); we trust it.

    assert b == algo_id, f"password's embedded algoId 0x{b:04X} != requested 0x{algo_id:04X}"
    assert a <= 0xFF - seed[4], "A must be <= 0xFF - seed[4]"
    n_iter = 0xFF - seed[4] - a

    h = payload
    for _ in range(n_iter):
        h = hashlib.sha256(h).digest()

    aes_key = h[0:16]
    plaintext = b"\xff" * 11 + seed
    ciphertext = AES.new(aes_key, AES.MODE_ECB).encrypt(plaintext)
    return ciphertext[0:5]


def main():
    # Known seed/key pairs (from prior community publication + our own hook capture)
    test_vectors = [
        # synthetic seed verified end-to-end with the hook
        ("11 22 33 44 06", "EC BF F7 87 A4"),
        # community-published E92 pairs
        ("43 89 30 D3 06", "CD CF 83 5F 22"),
        ("91 81 4E B9 06", "B0 7B 1E 76 BC"),
        ("C0 CA D2 9E 06", "1E 84 FD BC 03"),
        ("DE F0 8C D3 06", "F3 B2 B4 55 C8"),
        ("9C 92 6F F5 06", "0D 16 59 D3 B9"),
        ("D8 B1 D5 40 06", "23 B7 1F FC F4"),
    ]

    all_pass = True
    print(f"{'seed':<20} {'expected':<20} {'computed':<20} {'match'}")
    print("-" * 70)
    for seed_hex, expected_hex in test_vectors:
        seed = bytes.fromhex(seed_hex.replace(" ", ""))
        expected = bytes.fromhex(expected_hex.replace(" ", ""))
        computed = gm_seed_to_key(seed, 0x92, ALGO_92_PASSWORD)
        match = computed == expected
        all_pass = all_pass and match
        print(f"{seed.hex():<20} {expected.hex():<20} {computed.hex():<20} {'OK' if match else 'FAIL'}")
    print()
    print("ALL PASS" if all_pass else "SOME FAILED")


if __name__ == "__main__":
    main()
