// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#include "flash_cfg.h"
#include <string.h>
#include "pico/stdlib.h"
#include "hardware/flash.h"
#include "hardware/sync.h"
#include "hardware/regs/addressmap.h"

#define CFG_MAGIC 0x50413253u // "S2AP"
#define CFG_VERSION 1u
#define CFG_FLASH_OFFSET ((uint32_t)(PICO_FLASH_SIZE_BYTES - FLASH_SECTOR_SIZE))

typedef struct {
    uint32_t magic;
    uint32_t version;
    uint8_t eq_enabled;
    uint8_t rx_pin_mode;
    uint16_t reserved;
    int32_t game_db_x100;
    int32_t master_db_x100;
    int32_t pc_trim_db_x100;
    float preamp_linear;
    dsp_biquad_coeff_t coeffs[DSP_EQ_BANDS];
    uint32_t pad;
    uint32_t crc32;
} pack_flash_blob_t;

_Static_assert(sizeof(pack_flash_blob_t) == FLASH_PAGE_SIZE, "flash config blob must fit one 256-byte page");

static bool blob_valid(const pack_flash_blob_t *b) {
    if (b->magic != CFG_MAGIC || b->version != CFG_VERSION) return false;
    if (b->crc32 != dsp_crc32((const uint8_t *)b, sizeof(*b) - sizeof(uint32_t))) return false;
    if (b->eq_enabled > 1u) return false;
    if (b->rx_pin_mode != 0u && b->rx_pin_mode != 1u && b->rx_pin_mode != 13u) return false;
    if (b->game_db_x100 < -6000 || b->game_db_x100 > 1200) return false;
    if (b->master_db_x100 < -6000 || b->master_db_x100 > 600) return false;
    if (b->pc_trim_db_x100 < -2400 || b->pc_trim_db_x100 > 1200) return false;
    if (!dsp_float_sane(b->preamp_linear) || !(b->preamp_linear >= 0.01f && b->preamp_linear <= 4.0f)) return false;
    for (unsigned i = 0; i < DSP_EQ_BANDS; i++) {
        if (!dsp_biquad_stable(&b->coeffs[i])) return false;
    }
    return true;
}

bool flash_cfg_load(dsp_mixer_t *mixer) {
    const pack_flash_blob_t *stored = (const pack_flash_blob_t *)(XIP_BASE + CFG_FLASH_OFFSET);
    pack_flash_blob_t copy;
    memcpy(&copy, stored, sizeof(copy));
    if (!blob_valid(&copy)) return false;

    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    mixer->params.eq_enabled = (copy.eq_enabled != 0u);
    mixer->params.rx_pin_mode = copy.rx_pin_mode;
    mixer->params.game_db_x100 = copy.game_db_x100;
    mixer->params.master_db_x100 = copy.master_db_x100;
    mixer->params.pc_trim_db_x100 = copy.pc_trim_db_x100;
    mixer->eq.enabled = mixer->params.eq_enabled;
    mixer->eq.preamp_linear = copy.preamp_linear;
    mixer->eq.cur_preamp = copy.preamp_linear;
    mixer->eq.cur_mix = mixer->eq.enabled ? 1.0f : 0.0f;
    memcpy(mixer->eq.coeffs, copy.coeffs, sizeof(copy.coeffs));
    mixer->eq.reset_pending = true;
    mixer->eq.band_reset_mask = 0;
    dsp_unlock_irq(irq);

    dsp_mixer_recompute_gains(mixer);
    mixer->cur_game_gain = mixer->target_game_gain;
    mixer->cur_pc_gain_l = mixer->target_pc_gain_l;
    mixer->cur_pc_gain_r = mixer->target_pc_gain_r;
    mixer->cur_master_gain = mixer->target_master_gain;
    return true;
}

static void __no_inline_not_in_flash_func(commit_flash_page)(const uint8_t *page_buf) {
    uint32_t irq = save_and_disable_interrupts();
    flash_range_erase(CFG_FLASH_OFFSET, FLASH_SECTOR_SIZE);
    flash_range_program(CFG_FLASH_OFFSET, page_buf, FLASH_PAGE_SIZE);
    restore_interrupts(irq);
}

bool flash_cfg_save(const dsp_mixer_t *mixer, flash_audio_pause_fn pause_audio, flash_audio_resume_fn resume_audio) {
    pack_flash_blob_t blob;
    memset(&blob, 0, sizeof(blob));

    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    blob.magic = CFG_MAGIC;
    blob.version = CFG_VERSION;
    blob.eq_enabled = mixer->params.eq_enabled ? 1u : 0u;
    blob.rx_pin_mode = mixer->params.rx_pin_mode;
    blob.game_db_x100 = mixer->params.game_db_x100;
    blob.master_db_x100 = mixer->params.master_db_x100;
    blob.pc_trim_db_x100 = mixer->params.pc_trim_db_x100;
    blob.preamp_linear = mixer->eq.preamp_linear;
    memcpy(blob.coeffs, mixer->eq.coeffs, sizeof(blob.coeffs));
    dsp_unlock_irq(irq);

    blob.crc32 = dsp_crc32((const uint8_t *)&blob, sizeof(blob) - sizeof(uint32_t));
    if (!blob_valid(&blob)) return false;

    if (pause_audio) pause_audio();
    commit_flash_page((const uint8_t *)&blob);
    const pack_flash_blob_t *stored = (const pack_flash_blob_t *)(XIP_BASE + CFG_FLASH_OFFSET);
    pack_flash_blob_t readback;
    memcpy(&readback, stored, sizeof(readback));
    bool verified = (memcmp(&readback, &blob, sizeof(blob)) == 0) && blob_valid(&readback);
    if (resume_audio) resume_audio();
    return verified;
}
