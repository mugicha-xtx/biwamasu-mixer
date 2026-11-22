// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

enum {
    DSP_EQ_BANDS = 11,
    DSP_RING_CAPACITY = 1024,
    DSP_RING_MASK = DSP_RING_CAPACITY - 1,
    DSP_SW_PRIME = 96,
    DSP_SW_TARGET = 96,
    DSP_SW_HARD_HIGH = 512,
    DSP_PC_PRIME = 144,
    DSP_PC_TARGET = 144,
    DSP_PC_HARD_HIGH = 576,
    DSP_I2S_BLOCK_FRAMES = 32,

    DSP_GAME_DB_MIN_X100 = -6000,
    DSP_GAME_DB_MAX_X100 = 2400,
    DSP_GAME_DB_DEFAULT_X100 = 0,
    DSP_MASTER_DB_MIN_X100 = -8000,
    DSP_MASTER_DB_MAX_X100 = 600,
    DSP_MASTER_DB_DEFAULT_X100 = -4500,
    DSP_PC_TRIM_DB_MIN_X100 = -2400,
    DSP_PC_TRIM_DB_MAX_X100 = 2400,
    DSP_PC_TRIM_DB_DEFAULT_X100 = 0,
    DSP_WIN_VOL_Q8_MIN = -15360,
    DSP_WIN_VOL_Q8_MAX = 0,
    DSP_MUTE_MASTER = 1,
    DSP_MUTE_GAME = 2,
    DSP_MUTE_PC = 4,
    DSP_MUTE_ALL_MASK = 7,
    DSP_RX_PIN_MODE_AUTO = 0,
    DSP_RX_PIN_MODE_GP1 = 1,
    DSP_RX_PIN_MODE_GP13 = 13,
    DSP_RX_PIN_MODE_DEFAULT = DSP_RX_PIN_MODE_GP1
};

#define DSP_EQ_PREAMP_MIN_LINEAR 0.01f
#define DSP_EQ_PREAMP_MAX_LINEAR 4.0f

static inline bool dsp_rx_pin_mode_valid(uint8_t mode) {
    return mode == DSP_RX_PIN_MODE_AUTO ||
           mode == DSP_RX_PIN_MODE_GP1 ||
           mode == DSP_RX_PIN_MODE_GP13;
}

typedef struct {
    float b0, b1, b2, a1, a2;
} dsp_biquad_coeff_t;

typedef struct {
    float z1_l, z2_l, z1_r, z2_r;
} dsp_biquad_state_t;

typedef struct {
    bool enabled;
    bool reset_pending;
    uint16_t band_reset_mask;
    float preamp_linear;
    float cur_preamp;
    float cur_mix;
    dsp_biquad_coeff_t coeffs[DSP_EQ_BANDS];
    dsp_biquad_state_t states[DSP_EQ_BANDS];
} dsp_eq_t;

typedef struct {
    int32_t game_db_x100;
    int32_t master_db_x100;
    int32_t pc_trim_db_x100;
    int32_t win_vol_db_x100[3]; // [0]=master, [1]=L, [2]=R in centi-dB
    uint8_t win_mute_mask;      // bit0=master, bit1=L, bit2=R
    uint8_t user_mute_mask;     // temporary (not persisted to Flash): bit0=master, bit1=Switch2, bit2=PC
    bool eq_enabled;
    uint8_t rx_pin_mode;        // 0=auto, 1=GP1, 13=GP13
} dsp_params_t;

typedef struct {
    // Switch UART audio path -> I2S
    int16_t sw_ring[DSP_RING_CAPACITY][2];
    uint32_t sw_write, sw_read;
    uint32_t sw_underflows, sw_overflows, sw_high, sw_low;
    bool sw_primed, sw_ended, sw_active;
    uint32_t sw_fade_in;
    float sw_tail_l, sw_tail_r;

    // PC USB speaker path -> resampled -> I2S
    int16_t pc_ring[DSP_RING_CAPACITY][2];
    uint32_t pc_write, pc_read;
    uint32_t pc_underflows, pc_overflows, pc_high, pc_low;
    bool pc_primed, pc_active;
    int16_t pc_hist[4][2];
    uint32_t pc_phase_q16;
    uint32_t pc_step_q16;
    float pc_err_lp;

    // Gains (linear + smoothed)
    dsp_params_t params;
    float target_game_gain;
    float target_pc_gain_l;
    float target_pc_gain_r;
    float target_master_gain;
    float cur_game_gain;
    float cur_pc_gain_l;
    float cur_pc_gain_r;
    float cur_master_gain;

    // EQ and tone test state
    dsp_eq_t eq;
    bool test_tone_enabled;
    uint32_t tone_phase_l;
    uint32_t tone_phase_r;
    uint32_t clip_count;
    uint32_t i2s_blocks;
} dsp_mixer_t;

// Critical-section hooks implemented by firmware (no-op in offline unit tests).
void dsp_lock_irq(uint32_t *state_out);
void dsp_unlock_irq(uint32_t state);

uint32_t dsp_crc32(const uint8_t *data, size_t len);
float dsp_db_x100_to_linear(int32_t db_x100);
bool dsp_float_sane(float x);
bool dsp_biquad_stable(const dsp_biquad_coeff_t *c);

void dsp_eq_init_flat(dsp_eq_t *eq, bool enabled);
void dsp_eq_reset_states(dsp_eq_t *eq);
void dsp_eq_set_preamp(dsp_eq_t *eq, float preamp_linear);
void dsp_eq_set_band(dsp_eq_t *eq, unsigned band, const dsp_biquad_coeff_t *coeff);
void dsp_eq_commit_profile(dsp_eq_t *eq, float preamp_linear, const dsp_biquad_coeff_t coeffs[DSP_EQ_BANDS]);
void dsp_eq_process_stereo(dsp_eq_t *eq, float *l_io, float *r_io);

void dsp_mixer_init(dsp_mixer_t *m);
void dsp_mixer_recompute_gains(dsp_mixer_t *m);
void dsp_mixer_set_params(dsp_mixer_t *m, const dsp_params_t *p);
void dsp_mixer_set_win_mute(dsp_mixer_t *m, uint8_t ch, bool muted);
void dsp_mixer_set_win_vol_q8(dsp_mixer_t *m, uint8_t ch, int16_t vol_q8);
void dsp_mixer_set_eq_enabled(dsp_mixer_t *m, bool enabled);
void dsp_mixer_set_test_tone(dsp_mixer_t *m, bool enabled);

uint32_t dsp_sw_level(const dsp_mixer_t *m);
uint32_t dsp_pc_level(const dsp_mixer_t *m);

void dsp_sw_flush(dsp_mixer_t *m);
void dsp_sw_mark_ended(dsp_mixer_t *m);
void dsp_sw_set_active(dsp_mixer_t *m, bool on);
void dsp_sw_push_s16le(dsp_mixer_t *m, const uint8_t *pcm, unsigned frames);

void dsp_pc_flush(dsp_mixer_t *m);
void dsp_pc_set_active(dsp_mixer_t *m, bool on);
void dsp_pc_push_s16le(dsp_mixer_t *m, const uint8_t *pcm, unsigned frames);

// Renders exactly `frames` (<= DSP_I2S_BLOCK_FRAMES) stereo 32-bit signed I2S words
// laid out as out_i2s[2*i] = Left, out_i2s[2*i + 1] = Right.
void dsp_render_i2s_block(dsp_mixer_t *m, int32_t *out_i2s, unsigned frames);
