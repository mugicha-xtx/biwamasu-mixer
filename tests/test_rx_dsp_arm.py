# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
"""Verify Peace float32 biquad math, compile rx-mixer DSP C tests, and run ARM emulation if Unicorn is installed."""
import json
import math
import os
import pathlib
import re
import shutil
import struct
import subprocess
import sys


def f32(x: float) -> float:
    return struct.unpack('<f', struct.pack('<f', x))[0]


def rbj_biquad_f64(kind: str, fc: float, gdb: float, q: float, fs: float = 48000.0):
    a_amp = 10.0 ** (gdb / 40.0)
    w0 = 2.0 * math.pi * fc / fs
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / (2.0 * q)
    if kind == 'PK':
        b0, b1, b2 = 1.0 + alpha * a_amp, -2.0 * cw, 1.0 - alpha * a_amp
        a0, a1, a2 = 1.0 + alpha / a_amp, -2.0 * cw, 1.0 - alpha / a_amp
    elif kind == 'LSC':
        sa = 2.0 * math.sqrt(a_amp) * alpha
        b0 = a_amp * ((a_amp + 1.0) - (a_amp - 1.0) * cw + sa)
        b1 = 2.0 * a_amp * ((a_amp - 1.0) - (a_amp + 1.0) * cw)
        b2 = a_amp * ((a_amp + 1.0) - (a_amp - 1.0) * cw - sa)
        a0 = (a_amp + 1.0) + (a_amp - 1.0) * cw + sa
        a1 = -2.0 * ((a_amp - 1.0) + (a_amp + 1.0) * cw)
        a2 = (a_amp + 1.0) + (a_amp - 1.0) * cw - sa
    elif kind == 'HSC':
        sa = 2.0 * math.sqrt(a_amp) * alpha
        b0 = a_amp * ((a_amp + 1.0) + (a_amp - 1.0) * cw + sa)
        b1 = -2.0 * a_amp * ((a_amp - 1.0) + (a_amp + 1.0) * cw)
        b2 = a_amp * ((a_amp + 1.0) + (a_amp - 1.0) * cw - sa)
        a0 = (a_amp + 1.0) - (a_amp - 1.0) * cw + sa
        a1 = 2.0 * ((a_amp - 1.0) - (a_amp + 1.0) * cw)
        a2 = (a_amp + 1.0) - (a_amp - 1.0) * cw - sa
    else:
        raise ValueError(f'Unsupported filter kind: {kind}')
    return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0)


root = pathlib.Path(os.environ.get('BRIDGE_TEST_ROOT', str(pathlib.Path(__file__).resolve().parents[1])))
deps = pathlib.Path(os.environ.get('BRIDGE_DEPS_DIR', str(root / '.deps')))

peace_path = root / 'presets/sample-peace.txt'
peace_text = peace_path.read_text(encoding='utf-8')
assert 'Device:' not in peace_text and '{' not in peace_text, 'Sample preset must stay device-ID free'
preamp_m = re.search(r'Preamp:\s*([+-]?\d+(?:\.\d+)?)\s*dB', peace_text)
assert preamp_m, 'Missing Preamp in presets/sample-peace.txt'
preamp_db = float(preamp_m.group(1))
assert preamp_db == 0.0, f'Expected 0 dB sample Preamp, got {preamp_db}'
bands_f64 = []
for m in re.finditer(r'Filter\s+\d+:\s+ON\s+(LSC|PK|HSC)\s+Fc\s+([0-9.]+)\s+Hz\s+Gain\s+([+-]?[0-9.]+)\s*dB\s+Q\s+([0-9.]+)', peace_text):
    gain_db = float(m.group(3))
    assert gain_db == 0.0, f'Expected 0 dB sample Gain, got {gain_db}'
    bands_f64.append(rbj_biquad_f64(m.group(1), float(m.group(2)), gain_db, float(m.group(4))))
assert len(bands_f64) == 11, f'Expected 11 Peace filters, found {len(bands_f64)}'

def check_eq_response_err(pre_db: float, coeffs_f64, freqs) -> float:
    coeffs_f32 = [tuple(f32(v) for v in row) for row in coeffs_f64]
    pre_f64 = 10.0 ** (pre_db / 20.0)
    pre_f32 = f32(pre_f64)
    worst_err = 0.0
    for hz in freqs:
        w = 2.0 * math.pi * hz / 48000.0
        e1 = complex(math.cos(-w), math.sin(-w))
        e2 = e1 * e1
        h64 = pre_f64
        h32 = pre_f32
        for b0, b1, b2, a1, a2 in coeffs_f64:
            h64 *= (b0 + b1 * e1 + b2 * e2) / (1.0 + a1 * e1 + a2 * e2)
        for b0, b1, b2, a1, a2 in coeffs_f32:
            h32 *= (b0 + b1 * e1 + b2 * e2) / (1.0 + a1 * e1 + a2 * e2)
        err = abs(20.0 * math.log10(abs(h32)) - 20.0 * math.log10(abs(h64)))
        if err > worst_err:
            worst_err = err
    return worst_err

sample_freqs = (20.0, 60.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 3000.0, 4000.0, 6000.0, 8000.0, 12000.0, 20000.0)
max_err_db = check_eq_response_err(preamp_db, bands_f64, sample_freqs)
synth_bands_f64 = [
    rbj_biquad_f64('LSC', 100.0, -6.0, 0.7071),
    rbj_biquad_f64('PK', 1000.0, 3.0, 1.4142),
    rbj_biquad_f64('HSC', 10000.0, -3.0, 0.7071),
]
synth_err_db = check_eq_response_err(-3.0, synth_bands_f64, sample_freqs)
max_err_db = max(max_err_db, synth_err_db)
assert max_err_db < 0.01, f'EQ float32 response drifted {max_err_db:.5f} dB'

out = root / 'build-rx-dsp-logic'
out.mkdir(exist_ok=True)
try:
    (out / 'memory.c').write_text('''
#include <stddef.h>
void *memcpy(void *d,const void *s,size_t n){unsigned char *a=d;const unsigned char *b=s;while(n--)*a++=*b++;return d;}
void *memset(void *d,int c,size_t n){unsigned char *a=d;while(n--)*a++=c;return d;}
void *memmove(void *d,const void *s,size_t n){unsigned char *a=d;const unsigned char *b=s;if(a<b){for(size_t i=0;i<n;i++)a[i]=b[i];}else{while(n){--n;a[n]=b[n];}}return d;}
int memcmp(const void *a,const void *b,size_t n){const unsigned char *x=a,*y=b;while(n--){if(*x!=*y)return *x-*y;++x;++y;}return 0;}
''', encoding='utf-8')

    subprocess.run([
        str(deps / 'bin/arm-none-eabi-gcc.exe'),
        '-mcpu=cortex-m0', '-mthumb', '-Os', '-ffreestanding', '-fno-builtin', '-nostdlib',
        '-Wall', '-Wextra', '-Werror', '-DSELFTEST_STANDALONE',
        '-Wl,-Ttext=0x10000000,-Tbss=0x20000000,-Tdata=0x20040000,-e,pack_selftest',
        'firmware/rx-mixer/dsp_core.c',
        'firmware/rx-mixer/rx_usb.c',
        'firmware/rx-mixer/selftest_pack.c',
        'build-rx-dsp-logic/memory.c',
        '-lgcc', '-o', 'build-rx-dsp-logic/test.elf'
    ], cwd=root, check=True)

    uc_path = deps / 'boundary-test'
    if uc_path.exists():
        sys.path.insert(0, str(uc_path))

    try:
        from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_MODE_MCLASS
        from unicorn.arm_const import UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_R0
        has_unicorn = True
    except ImportError:
        has_unicorn = False

    if has_unicorn:
        elf = (out / 'test.elf').read_bytes()
        entry, phoff = struct.unpack_from('<II', elf, 24)
        phsize, phcount = struct.unpack_from('<HH', elf, 42)
        cpu = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
        cpu.mem_map(0x10000000, 0x80000)
        cpu.mem_map(0x20000000, 0x80000)
        for i in range(phcount):
            kind, offset, addr, _, size, _, _, _ = struct.unpack_from('<8I', elf, phoff + i * phsize)
            if kind == 1 and size:
                cpu.mem_write(addr, elf[offset:offset + size])
        cpu.reg_write(UC_ARM_REG_SP, 0x2007fff0)
        cpu.reg_write(UC_ARM_REG_LR, 0x1007fff1)
        cpu.emu_start(entry | 1, 0x1007fff0, timeout=30000000, count=100000000)
        assert cpu.reg_read(UC_ARM_REG_PC) == 0x1007fff0, 'Selftest did not return'
        line = cpu.reg_read(UC_ARM_REG_R0)
        assert line == 0, f'Production selftest failed at selftest_pack.c:{line}'
        engine = 'Unicorn 2.1.4 ARM'
    else:
        line = None
        engine = 'Skipped (Unicorn not installed); ARM freestanding compile passed'
finally:
    shutil.rmtree(out, ignore_errors=True)

print(json.dumps({
    'passed': True,
    'arm_emulation_status': 'passed' if has_unicorn else 'skipped',
    'selftest_failure_line': line,
    'eq_max_err_db': round(max_err_db, 6),
    'engine': engine,
    'excludes': 'real USB, PIO/DMA hardware, Flash erase/program and physical I2S DAC'
}))
