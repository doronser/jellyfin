#!/usr/bin/env python3
"""Re-sort contiguous plain `using X;` blocks (System.* first, then ordinal) in the given files (StyleCop SA1208/SA1210)."""
import re, sys
plain = re.compile(r'^using (?!static )(?![A-Za-z_]+ = )([\w.]+);\s*$')
def key(l):
    ns = plain.match(l).group(1)
    return (0 if ns == 'System' or ns.startswith('System.') else 1, ns.replace('.', '\x00') if False else ns)
for f in sys.argv[1:]:
    raw = open(f, encoding='utf-8-sig').read()
    bom = open(f, 'rb').read(3) == b'\xef\xbb\xbf'
    nl = '\r\n' if '\r\n' in raw else '\n'
    lines = raw.replace('\r\n', '\n').split('\n')
    out, i = [], 0
    while i < len(lines):
        if plain.match(lines[i]):
            j = i
            while j < len(lines) and plain.match(lines[j]): j += 1
            out += sorted(lines[i:j], key=key); i = j
        else:
            out.append(lines[i]); i += 1
    new = nl.join(out)
    if new != raw.replace('\r\n', '\n') .replace('\n', nl):
        open(f, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='').write(new)
