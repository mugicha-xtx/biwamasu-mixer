// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
// Uses TinyUSB UAC2 / HID descriptor macros; see THIRD_PARTY_NOTICES.md.
#include <string.h>
#include "tusb.h"
#include "pico/unique_id.h"
#include "usb_descriptors.h"

const tusb_desc_device_t pack_device_descriptor = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = TUSB_CLASS_MISC,
    .bDeviceSubClass = MISC_SUBCLASS_COMMON,
    .bDeviceProtocol = MISC_PROTOCOL_IAD,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = PACK_VID,
    .idProduct = PACK_PID,
    .bcdDevice = 0x0101,
    .iManufacturer = 1,
    .iProduct = 2,
    .iSerialNumber = 3,
    .bNumConfigurations = 1
};

static const uint8_t hid_report_desc[] = {
    0x06, 0x00, 0xff, // Usage Page (Vendor Defined 0xFF00)
    0x09, 0x01,       // Usage (0x01)
    0xa1, 0x01,       // Collection (Application)
    0x15, 0x00,       //   Logical Minimum (0)
    0x26, 0xff, 0x00, //   Logical Maximum (255)
    0x75, 0x08,       //   Report Size (8)
    0x95, 63,         //   Report Count (63)
    0x09, 0x01,       //   Usage (0x01)
    0xb1, 0x02,       //   Feature (Data,Var,Abs)
    0xc0              // End Collection
};

#define CS_AC_INNER_LEN ( \
    TUD_AUDIO20_DESC_CLK_SRC_LEN + \
    TUD_AUDIO20_DESC_INPUT_TERM_LEN + \
    TUD_AUDIO20_DESC_FEATURE_UNIT_LEN(2) + \
    TUD_AUDIO20_DESC_OUTPUT_TERM_LEN + \
    TUD_AUDIO20_DESC_CLK_SRC_LEN + \
    TUD_AUDIO20_DESC_INPUT_TERM_LEN + \
    TUD_AUDIO20_DESC_FEATURE_UNIT_LEN(2) + \
    TUD_AUDIO20_DESC_OUTPUT_TERM_LEN)

_Static_assert(CS_AC_INNER_LEN == 110, "CS_AC entity byte count");

#define FU_CTRL_MUTE_VOL ( \
    (AUDIO20_CTRL_RW << AUDIO20_FEATURE_UNIT_CTRL_MUTE_POS) | \
    (AUDIO20_CTRL_RW << AUDIO20_FEATURE_UNIT_CTRL_VOLUME_POS))

const uint8_t pack_configuration_descriptor[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN, 0, 100),
    TUD_AUDIO20_DESC_IAD(ITF_NUM_AUDIO_CONTROL, 3, 2),

    // Interface 0: Audio Control
    TUD_AUDIO20_DESC_STD_AC(ITF_NUM_AUDIO_CONTROL, 0, 2),
    TUD_AUDIO20_DESC_CS_AC(0x0200, AUDIO20_FUNC_HEADSET, CS_AC_INNER_LEN, 0),

    // Speaker path (PC Mix -> Pico I2S -> Pimoroni Pico Audio Pack)
    TUD_AUDIO20_DESC_CLK_SRC(UAC2_ENTITY_SPK_CLOCK, AUDIO20_CLOCK_SOURCE_ATT_INT_FIX_CLK, 5, 0, 0),
    TUD_AUDIO20_DESC_INPUT_TERM(UAC2_ENTITY_SPK_INPUT_TERMINAL, AUDIO_TERM_TYPE_USB_STREAMING, 0,
        UAC2_ENTITY_SPK_CLOCK, 2, 0x03, 0, 0, 4),
    TUD_AUDIO20_DESC_FEATURE_UNIT(UAC2_ENTITY_SPK_FEATURE_UNIT, UAC2_ENTITY_SPK_INPUT_TERMINAL, 0,
        FU_CTRL_MUTE_VOL, FU_CTRL_MUTE_VOL, FU_CTRL_MUTE_VOL),
    TUD_AUDIO20_DESC_OUTPUT_TERM(UAC2_ENTITY_SPK_OUTPUT_TERMINAL, AUDIO_TERM_TYPE_OUT_HEADPHONES, 0,
        UAC2_ENTITY_SPK_FEATURE_UNIT, UAC2_ENTITY_SPK_CLOCK, 0, 4),

    // Mic path (Switch Raw PCM -> OBS Capture)
    TUD_AUDIO20_DESC_CLK_SRC(UAC2_ENTITY_MIC_CLOCK, AUDIO20_CLOCK_SOURCE_ATT_EXT_CLK, 5, 0, 0),
    TUD_AUDIO20_DESC_INPUT_TERM(UAC2_ENTITY_MIC_INPUT_TERMINAL, AUDIO_TERM_TYPE_IN_GENERIC_MIC, 0,
        UAC2_ENTITY_MIC_CLOCK, 2, 0x03, 0, 0, 5),
    TUD_AUDIO20_DESC_FEATURE_UNIT(UAC2_ENTITY_MIC_FEATURE_UNIT, UAC2_ENTITY_MIC_INPUT_TERMINAL, 0,
        FU_CTRL_MUTE_VOL, FU_CTRL_MUTE_VOL, FU_CTRL_MUTE_VOL),
    TUD_AUDIO20_DESC_OUTPUT_TERM(UAC2_ENTITY_MIC_OUTPUT_TERMINAL, AUDIO_TERM_TYPE_USB_STREAMING, 0,
        UAC2_ENTITY_MIC_FEATURE_UNIT, UAC2_ENTITY_MIC_CLOCK, 0, 5),

    // Interface 1: Speaker Audio Streaming (EP 0x01 OUT Adaptive 196B)
    TUD_AUDIO20_DESC_STD_AS_INT(ITF_NUM_AUDIO_STREAMING_SPK, 0, 0, 4),
    TUD_AUDIO20_DESC_STD_AS_INT(ITF_NUM_AUDIO_STREAMING_SPK, 1, 1, 4),
    TUD_AUDIO20_DESC_CS_AS_INT(UAC2_ENTITY_SPK_INPUT_TERMINAL, 0, AUDIO20_FORMAT_TYPE_I,
        AUDIO20_DATA_FORMAT_TYPE_I_PCM, 2, 0x03, 0),
    TUD_AUDIO20_DESC_TYPE_I_FORMAT(2, 16),
    TUD_AUDIO20_DESC_STD_AS_ISO_EP(0x01, TUSB_XFER_ISOCHRONOUS | TUSB_ISO_EP_ATT_ADAPTIVE | TUSB_ISO_EP_ATT_DATA, 196, 1),
    TUD_AUDIO20_DESC_CS_AS_ISO_EP(0, 0, 0, 0),

    // Interface 2: Mic Audio Streaming (EP 0x81 IN Asynchronous 196B)
    TUD_AUDIO20_DESC_STD_AS_INT(ITF_NUM_AUDIO_STREAMING_MIC, 0, 0, 5),
    TUD_AUDIO20_DESC_STD_AS_INT(ITF_NUM_AUDIO_STREAMING_MIC, 1, 1, 5),
    TUD_AUDIO20_DESC_CS_AS_INT(UAC2_ENTITY_MIC_OUTPUT_TERMINAL, 0, AUDIO20_FORMAT_TYPE_I,
        AUDIO20_DATA_FORMAT_TYPE_I_PCM, 2, 0x03, 0),
    TUD_AUDIO20_DESC_TYPE_I_FORMAT(2, 16),
    TUD_AUDIO20_DESC_STD_AS_ISO_EP(0x81, TUSB_XFER_ISOCHRONOUS | TUSB_ISO_EP_ATT_ASYNCHRONOUS | TUSB_ISO_EP_ATT_DATA, 196, 1),
    TUD_AUDIO20_DESC_CS_AS_ISO_EP(0, 0, 0, 0),

    // Interface 3: Vendor HID Control & Telemetry
    TUD_HID_DESCRIPTOR(ITF_NUM_HID, 6, HID_ITF_PROTOCOL_NONE, sizeof(hid_report_desc), 0x82, 64, 10)
};

_Static_assert(sizeof(pack_configuration_descriptor) == CONFIG_TOTAL_LEN, "configuration length must be 280");

const uint8_t *tud_descriptor_device_cb(void) {
    return (const uint8_t *)&pack_device_descriptor;
}

const uint8_t *tud_descriptor_configuration_cb(uint8_t index) {
    return index == 0 ? pack_configuration_descriptor : NULL;
}

const uint8_t *tud_hid_descriptor_report_cb(uint8_t instance) {
    (void)instance;
    return hid_report_desc;
}

const uint16_t *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void)langid;
    static uint16_t out[40];
    static char serial[2 * PICO_UNIQUE_BOARD_ID_SIZE_BYTES + 1];
    const char *strings[] = {
        NULL,
        "Personal Audio Lab",
        "Switch2 Pico Audio Pack Bridge",
        serial,
        "Switch2 PC Mix Output",
        "Switch2 Raw Capture",
        "Switch2 Audio Pack HID"
    };
    if (!index) {
        out[0] = 0x0304;
        out[1] = 0x0409;
        return out;
    }
    if (index >= TU_ARRAY_SIZE(strings)) return NULL;
    if (index == 3) pico_get_unique_board_id_string(serial, sizeof(serial));
    size_t n = strlen(strings[index]);
    if (n > 39) n = 39;
    out[0] = (uint16_t)(0x0300 | ((n + 1u) * 2u));
    for (size_t i = 0; i < n; i++) out[i + 1u] = (uint8_t)strings[index][i];
    return out;
}
