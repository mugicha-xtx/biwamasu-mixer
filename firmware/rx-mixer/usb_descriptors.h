// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#pragma once
#include "usb_ids.h"

enum {
    ITF_NUM_AUDIO_CONTROL = 0,
    ITF_NUM_AUDIO_STREAMING_SPK = 1,
    ITF_NUM_AUDIO_STREAMING_MIC = 2,
    ITF_NUM_HID = 3,
    ITF_NUM_TOTAL = 4
};

#define UAC2_ENTITY_SPK_CLOCK           0x04
#define UAC2_ENTITY_SPK_INPUT_TERMINAL  0x01
#define UAC2_ENTITY_SPK_FEATURE_UNIT    0x02
#define UAC2_ENTITY_SPK_OUTPUT_TERMINAL 0x03

#define UAC2_ENTITY_MIC_CLOCK           0x14
#define UAC2_ENTITY_MIC_INPUT_TERMINAL  0x11
#define UAC2_ENTITY_MIC_OUTPUT_TERMINAL 0x12

#define PACK_VID ((uint16_t)USB_VID)
#define PACK_PID ((uint16_t)MIXER_USB_PID)

#define CONFIG_TOTAL_LEN 262
