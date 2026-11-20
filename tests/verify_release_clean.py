# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
"""Audit public-release/ for privacy hygiene, stripped image metadata, SPDX/license completeness, and clean git state."""
import json
import pathlib
import re
import struct

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
    'licenses/fluentui-emoji-MIT.txt',
    'licenses/newlib-COPYING.txt',
    'presets/sample-peace.txt',
    'host-gui/BiwamasuMixerGui.cs',
    'scripts/Build-Gui.ps1',
    'scripts/Control-BiwamasuMixer.ps1',
    'images/overview.jpg',
    'images/wiring-1-placement.jpg',
    'images/wiring-2-uart-link.jpg',
    'images/wiring-3-audio-pack.jpg',
    'docs/USB_PID_APPLICATION.md',
    'tools/downloads.sha256.json',
]


def assert_image_metadata_free(path: pathlib.Path, rel: str) -> None:
    """Ensure images under images/ carry no Exif, XMP, ICC, IPTC, or encoder comment metadata."""
    data = path.read_bytes()
    suffix = path.suffix.lower()
    if suffix == '.jpg':
        assert data.startswith(b'\xff\xd8'), f'Invalid JPEG header in {rel}'
        pos = 2
        saw_sos = False
        while pos < len(data):
            assert data[pos] == 0xff, f'Unexpected non-marker byte at offset {pos} in {rel}'
            while pos < len(data) and data[pos] == 0xff:
                pos += 1
            assert pos < len(data), f'Truncated JPEG marker in {rel}'
            marker = data[pos]
            pos += 1
            if marker == 0xda:  # SOS: walk entropy-coded scan stream cleanly until EOI
                saw_sos = True
                assert pos + 2 <= len(data), f'Truncated SOS header in {rel}'
                sos_len = struct.unpack('>H', data[pos:pos + 2])[0]
                pos += sos_len
                while pos < len(data):
                    if data[pos] == 0xff:
                        assert pos + 1 < len(data), f'Truncated marker in JPEG scan data: {rel}'
                        nxt = data[pos + 1]
                        if nxt == 0x00 or (0xd0 <= nxt <= 0xd7):
                            pos += 2
                            continue
                        if nxt == 0xff:
                            pos += 1
                            continue
                        if nxt == 0xd9:
                            pos += 2
                            assert pos == len(data), f'Trailing bytes after JPEG EOI in {rel}'
                            return
                        raise AssertionError(f'Unexpected marker 0xFF{nxt:02X} inside scan data in {rel}')
                    pos += 1
                raise AssertionError(f'Missing JPEG EOI in {rel}')
            elif marker == 0x00 or (0xd0 <= marker <= 0xd7):
                continue
            elif marker == 0xd9:
                assert pos == len(data), f'Trailing bytes after JPEG EOI in {rel}'
                return
            else:
                assert pos + 2 <= len(data), f'Truncated segment header in {rel}'
                seg_len = struct.unpack('>H', data[pos:pos + 2])[0]
                assert seg_len >= 2 and pos + seg_len <= len(data), f'Invalid segment length in {rel}'
                assert not (0xe1 <= marker <= 0xef), f'Forbidden JPEG metadata marker 0xFF{marker:02X} found in {rel}'
                assert marker != 0xfe, f'Forbidden JPEG comment (COM) marker found in {rel}'
                if marker == 0xe0:
                    payload = data[pos + 2:pos + seg_len]
                    assert payload[:5] == b'JFIF\x00' and len(payload) == 14, f'Unexpected APP0 payload in {rel}'
                pos += seg_len
        assert saw_sos, f'Missing SOS in JPEG {rel}'
    elif suffix == '.png':
        assert data.startswith(b'\x89PNG\r\n\x1a\n'), f'Invalid PNG signature in {rel}'
        allowed_chunks = {b'IHDR', b'PLTE', b'tRNS', b'IDAT', b'IEND'}
        pos = 8
        saw_iend = False
        while pos < len(data):
            assert pos + 12 <= len(data), f'Truncated PNG chunk in {rel}'
            clen = struct.unpack('>I', data[pos:pos + 4])[0]
            ctype = data[pos + 4:pos + 8]
            assert ctype in allowed_chunks, f'Forbidden PNG metadata/ancillary chunk {ctype!r} in {rel}'
            pos += 12 + clen
            assert pos <= len(data), f'PNG chunk exceeds EOF in {rel}'
            if ctype == b'IEND':
                saw_iend = True
                assert pos == len(data), f'Trailing bytes after PNG IEND in {rel}'
        assert saw_iend, f'Missing PNG IEND in {rel}'
    else:
        raise AssertionError(f'Unsupported image format in {rel}')


if __name__ == '__main__':
    for rel in required_files:
        assert (root / rel).is_file(), f'Missing required release file: {rel}'

    license_txt = (root / 'LICENSE').read_text(encoding='utf-8')
    assert 'Copyright (c) 2026 mugicha_xtx' in license_txt, 'LICENSE copyright mismatch'

    usb_ids_txt = (root / 'include/usb_ids.h').read_text(encoding='utf-8')
    for symbol in ('USB_VID         0x0000u', 'BRIDGE_USB_PID  0x0000u', 'MIXER_USB_PID   0x0000u'):
        assert symbol in usb_ids_txt, f'include/usb_ids.h must keep 0x0000u default before official allocation ({symbol})'

    forbidden_binaries = {'.uf2', '.elf', '.bin', '.map', '.wav', '.zip', '.exe', '.dll', '.pdb'}
    image_exts = {'.jpg', '.jpeg', '.png', '.webp', '.gif', '.bmp', '.tiff', '.heic'}
    forbidden_patterns = [
        ('win_users_path', re.compile(r'[a-z]:[\\/]+users[\\/]', re.I)),
        ('unix_home_path', re.compile(r'(?:^|\s)/(?:Users|home)/[A-Za-z0-9._-]+')),
        ('win_endpoint_id', re.compile(r'\{0\.0\.[01]\.00000000\}', re.I)),
        ('guid', re.compile(r'\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}', re.I)),
        ('hostname', re.compile(r'DESKTOP-[A-Z0-9]+|LAPTOP-[A-Z0-9]+', re.I)),
    ]
    email_rx = re.compile(r'[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}')

    spdx_exts = {'.c', '.h', '.pio', '.ps1', '.py', '.cs'}
    checked_files = 0
    spdx_files = 0
    clean_images = 0

    for p in sorted(root.rglob('*')):
        parts = set(p.parts)
        if parts & {'.deps', '.git', '__pycache__', 'raw_photos'}:
            continue
        rel = p.relative_to(root).as_posix()
        if p.is_dir():
            assert not rel.startswith('build') and rel != 'dist', f'Stray build/dist dir left in public-release: {rel}'
            continue

        checked_files += 1
        ext = p.suffix.lower()
        assert ext not in forbidden_binaries, f'Binary artifact must not sit in source repo before PID allocation: {rel}'

        if ext in image_exts:
            assert rel.startswith('images/') and ext in {'.jpg', '.png'}, f'Image outside images/ or disallowed extension: {rel}'
            assert_image_metadata_free(p, rel)
            clean_images += 1
            continue

        if p.resolve() != pathlib.Path(__file__).resolve():
            txt = p.read_text(encoding='utf-8')
            for name, rx in forbidden_patterns:
                assert not rx.search(txt), f'Forbidden pattern ({name}) found in {rel}'

            for m in email_rx.finditer(txt):
                addr = m.group(0)
                assert addr.endswith('@users.noreply.github.com'), f'Unexpected email in {rel}: {addr}'
        else:
            txt = p.read_text(encoding='utf-8')

        if ext in spdx_exts or p.name == 'CMakeLists.txt':
            head = txt[:240]
            assert 'SPDX-License-Identifier: MIT' in head, f'Missing SPDX header in {rel}'
            assert 'Copyright (c) 2026 mugicha_xtx' in head, f'Missing copyright header in {rel}'
            spdx_files += 1

    print(json.dumps({
        'clean': True,
        'checked_files': checked_files,
        'clean_images_verified': clean_images,
        'spdx_verified_files': spdx_files,
        'copyright': 'mugicha_xtx',
        'usb_ids_placeholder_intact': True
    }))
