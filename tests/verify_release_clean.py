# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
"""Audit public-release/ for privacy hygiene, SPDX/license completeness, and clean git state."""
import json
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[1]

required_files = [
    '.gitignore',
    'LICENSE',
    'README.md',
    'THIRD_PARTY_NOTICES.md',
    'include/usb_ids.h',
    'licenses/pico-sdk-BSD-3-Clause.txt',
    'licenses/tinyusb-MIT.txt',
    'licenses/cmsis-Apache-2.0.txt',
    'licenses/pico-printf-MIT.txt',
    'presets/sample-peace.txt',
    'docs/GUIDE_JA.md',
    'docs/USB_PID_APPLICATION.md',
    'tools/downloads.sha256.json',
]
for rel in required_files:
    assert (root / rel).is_file(), f'Missing required release file: {rel}'

license_txt = (root / 'LICENSE').read_text(encoding='utf-8')
assert 'Copyright (c) 2026 mugicha_xtx' in license_txt, 'LICENSE copyright mismatch'

usb_ids_txt = (root / 'include/usb_ids.h').read_text(encoding='utf-8')
for symbol in ('USB_VID         0x0000u', 'BRIDGE_USB_PID  0x0000u', 'MIXER_USB_PID   0x0000u'):
    assert symbol in usb_ids_txt, f'include/usb_ids.h must keep 0x0000u default before official allocation ({symbol})'

forbidden_binaries = {'.uf2', '.elf', '.bin', '.map', '.wav', '.png', '.jpg'}
forbidden_patterns = [
    ('win_users_path', re.compile(r'[a-z]:[\\/]+users[\\/]', re.I)),
    ('unix_home_path', re.compile(r'(?:^|\s)/(?:Users|home)/[A-Za-z0-9._-]+')),
    ('win_endpoint_id', re.compile(r'\{0\.0\.[01]\.00000000\}', re.I)),
    ('guid', re.compile(r'\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}', re.I)),
    ('hostname', re.compile(r'DESKTOP-[A-Z0-9]+|LAPTOP-[A-Z0-9]+', re.I)),
]
email_rx = re.compile(r'[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}')

spdx_exts = {'.c', '.h', '.pio', '.ps1', '.py'}
checked_files = 0
spdx_files = 0

for p in sorted(root.rglob('*')):
    parts = set(p.parts)
    if parts & {'.deps', '.git', '__pycache__'}:
        continue
    rel = p.relative_to(root).as_posix()
    if p.is_dir():
        assert not rel.startswith('build') and rel != 'dist', f'Stray build/dist dir left in public-release: {rel}'
        continue

    checked_files += 1
    assert p.suffix.lower() not in forbidden_binaries, f'Binary artifact must not sit in source repo before PID allocation: {rel}'

    if p.resolve() != pathlib.Path(__file__).resolve():
        txt = p.read_text(encoding='utf-8')
        for name, rx in forbidden_patterns:
            assert not rx.search(txt), f'Forbidden pattern ({name}) found in {rel}'

        for m in email_rx.finditer(txt):
            addr = m.group(0)
            assert addr.endswith('@users.noreply.github.com'), f'Unexpected email in {rel}: {addr}'
    else:
        txt = p.read_text(encoding='utf-8')

    if p.suffix.lower() in spdx_exts or p.name == 'CMakeLists.txt':
        head = txt[:240]
        assert 'SPDX-License-Identifier: MIT' in head, f'Missing SPDX header in {rel}'
        assert 'Copyright (c) 2026 mugicha_xtx' in head, f'Missing copyright header in {rel}'
        spdx_files += 1

print(json.dumps({
    'clean': True,
    'checked_files': checked_files,
    'spdx_verified_files': spdx_files,
    'copyright': 'mugicha_xtx',
    'usb_ids_placeholder_intact': True
}))
