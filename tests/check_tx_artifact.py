# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
"""Verify linked ELF descriptors, RP2350 UF2 blocks, and zero PII leaks for tx-bridge."""
import pathlib
import re
import struct
import sys

elf, uf2 = [pathlib.Path(p).read_bytes() for p in sys.argv[1:3]]
exp_vid = int(sys.argv[3], 16)
exp_pid = int(sys.argv[4], 16)
assert exp_vid != 0 and exp_pid != 0, 'Expected VID/PID must be non-zero'

assert elf[:7] == b'\x7fELF\x01\x01\x01'
shoff = struct.unpack_from('<I', elf, 32)[0]
shentsize, shnum = struct.unpack_from('<HH', elf, 46)
sections = [struct.unpack_from('<10I', elf, shoff + i * shentsize) for i in range(shnum)]
symbols = {}
symbol_addresses = {}
for s in sections:
    if s[1] != 2:
        continue
    strings = sections[s[6]]
    names = elf[strings[4]:strings[4] + strings[5]]
    for off in range(s[4], s[4] + s[5], s[9]):
        name, addr, size, _, _, ndx = struct.unpack_from('<IIIBBH', elf, off)
        if ndx >= len(sections):
            continue
        label = names[name:names.find(b'\0', name)].decode(errors='replace')
        symbol_addresses[label] = addr
        section = sections[ndx]
        start = section[4] + addr - section[3]
        symbols[label] = elf[start:start + size]

assert 'cyw43_arch_init' not in symbols, 'Must target non-wireless Pico 2 H'
assert symbol_addresses['__flash_binary_end'] <= 0x103c0000

device = symbols['probe_device_descriptor']
assert len(device) == 18 and device[4:7] == bytes(3)
assert struct.unpack_from('<HH', device, 8) == (exp_vid, exp_pid), f'Unexpected VID:PID in tx-bridge ELF'

config = symbols['probe_configuration_descriptor']
assert struct.unpack_from('<H', config, 2)[0] == len(config)
assert config[4] == 3

descs = []
pos = 0
while pos < len(config):
    size = config[pos]
    assert size >= 2 and pos + size <= len(config)
    descs.append(config[pos:pos + size])
    pos += size
assert pos == len(config)
interfaces = [d for d in descs if d[1] == 4]
assert [tuple(d[2:5]) for d in interfaces[:3]] == [(0, 0, 0), (1, 0, 0), (1, 1, 1)]

assert len(uf2) % 512 == 0
blocks = len(uf2) // 512
memory = {}
main_blocks = []
for i in range(blocks):
    block = uf2[i * 512:(i + 1) * 512]
    m0, m1, flags, addr, size, index, total, family = struct.unpack_from('<8I', block)
    assert (m0, m1) == (0x0a324655, 0x9e5d5157) and struct.unpack_from('<I', block, 508)[0] == 0x0ab16f30
    if flags == 0xa000:
        assert family == 0xe48bff57 and addr == 0x10ffff00
        continue
    assert flags == 0x2000 and family == 0xe48bff59 and size == 256
    assert 0x10000000 <= addr < 0x10400000 and addr % 256 == 0 and addr not in memory
    memory[addr] = block[32:32 + size]
    main_blocks.append((index, total))

assert main_blocks == [(i, len(main_blocks)) for i in range(len(main_blocks))]
payload = b''.join(memory[k] for k in sorted(memory))
assert device in payload, 'Device descriptor (with expected VID:PID) missing from tx-bridge UF2'
assert config in payload, 'Configuration descriptor missing from tx-bridge UF2'

for s in re.findall(rb'[ -~]{4,}', payload):
    low = s.decode('ascii', 'ignore').lower()
    for bad in ('users\\', 'users/', ':\\users', ':/users', '/home/', 'desktop-', 'laptop-', 'c:/', 'c:\\', 's:/', 'r:/', 't:/'):
        assert bad not in low, f'Unexpected string leak in UF2 payload: {s!r}'

print(f'PASS: tx-bridge VID=0x{exp_vid:04x} PID=0x{exp_pid:04x}, UAC1+HID {len(config)}B, {blocks} RP2350 UF2 blocks, 0 leaks')
