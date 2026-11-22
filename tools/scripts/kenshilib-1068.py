"""Port KenshiLib function addresses from Kenshi Steam 1.0.65 to Steam 1.0.68 by code bytes.

Author: PZA. GPL-3.0-or-later, as MeitouClient.

Input:  the RE_Kenshi 1.0.65 exe (RE_Kenshi/Kenshi_x64.exe), the Steam 1.0.68 exe (kenshi_x64.exe),
        RE_Kenshi's RVA tables (RE_Kenshi/RVAs/GOG_1.0.65.br, Steam_1.0.65.br) and KenshiLib's Include folder.
Output: TSV  name  steam_1065_rva  steam_1068_rva  found_by  signature  header
        (signature = KenshiLib's declaration with parameter names, defaults and comments removed: types tell overloads apart)

Method, per KenshiLib function:
  1. its GOG 1.0.65 RVA (the header comment) -> Steam 1.0.65 RVA through RE_Kenshi's two parallel tables;
  2. the first 32 bytes of the function in the 1.0.65 exe, with the bytes that change between builds masked
     (rel32 call / jmp / jcc targets and RIP-relative displacements);
  3. a search of the 1.0.68 exe within +-64 KB of the same RVA; only one exact masked match is accepted;
     no match or several -> retried with 64, 128, 16 and 12 bytes; still not unique -> left out.
The class scope is read from the headers (class / struct / namespace blocks), not from the file name.

Usage: python tools/scripts/kenshilib-1068.py <exe_1065> <exe_1068> <RE_Kenshi/RVAs folder> <KenshiLib/Include> <out.tsv>
"""
import os
import re
import struct
import sys


def load_pe(path):
    d = open(path, 'rb').read()
    pe = struct.unpack_from('<I', d, 0x3c)[0]
    n = struct.unpack_from('<H', d, pe + 6)[0]
    osz = struct.unpack_from('<H', d, pe + 20)[0]
    secs = []
    for i in range(n):
        o = pe + 24 + osz + i * 40
        vsz, va, rsz, raw = struct.unpack_from('<IIII', d, o + 8)
        secs.append((d[o:o + 8].rstrip(b'\0').decode(), va, max(vsz, rsz), raw, rsz))
    return d, secs


def rva_to_off(secs, r):
    for _, va, sz, raw, rsz in secs:
        if va <= r < va + sz and r - va < rsz:
            return raw + r - va
    return None


def off_to_rva(secs, o):
    for _, va, sz, raw, rsz in secs:
        if raw <= o < raw + rsz:
            return va + o - raw
    return None


RIP_OPS = {0x8B, 0x8D, 0x89, 0x3B, 0x39, 0x03, 0x2B, 0x33, 0x0B, 0x23, 0x10, 0x11, 0x28, 0x29, 0x2E, 0x2F, 0x58,
           0x59, 0x5C, 0x5E, 0xB6, 0xB7, 0xBE, 0xBF, 0x80, 0x81, 0x83, 0xC6, 0xC7, 0xFF, 0x85, 0x87, 0x38, 0x3A,
           0x88, 0x8A}


def mask(code):
    keep = [True] * len(code)
    i = 0
    while i < len(code):
        b = code[i]
        if b in (0xE8, 0xE9) and i + 5 <= len(code):
            for k in range(1, 5): keep[i + k] = False
            i += 5
            continue
        if b == 0x0F and i + 1 < len(code) and 0x80 <= code[i + 1] <= 0x8F and i + 6 <= len(code):
            for k in range(2, 6): keep[i + k] = False
            i += 6
            continue
        i += 1
    for i in range(1, len(code) - 4):
        if (code[i] & 0xC7) == 0x05 and code[i - 1] in RIP_OPS:
            for k in range(1, 5): keep[i + k] = False
    return keep


def port(d5, s5, d8, s8, r5, n, window=0x10000):
    o5 = rva_to_off(s5, r5)
    if o5 is None: return []
    code = d5[o5:o5 + n]
    keep = mask(code)
    lo, hi = rva_to_off(s8, max(0x1000, r5 - window)), rva_to_off(s8, r5 + window)
    if lo is None or hi is None: return []
    head = code[:4] if all(keep[:4]) else None
    found, i = [], lo
    while i < hi:
        if head is not None:
            i = d8.find(head, i, hi)
            if i < 0: break
        if all((not keep[k]) or d8[i + k] == code[k] for k in range(n)):
            found.append(off_to_rva(s8, i))
        i += 1
    return found


HEAD = re.compile(r'^\s*(?:template\s*<[^>]*>\s*)?(class|struct|namespace)\s+(?:__declspec\([^)]*\)\s*)?'
                  r'([A-Za-z_][A-Za-z0-9_]*)\b([^;]*)$')


def kenshilib_functions(rvas_dir, include):
    def table(name):
        b = open(os.path.join(rvas_dir, name), 'rb').read()
        return list(struct.unpack('<%dI' % (len(b) // 4), b))
    gog, steam = table('GOG_1.0.65.br'), table('Steam_1.0.65.br')
    idx = {}
    for i, r in enumerate(gog):
        idx.setdefault(r, i)
    out = {}
    for root, _, files in os.walk(include):
        for f in files:
            if not f.endswith('.h'): continue
            path = os.path.join(root, f)
            stack, pending, in_comment = [], None, False
            for line in open(path, encoding='utf-8', errors='replace'):
                m = re.search(r'RVA = (0x[0-9A-Fa-f]+)', line)
                code = line.split('//')[0]
                if in_comment:
                    if '*/' in code: code, in_comment = code.split('*/', 1)[1], False
                    else: code = ''
                if '/*' in code:
                    code, rest = code.split('/*', 1)
                    in_comment = '*/' not in rest
                h = HEAD.match(code)
                if h: pending = h.group(2)
                if m and '_NV_' not in line:
                    i = idx.get(int(m.group(1), 16))
                    if i is not None and 0x1000 <= steam[i] < 0x2000000:
                        decl = code.strip()
                        fm = re.search(r'(~?[A-Za-z_][A-Za-z0-9_]*|operator\s*\S+)\s*\(', decl)
                        scope = '::'.join(s for s in stack if s)
                        name = (scope + '::' if scope else '') + (fm.group(1) if fm else '?')
                        out.setdefault(steam[i], (name, decl, os.path.relpath(path, include).replace(os.sep, '/')))
                for ch in code:
                    if ch == '{': stack.append(pending); pending = None
                    elif ch == '}' and stack: stack.pop()
                if ';' in code and '{' not in code: pending = None
    return out


def signature(decl):
    """KenshiLib's declaration with parameter names and default values removed (types only)."""
    decl = decl.strip().rstrip(';').strip()
    for tail in (' override', ' = 0'):
        if decl.endswith(tail): decl = decl[:-len(tail)].rstrip()
    op = decl.find('operator()')
    o = decl.find('(', op + len('operator()')) if op >= 0 else decl.find('(')
    if o < 0: return decl
    depth, c = 0, -1
    for i in range(o, len(decl)):
        if decl[i] == '(': depth += 1
        elif decl[i] == ')':
            depth -= 1
            if depth == 0: c = i; break
    if c < 0: return decl
    params, cur, depth = [], '', 0
    for ch in decl[o + 1:c]:
        if ch in '<([': depth += 1
        elif ch in '>)]': depth -= 1
        if ch == ',' and depth == 0: params.append(cur); cur = ''
        else: cur += ch
    if cur.strip(): params.append(cur)
    out = []
    for p in params:
        p = p.split('=')[0].strip()
        m = re.match(r'^(.*?[\s\*&>])([A-Za-z_][A-Za-z0-9_]*)(\s*\[[^\]]*\])?$', p)
        if m and m.group(1).strip() not in ('', 'const', 'unsigned', 'signed', 'struct', 'class', 'enum'):
            p = (m.group(1).strip() + (m.group(3) or '')).strip()
        out.append(re.sub(r'\s+', ' ', p))
    return decl[:o] + '(' + ', '.join(out) + ')' + decl[c + 1:]


def main():
    exe5, exe8, rvas, include, out_path = sys.argv[1:6]
    d5, s5 = load_pe(exe5)
    d8, s8 = load_pe(exe8)
    funcs = kenshilib_functions(rvas, include)
    rows, unique = [], 0
    for r5, (name, decl, hdr) in sorted(funcs.items()):
        r8, how = None, None
        for n in (32, 64, 128, 16, 12):
            hits = port(d5, s5, d8, s8, r5, n)
            if len(hits) == 1:
                r8, how = hits[0], 'bytes-%d' % n
                break
        if r8 is None: continue
        unique += 1
        rows.append('%s\t0x%X\t0x%X\t%s\t%s\t%s' % (name, r5, r8, how, signature(decl).replace('\t', ' '), hdr))
    with open(out_path, 'w', encoding='utf-8', newline='\n') as o:
        o.write('name\tsteam_1065_rva\tsteam_1068_rva\tfound_by\tsignature\theader\n')
        o.write('\n'.join(rows) + '\n')
    print('%d KenshiLib functions, %d ported uniquely' % (len(funcs), unique))


if __name__ == '__main__':
    main()
