// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "dsp_core.h"

typedef void (*flash_audio_pause_fn)(void);
typedef void (*flash_audio_resume_fn)(void);

bool flash_cfg_load(dsp_mixer_t *mixer);
bool flash_cfg_save(const dsp_mixer_t *mixer, flash_audio_pause_fn pause_audio, flash_audio_resume_fn resume_audio);
