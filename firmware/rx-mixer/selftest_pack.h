// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#pragma once
#include <stdint.h>
#include "dsp_core.h"
#include "rx_usb.h"

// Runs deterministic DSP, EQ, CRC, resampler, and UART/USB packet tests.
// Returns 0 on success or the failing line number.
uint32_t pack_selftest_run(dsp_mixer_t *m, rx_usb_audio_t *mic);
