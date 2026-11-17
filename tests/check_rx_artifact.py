# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
"""Verify linked ELF descriptors, RP2350 UF2 blocks, and zero PII leaks for rx-mixer."""
import pathlib
import re
import struct
import sys

elf, uf2 = [pathlib.Path(p).read_bytes() for p in sys.argv[1:3]]
exp_vid = int(sys.argv[3], 16)
exp_pid = int(sys.argv[4], 16)
assert exp_vid != 0 and exp_pid != 0, 'Expected VID/PID must be non-zero'

assert elf[:7] == b'\x7fELF\x01\x01\x01'
off = struct.unpack_from('<I', elf, 32)[0]
width, count = struct.unpack_from('<HH', elf, 46)
sections = [struct.unpack_from('<10I', elf, off + i * width) for i in range(count)]
symbols = {}
for s in sections:
    if s[1] != 2:
        continue
    st = sections[s[6]]
    names = elf[st[4]:st[4] + st[5]]
    for p in range(s[4], s[4] + s[5], s[9]):
        name, addr, n, _, _, ndx = struct.unpack_from('<IIIBBH', elf, p)
        if ndx >= len(sections):
            continue
        label = names[name:names.find(b'\0', name)].decode(errors='replace')
        sec = sections[ndx]
        start = sec[4] + addr - sec[3]
        symbols[label] = elf[start:start + n]

dev = symbols['pack_device_descriptor']
cfg = symbols['pack_configuration_descriptor']
assert struct.unpack_from('<HH', dev, 8) == (exp_vid, exp_pid), 'Unexpected VID:PID in rx-mixer ELF'
assert dev[4:7] == bytes((0xef, 2, 1)), 'Device must advertise IAD misc class'
assert len(cfg) == struct.unpack_from('<H', cfg, 2)[0] == 280 and cfg[4] == 4

desc = []
i = 0
while i < len(cfg):
    n = cfg[i]
    assert n >= 2 and i + n <= len(cfg)
    desc.append(cfg[i:i + n])
    i += n

interfaces = [x for x in desc if x[1] == 4]
assert [tuple(x[2:5]) for x in interfaces] == [
    (0, 0, 0), (1, 0, 0), (1, 1, 1), (2, 0, 0), (2, 1, 1), (3, 0, 1)
]

cs = [x for x in desc if x[1] == 0x24]
clk_ids = [x[3] for x in cs if x[2] == 10]
fu_ids = [x[3] for x in cs if x[2] == 6]
assert clk_ids == [0x04, 0x14]
assert fu_ids == [0x02, 0x13]

eps = [x for x in desc if x[1] == 5]
assert len(eps) == 3
assert eps[0][:7] == bytes((7, 5, 0x01, 0x09, 196, 0, 1))
assert eps[1][:7] == bytes((7, 5, 0x81, 0x05, 196, 0, 1))
assert eps[2][2] == 0x82 and eps[2][3] == 0x03

assert 'cyw43_arch_init' not in symbols, 'Must target non-wireless Pico 2 H'
assert 'uart_init' in symbols and 'pio_sm_init' in symbols and 'flash_range_program' in symbols
assert len(symbols['uart_ring']) == 8192 and 'pack_selftest_run' in symbols

assert len(uf2) % 512 == 0
memory = {}
blocks = []
for p in range(0, len(uf2), 512):
    b = uf2[p:p + 512]
    m0, m1, flags, addr, n, idx, total, family = struct.unpack_from('<8I', b)
    assert (m0, m1) == (0x0a324655, 0x9e5d5157) and struct.unpack_from('<I', b, 508)[0] == 0x0ab16f30
    if flags == 0xa000:
        assert family == 0xe48bff57 and addr == 0x10ffff00
        continue
    assert flags == 0x2000 and family == 0xe48bff59 and n == 256
    assert 0x10000000 <= addr < 0x10400000 and addr % 256 == 0 and addr not in memory
    memory[addr] = b[32:288]
    blocks.append((idx, total))

assert blocks == [(k, len(blocks)) for k in range(len(blocks))]
payload = b''.join(memory[k] for k in sorted(memory))
assert cfg in payload

for s in re.findall(rb'[ -~]{4,}', payload):
    low = s.decode('ascii', 'ignore').lower()
    for bad in ('users\\', 'users/', ':\\users', ':/users', '/home/', 'desktop-', 'laptop-', 'c:/', 'c:\\', 's:/', 'r:/', 't:/'):
        assert bad not in low, f'Unexpected string leak in UF2 payload: {s!r}'

print(f'PASS: rx-mixer VID=0x{exp_vid:04x} PID=0x{exp_pid:04x}, UAC2+HID 280B, {len(blocks)} RP2350 UF2 blocks, 0 leaks')
