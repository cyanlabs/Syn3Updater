#!/usr/bin/env python3
"""Anonymises an Interrogator log (Sync 3 APIM snapshot) so it can be committed as a test fixture.

    tools/sanitize-interrogator-log.py IN.xml OUT.xml [--vin XXXXXXXXXXXXXXXX1]

What is replaced (everything else, such as part numbers, versions and partition sizes, is kept because the tests read it):
  * the VIN (default: empty, or the fake VIN given with --vin)
  * every MAC address, with 00:00:00:00:00:NN
  * the private sub-node serial number (DID F141)
  * the as-built configuration blocks (DE00 to DEFF), with same-length hex derived from a hash, so nothing of the car's real configuration is left
  * the SyncData certificate blob, with a placeholder
(The APIM model, DID F113, is kept: it is what the MY20 check reads. The test fixtures use 5U5T-14G371-BKC for an MY20 unit and 3U5T-14G371-ADD for an older one.)
The file layout (line breaks, indentation) is not touched. Run it, then read the output before committing it.
"""
import hashlib, re, sys

def sanitize(text: str, vin: str = "") -> str:
    text = re.sub(r"(<p:VIN>).*?(</p:VIN>)", lambda m: m.group(1) + vin + m.group(2), text, flags=re.S)
    n = iter(range(1, 1000))
    text = re.sub(r"\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b", lambda m: "00:00:00:00:00:%02x" % next(n), text)

    def did(pattern, repl):
        rx = re.compile(r'(<d2p1:DID [^>]*didValue="' + pattern + r'"[^>]*>\s*<d2p1:Response>)(.*?)(</d2p1:Response>)', re.S)
        return rx.sub(lambda m: m.group(1) + repl(m) + m.group(3), text)

    text = did("F141", lambda m: "00000000" if m.group(2).strip() else m.group(2))

    def fake_hex(m):
        old = m.group(2)
        if not re.fullmatch(r"[0-9a-fA-F]+", old.strip() or "x"): return old            # "Blank" and friends stay
        digest = hashlib.sha256(old.encode()).hexdigest() * 4
        return digest[: len(old)]
    text = did(r"DE[0-9A-Fa-f]{2}", fake_hex)
    text = re.sub(r"(<d2p1:SyncData>|<p:SyncData>)(.*?)(</d2p1:SyncData>|</p:SyncData>)", lambda m: m.group(1) + "U0FOSVRJWkVE" + m.group(3), text, flags=re.S)
    return text

if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    vin = sys.argv[sys.argv.index("--vin") + 1] if "--vin" in sys.argv else ""
    if "--vin" in sys.argv: args.remove(vin)
    if len(args) != 2: sys.exit(__doc__)
    open(args[1], "w", encoding="utf-8", newline="").write(sanitize(open(args[0], encoding="utf-8").read(), vin))
