// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
// Uses TinyUSB's UAC1 descriptor macros; see THIRD_PARTY_NOTICES.md.
#include <string.h>
#include "tusb.h"
#include "pico/unique_id.h"
#include "usb_ids.h"

const tusb_desc_device_t probe_device_descriptor = {
    .bLength = sizeof(tusb_desc_device_t), .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200, .bDeviceClass = 0, .bDeviceSubClass = 0, .bDeviceProtocol = 0,
    .bMaxPacketSize0 = 64, .idVendor = (uint16_t)USB_VID,
    .idProduct = (uint16_t)BRIDGE_USB_PID, .bcdDevice = 0x0090,
    .iManufacturer = 1, .iProduct = 2, .iSerialNumber = 3, .bNumConfigurations = 1
};

enum { AC_LENGTH = 9 + 12 + 9, AUDIO_LENGTH = 9 + AC_LENGTH + 9 + 9 + 7 + 11 + 9 + 7,
       CONFIG_LENGTH = 9 + AUDIO_LENGTH + (PROBE_HID ? TUD_HID_DESC_LEN : 0) };

#if PROBE_HID
static const uint8_t hid_report[] = {
    0x06, 0x00, 0xff, 0x09, 0x01, 0xa1, 0x01,
    0x15, 0x00, 0x26, 0xff, 0x00, 0x75, 0x08, 0x95, 63,
    0x09, 0x01, 0xb1, 0x02, // 63-byte Feature (report ID 0)
    0x09, 0x02, 0x81, 0x02, // 63-byte Input (enables EP 0x82 IN)
    0x09, 0x03, 0x91, 0x02, // 63-byte Output
    0xc0
};
const uint8_t *tud_hid_descriptor_report_cb(uint8_t instance) { (void)instance; return hid_report; }
#endif

const uint8_t probe_configuration_descriptor[] = {
    TUD_CONFIG_DESCRIPTOR(1, PROBE_HID ? 3 : 2, 0, CONFIG_LENGTH, 0, 100),
    TUD_AUDIO10_DESC_STD_AC(0, 0, 4),
    TUD_AUDIO10_DESC_CS_AC(0x0100, 12 + 9, 1),
    TUD_AUDIO10_DESC_INPUT_TERM(1, AUDIO_TERM_TYPE_USB_STREAMING, 0, 2, 3, 0, 0),
    TUD_AUDIO10_DESC_OUTPUT_TERM(2, AUDIO_TERM_TYPE_OUT_HEADPHONES, 0, 1, 0),
    TUD_AUDIO10_DESC_STD_AS_INT(1, 0, 0, 0),
    TUD_AUDIO10_DESC_STD_AS_INT(1, 1, 1, 0),
    TUD_AUDIO10_DESC_CS_AS_INT(1, 0, AUDIO10_DATA_FORMAT_TYPE_I_PCM),
    TUD_AUDIO10_DESC_TYPE_I_FORMAT(2, 2, 16, 48000),
    // Synchronous to USB SOF: Switch 2 / host pushes 48 kHz frames into 3 Mbps UART immediately.
    TUD_AUDIO10_DESC_STD_AS_ISO_EP(0x01, TUSB_XFER_ISOCHRONOUS | TUSB_ISO_EP_ATT_SYNCHRONOUS, 192, 1, 0),
    TUD_AUDIO10_DESC_CS_AS_ISO_EP(AUDIO10_CS_AS_ISO_DATA_EP_ATT_SAMPLING_FRQ, 0, 0),
#if PROBE_HID
    TUD_HID_DESCRIPTOR(2, 0, HID_ITF_PROTOCOL_NONE, sizeof(hid_report), 0x82, 64, 10),
#endif
};
_Static_assert(sizeof(probe_configuration_descriptor) == CONFIG_LENGTH, "Descriptor length");
const uint8_t *tud_descriptor_device_cb(void) { return (const uint8_t *)&probe_device_descriptor; }
const uint8_t *tud_descriptor_configuration_cb(uint8_t index) {
    return index == 0 ? probe_configuration_descriptor : NULL;
}
const uint16_t *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void)langid;
    static uint16_t out[40];
    static char serial[PICO_UNIQUE_BOARD_ID_SIZE_BYTES * 2 + 1];
    const char *strings[] = {NULL, "mugicha_xtx", "Biwamasu Mixer TX", serial, "Biwamasu Mixer TX"};
    if (!index) { out[0] = 0x0304; out[1] = 0x0409; return out; }
    if (index >= TU_ARRAY_SIZE(strings)) return NULL;
    if (index == 3) pico_get_unique_board_id_string(serial, sizeof(serial));
    size_t n = strlen(strings[index]);
    if (n > 39) n = 39;
    out[0] = (uint16_t)(0x0300 | (2 + n * 2));
    for (size_t i = 0; i < n; ++i) out[i + 1] = (uint8_t)strings[index][i];
    return out;
}
