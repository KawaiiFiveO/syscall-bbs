"""Z-machine story analyser: header, colours, opcode counts, and optionally call sites and text.

Usage: python zscan.py story.z5|z8 [--strings PATTERN] [--calls OPNAME]

  --strings PATTERN  inline strings and dictionary words matching a regular expression
  --calls OPNAME     every site of one opcode, with its operands (S constant, L large constant, V variable)

Routines are found from the main routine, from call operands, and from every word below high
memory that unpacks to a decodable routine (routines stored in properties and arrays). That last
source also picks up data that happens to decode, so counts are upper bounds: judge a rare
opcode by its --calls operands. Versions 5 and 8 only.
"""
import re
import struct
import sys
from collections import Counter, defaultdict

data = open(sys.argv[1], 'rb').read()
args = sys.argv[2:]
V = data[0]
W = lambda a: struct.unpack_from('>H', data, a)[0]
PACK = {1: 2, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 4, 8: 8}[V]

# ---- Header ----
hdr = {
    'version': V, 'flags1': data[1], 'release': W(2), 'high_mem': W(4), 'initial_pc': W(6),
    'dictionary': W(8), 'objects': W(0x0A), 'globals': W(0x0C), 'static_mem': W(0x0E), 'flags2': W(0x10),
    'serial': data[0x12:0x18].decode('latin1'), 'abbreviations': W(0x18), 'file_length': W(0x1A) * PACK,
    'checksum': W(0x1C), 'routines_offset': W(0x28), 'strings_offset': W(0x2A),
    'terminating_chars': W(0x2E), 'standard_rev': W(0x32), 'alphabet': W(0x34), 'header_ext': W(0x36),
    'compiler': data[0x3C:0x40].decode('latin1', 'replace'),
}

# ---- Text ----
A0 = 'abcdefghijklmnopqrstuvwxyz'
A1 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
A2 = ' \n0123456789.,!?_#\'"/\\-:()'
if hdr['alphabet']:
    t = data[hdr['alphabet']:hdr['alphabet'] + 78]
    A0, A1 = ''.join(map(chr, t[0:26])), ''.join(map(chr, t[26:52]))
    A2 = ' \n' + ''.join(map(chr, t[54:78]))
unicode_table = None
ext_words = 0
if hdr['header_ext']:
    ext_words = W(hdr['header_ext'])
    if ext_words >= 3 and W(hdr['header_ext'] + 6):
        u = W(hdr['header_ext'] + 6)
        unicode_table = [W(u + 1 + 2 * i) for i in range(data[u])]


def zscii(c):
    if 155 <= c <= 251:
        table = unicode_table or [0xe4, 0xf6, 0xfc, 0xc4, 0xd6, 0xdc, 0xdf, 0xbb, 0xab, 0xeb, 0xef, 0xff, 0xcb, 0xcf, 0xe1, 0xe9,
                                  0xed, 0xf3, 0xfa, 0xfd, 0xc1, 0xc9, 0xcd, 0xd3, 0xda, 0xdd, 0xe0, 0xe8, 0xec, 0xf2, 0xf9,
                                  0xc0, 0xc8, 0xcc, 0xd2, 0xd9, 0xe2, 0xea, 0xee, 0xf4, 0xfb, 0xc2, 0xca, 0xce, 0xd4, 0xdb,
                                  0xe5, 0xc5, 0xf8, 0xd8, 0xe3, 0xf1, 0xf5, 0xc3, 0xd1, 0xd5, 0xe6, 0xc6, 0xe7, 0xc7, 0xfe,
                                  0xf0, 0xde, 0xd0, 0xa3, 0x153, 0x152, 0xa1, 0xbf]
        i = c - 155
        return chr(table[i]) if i < len(table) else '?'
    return chr(c) if 32 <= c < 127 else ('\n' if c == 13 else f'<{c}>')


def zstring(addr, depth=0):
    """Returns (text, address after)."""
    zchars = []
    while True:
        w = W(addr)
        addr += 2
        zchars += [(w >> 10) & 31, (w >> 5) & 31, w & 31]
        if w & 0x8000:
            break
    out, alpha, i = [], 0, 0
    while i < len(zchars):
        c = zchars[i]
        if c == 0:
            out.append(' ')
        elif c in (1, 2, 3):
            if i + 1 < len(zchars) and depth == 0:
                a = W(hdr['abbreviations'] + 2 * (32 * (c - 1) + zchars[i + 1])) * 2
                out.append(zstring(a, 1)[0])
            i += 1
        elif c in (4, 5):
            alpha = c - 3
            i += 1
            continue
        elif c == 6 and alpha == 2:
            if i + 2 < len(zchars):
                out.append(zscii(zchars[i + 1] << 5 | zchars[i + 2]))
            i += 2
        else:
            out.append((A0, A1, A2)[alpha][c - 6] if c >= 6 else '?')
        alpha = 0
        i += 1
    return ''.join(out), addr


# ---- Disassembly ----
TWO = {1: 'je', 2: 'jl', 3: 'jg', 4: 'dec_chk', 5: 'inc_chk', 6: 'jin', 7: 'test', 8: 'or', 9: 'and', 10: 'test_attr',
       11: 'set_attr', 12: 'clear_attr', 13: 'store', 14: 'insert_obj', 15: 'loadw', 16: 'loadb', 17: 'get_prop',
       18: 'get_prop_addr', 19: 'get_next_prop', 20: 'add', 21: 'sub', 22: 'mul', 23: 'div', 24: 'mod', 25: 'call_2s',
       26: 'call_2n', 27: 'set_colour', 28: 'throw'}
TWO_ST = {8, 9, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25}
TWO_BR = {1, 2, 3, 4, 5, 6, 7, 10}
ONE = {0: 'jz', 1: 'get_sibling', 2: 'get_child', 3: 'get_parent', 4: 'get_prop_len', 5: 'inc', 6: 'dec', 7: 'print_addr',
       8: 'call_1s', 9: 'remove_obj', 10: 'print_obj', 11: 'ret', 12: 'jump', 13: 'print_paddr', 14: 'load', 15: 'call_1n'}
ONE_ST = {1, 2, 3, 4, 8, 14}
ONE_BR = {0, 1, 2}
ZERO = {0: 'rtrue', 1: 'rfalse', 2: 'print', 3: 'print_ret', 4: 'nop', 7: 'restart', 8: 'ret_popped', 9: 'catch',
        10: 'quit', 11: 'new_line', 13: 'verify', 15: 'piracy'}
ZERO_ST = {9}
ZERO_BR = {13, 15}
VAR = {0: 'call_vs', 1: 'storew', 2: 'storeb', 3: 'put_prop', 4: 'aread', 5: 'print_char', 6: 'print_num', 7: 'random',
       8: 'push', 9: 'pull', 10: 'split_window', 11: 'set_window', 12: 'call_vs2', 13: 'erase_window', 14: 'erase_line',
       15: 'set_cursor', 16: 'get_cursor', 17: 'set_text_style', 18: 'buffer_mode', 19: 'output_stream',
       20: 'input_stream', 21: 'sound_effect', 22: 'read_char', 23: 'scan_table', 24: 'not', 25: 'call_vn',
       26: 'call_vn2', 27: 'tokenise', 28: 'encode_text', 29: 'copy_table', 30: 'print_table', 31: 'check_arg_count'}
VAR_ST = {0, 4, 7, 12, 22, 23, 24}
VAR_BR = {23, 31}
EXT = {0: 'save', 1: 'restore', 2: 'log_shift', 3: 'art_shift', 4: 'set_font', 5: 'draw_picture', 6: 'picture_data',
       7: 'erase_picture', 8: 'set_margins', 9: 'save_undo', 10: 'restore_undo', 11: 'print_unicode',
       12: 'check_unicode', 13: 'set_true_colour', 0x1d: 'buffer_screen'}
EXT_ST = {0, 1, 2, 3, 4, 9, 10, 12, 0x1d}
EXT_BR = {6}
CALLS = {'call_vs', 'call_vs2', 'call_vn', 'call_vn2', 'call_1s', 'call_1n', 'call_2s', 'call_2n'}
END = {'rtrue', 'rfalse', 'ret', 'ret_popped', 'print_ret', 'jump', 'quit', 'restart', 'throw'}


class Bad(Exception):
    pass


def decode(a):
    """Returns (name, operands [(type, value)], next address, branch target or None, inline text or None)."""
    start = a
    b = data[a]
    a += 1
    types = []
    if b == 0xBE:
        num = data[a]
        a += 1
        if num not in EXT:
            raise Bad(f'ext {num}')
        name, st, br = EXT[num], num in EXT_ST, num in EXT_BR
        tb = data[a]
        a += 1
        types = [(tb >> s) & 3 for s in (6, 4, 2, 0)]
    elif b >= 0xC0:
        num = b & 0x1F
        if b & 0x20:
            if num not in VAR:
                raise Bad(f'var {num}')
            name, st, br = VAR[num], num in VAR_ST, num in VAR_BR
        else:
            if num not in TWO:
                raise Bad(f'2op {num}')
            name, st, br = TWO[num], num in TWO_ST, num in TWO_BR
        tbytes = 2 if (b & 0x20 and num in (12, 26)) else 1
        for _ in range(tbytes):
            tb = data[a]
            a += 1
            types += [(tb >> s) & 3 for s in (6, 4, 2, 0)]
    elif b >= 0x80:
        t = (b >> 4) & 3
        num = b & 0xF
        if t == 3:
            if num == 14:
                raise Bad('0op 14 outside 0xBE')
            if num not in ZERO:
                raise Bad(f'0op {num}')
            name, st, br = ZERO[num], num in ZERO_ST, num in ZERO_BR
        else:
            name, st, br = ONE[num], num in ONE_ST, num in ONE_BR
            types = [t]
    else:
        num = b & 0x1F
        if num not in TWO:
            raise Bad(f'2op {num}')
        name, st, br = TWO[num], num in TWO_ST, num in TWO_BR
        types = [2 if b & 0x40 else 1, 2 if b & 0x20 else 1]
    ops = []
    for t in types:
        if t == 3:
            break
        if t == 0:
            ops.append(('L', W(a)))
            a += 2
        else:
            ops.append(('S' if t == 1 else 'V', data[a]))
            a += 1
    if st:
        a += 1
    target = None
    if br:
        x = data[a]
        a += 1
        if x & 0x40:
            off = x & 0x3F
        else:
            off = ((x & 0x3F) << 8) | data[a]
            a += 1
            if off & 0x2000:
                off -= 0x4000
        if off not in (0, 1):
            target = a + off - 2
    text = None
    if name in ('print', 'print_ret'):
        text, a = zstring(a)
    if name == 'jump':
        off = ops[0][1] if ops[0][0] == 'L' else None
        if off is None:
            raise Bad('jump with non-constant')
        if off & 0x8000:
            off -= 0x10000
        target = a + off - 2
    return name, ops, a, target, text


def routine(addr):
    """Flow-follows one routine. Returns {address: instruction} or raises Bad."""
    if addr >= len(data) or data[addr] > 15:
        raise Bad('locals')
    first = addr + 1 + (2 * data[addr] if V <= 4 else 0)
    seen, work = {}, [first]
    while work:
        a = work.pop()
        if a in seen:
            continue
        if not (hdr['high_mem'] <= a < len(data)):
            raise Bad('outside code')
        ins = decode(a)
        seen[a] = ins
        name, ops, nxt, target, _ = ins
        if target is not None:
            work.append(target)
        if name not in END:
            work.append(nxt)
    return seen


routines, failed = {}, set()


def explore(start_addrs):
    work = list(start_addrs)
    while work:
        r = work.pop()
        if r in routines or r in failed:
            continue
        try:
            body = routine(r)
        except (Bad, IndexError, struct.error):
            failed.add(r)
            continue
        routines[r] = body
        for name, ops, *_ in body.values():
            if name in CALLS and ops and ops[0][0] == 'L' and ops[0][1]:
                work.append(ops[0][1] * PACK)


# Main routine: in v5+ the initial PC is the first instruction of a routine with no locals.
main = hdr['initial_pc'] - 1
explore([main])
direct = set(routines)

# Routines reached indirectly (properties, arrays, variables): any word below high memory that
# unpacks to an aligned, decodable routine.
candidates = set()
for a in range(0, hdr['high_mem'] - 1):
    p = W(a) * PACK
    if hdr['high_mem'] <= p < len(data) and data[p] <= 15:
        candidates.add(p)
explore(candidates)

used = Counter()
where = defaultdict(list)
for r, body in routines.items():
    for a, (name, ops, _, _, _) in body.items():
        used[name] += 1
        where[name].append((r, a, ops))

# ---- Report ----
print('== Header')
for k, v in hdr.items():
    print(f'  {k}: {v:#x}' if isinstance(v, int) and k not in ('version', 'release') else f'  {k}: {v}')
print(f'  flags2 bits: {[b for b in range(16) if hdr["flags2"] >> b & 1]}')
print(f'  header extension words: {ext_words}, unicode table: {len(unicode_table) if unicode_table else "none"}')
if hdr['terminating_chars']:
    t, chars = hdr['terminating_chars'], []
    while data[t]:
        chars.append(data[t])
        t += 1
    print(f'  terminating chars: {chars}')
print(f'\n== Code: {len(routines)} routines ({len(direct)} reached by direct calls), {len(failed)} candidates rejected')
for name in sorted(used, key=lambda n: -used[n]):
    print(f'  {name:16} {used[name]}')

colours = Counter(tuple((t, v) for t, v in ops) for r, a, ops in where.get('set_colour', []))
print()
print('== set_colour operands (S = constant, V = variable)')
for ops, n in colours.most_common(25):
    print(f'  {n:5}  ' + ', '.join(f'{t}{v if v < 0x8000 else v - 0x10000}' for t, v in ops))
high = Counter()
for r, body in routines.items():
    for a, (name, ops, _, _, text) in body.items():
        for ch in text or '':
            if ord(ch) > 126:
                high[ch] += 1
        if name == 'print_char' and ops and ops[0][0] != 'V' and ops[0][1] > 126:
            high[f'print_char {ops[0][1]}'] += 1
print()
print(f'== Characters above ASCII in inline text: {dict(high) or "none"}')

if '--calls' in args:
    op = args[args.index('--calls') + 1]
    print(f'\n== {op} sites')
    for r, a, ops in where.get(op, []):
        print(f'  routine {r:#x} at {a:#x}: ' + ', '.join(f'{t}{v:#x}' if t == 'L' else f'{t}{v}' for t, v in ops))

if '--strings' in args:
    pat = re.compile(args[args.index('--strings') + 1], re.I)
    print('\n== Matching inline strings')
    for r, body in routines.items():
        for a, (name, ops, _, _, text) in body.items():
            if text and pat.search(text):
                print(f'  {a:#x} in routine {r:#x}: {text[:200]!r}')
    print('== Matching dictionary words')
    d = hdr['dictionary']
    n = data[d]
    d += 1 + n
    entry_len, count = data[d], struct.unpack_from('>h', data, d + 1)[0]
    d += 3
    for i in range(abs(count)):
        word = zstring(d + i * entry_len)[0]
        if pat.search(word):
            print(f'  {word}')
