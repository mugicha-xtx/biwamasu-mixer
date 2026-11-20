// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#include "dsp_core.h"
#include <string.h>

static uint32_t crc32_table[256];
static bool crc32_ready = false;

static void ensure_crc32_table(void) {
    if (crc32_ready) return;
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t c = i;
        for (unsigned k = 0; k < 8; k++) {
            c = (c & 1u) ? ((c >> 1) ^ 0xedb88320u) : (c >> 1);
        }
        crc32_table[i] = c;
    }
    crc32_ready = true;
}

uint32_t dsp_crc32(const uint8_t *data, size_t len) {
    ensure_crc32_table();
    uint32_t c = 0xffffffffu;
    for (size_t i = 0; i < len; i++) {
        c = crc32_table[(c ^ data[i]) & 0xffu] ^ (c >> 8);
    }
    return ~c;
}

float dsp_db_x100_to_linear(int32_t db_x100) {
    if (db_x100 <= -6000) return 0.0f;
    if (db_x100 == 0) return 1.0f;
    if (db_x100 > 1200) db_x100 = 1200;

    // 10^(db_x100 / 2000) = 2^(db_x100 * log2(10) / 2000)
    // Decompose into integer n + fraction f in [0, 1), compute 2^f via exp(f * ln(2)) Taylor series.
    const double x = (double)db_x100 * 0.00166096404744368117;
    int32_t n = (int32_t)x;
    if ((double)n > x) n--;
    const double f = x - (double)n;
    const double u = f * 0.69314718055994530942;

    double poly = 1.0 + u / 7.0;
    poly = 1.0 + u * poly / 6.0;
    poly = 1.0 + u * poly / 5.0;
    poly = 1.0 + u * poly / 4.0;
    poly = 1.0 + u * poly / 3.0;
    poly = 1.0 + u * poly / 2.0;
    poly = 1.0 + u * poly;

    double pow2 = (n >= 0) ? (double)(1u << (unsigned)n) : (1.0 / (double)(1u << (unsigned)(-n)));
    return (float)(poly * pow2);
}

static inline float dsp_flush_tiny(float x) {
    return (x > -1.0e-20f && x < 1.0e-20f) ? 0.0f : x;
}

bool dsp_float_sane(float x) {
    return (x == x) && (x > -20.0f && x < 20.0f);
}

bool dsp_biquad_stable(const dsp_biquad_coeff_t *c) {
    if (!dsp_float_sane(c->b0) || !dsp_float_sane(c->b1) || !dsp_float_sane(c->b2) ||
        !dsp_float_sane(c->a1) || !dsp_float_sane(c->a2)) return false;
    if (!(c->a2 > -1.0f && c->a2 < 1.0f)) return false;
    float abs_a1 = c->a1 < 0.0f ? -c->a1 : c->a1;
    return (1.0f + c->a2) > abs_a1;
}

static bool coeff_equal(const dsp_biquad_coeff_t *a, const dsp_biquad_coeff_t *b) {
    return a->b0 == b->b0 && a->b1 == b->b1 && a->b2 == b->b2 &&
           a->a1 == b->a1 && a->a2 == b->a2;
}

static inline int16_t read_s16le(const uint8_t *p) {
    return (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

void dsp_eq_reset_states(dsp_eq_t *eq) {
    memset(eq->states, 0, sizeof(eq->states));
}

static void dsp_eq_apply_pending_resets(dsp_eq_t *eq) {
    if (eq->reset_pending) {
        dsp_eq_reset_states(eq);
        eq->reset_pending = false;
        eq->band_reset_mask = 0;
    } else if (eq->band_reset_mask != 0u) {
        uint16_t mask = eq->band_reset_mask;
        eq->band_reset_mask = 0;
        for (unsigned b = 0; b < DSP_EQ_BANDS; b++) {
            if (mask & (uint16_t)(1u << b)) {
                memset(&eq->states[b], 0, sizeof(eq->states[b]));
            }
        }
    }
}

static inline void dsp_eq_run_biquads(dsp_eq_t *eq, float preamp, float *l_io, float *r_io) {
    float xl = *l_io * preamp;
    float xr = *r_io * preamp;
    for (unsigned b = 0; b < DSP_EQ_BANDS; b++) {
        const dsp_biquad_coeff_t *c = &eq->coeffs[b];
        dsp_biquad_state_t *st = &eq->states[b];

        float yl = c->b0 * xl + st->z1_l;
        st->z1_l = dsp_flush_tiny(c->b1 * xl - c->a1 * yl + st->z2_l);
        st->z2_l = dsp_flush_tiny(c->b2 * xl - c->a2 * yl);
        xl = yl;

        float yr = c->b0 * xr + st->z1_r;
        st->z1_r = dsp_flush_tiny(c->b1 * xr - c->a1 * yr + st->z2_r);
        st->z2_r = dsp_flush_tiny(c->b2 * xr - c->a2 * yr);
        xr = yr;
    }
    *l_io = xl;
    *r_io = xr;
}

void dsp_eq_init_flat(dsp_eq_t *eq, bool enabled) {
    eq->enabled = enabled;
    eq->reset_pending = false;
    eq->band_reset_mask = 0;
    eq->preamp_linear = 1.0f;
    eq->cur_preamp = 1.0f;
    eq->cur_mix = enabled ? 1.0f : 0.0f;
    for (unsigned b = 0; b < DSP_EQ_BANDS; b++) {
        eq->coeffs[b] = (dsp_biquad_coeff_t){ 1.0f, 0.0f, 0.0f, 0.0f, 0.0f };
    }
    dsp_eq_reset_states(eq);
}

void dsp_eq_set_preamp(dsp_eq_t *eq, float preamp_linear) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    eq->preamp_linear = preamp_linear;
    dsp_unlock_irq(irq);
}

void dsp_eq_set_band(dsp_eq_t *eq, unsigned band, const dsp_biquad_coeff_t *coeff) {
    if (band >= DSP_EQ_BANDS) return;
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    if (!coeff_equal(&eq->coeffs[band], coeff)) {
        eq->coeffs[band] = *coeff;
        eq->band_reset_mask |= (uint16_t)(1u << band);
    }
    dsp_unlock_irq(irq);
}

void dsp_eq_commit_profile(dsp_eq_t *eq, float preamp_linear, const dsp_biquad_coeff_t coeffs[DSP_EQ_BANDS]) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    eq->preamp_linear = preamp_linear;
    for (unsigned b = 0; b < DSP_EQ_BANDS; b++) {
        if (!coeff_equal(&eq->coeffs[b], &coeffs[b])) {
            eq->coeffs[b] = coeffs[b];
            eq->band_reset_mask |= (uint16_t)(1u << b);
        }
    }
    dsp_unlock_irq(irq);
}

void dsp_eq_process_stereo(dsp_eq_t *eq, float *l_io, float *r_io) {
    dsp_eq_apply_pending_resets(eq);
    if (!eq->enabled) return;
    dsp_eq_run_biquads(eq, eq->preamp_linear, l_io, r_io);
}

static void recompute_gains_unlocked(dsp_mixer_t *m) {
    m->target_game_gain = dsp_db_x100_to_linear(m->params.game_db_x100);
    m->target_master_gain = dsp_db_x100_to_linear(m->params.master_db_x100);
    for (unsigned ch = 0; ch < 2; ch++) {
        bool muted = ((m->params.win_mute_mask & 1u) != 0u) ||
                     ((m->params.win_mute_mask & (1u << (ch + 1u))) != 0u);
        int32_t db = m->params.win_vol_db_x100[0] + m->params.win_vol_db_x100[ch + 1u] + m->params.pc_trim_db_x100;
        float g = muted ? 0.0f : dsp_db_x100_to_linear(db);
        if (ch == 0) m->target_pc_gain_l = g;
        else m->target_pc_gain_r = g;
    }
}

void dsp_mixer_init(dsp_mixer_t *m) {
    memset(m, 0, sizeof(*m));
    m->sw_low = DSP_RING_CAPACITY;
    m->pc_low = DSP_RING_CAPACITY;
    m->pc_step_q16 = 65536u;
    m->params.game_db_x100 = 0;
    m->params.master_db_x100 = -4500; // Safe headphone-friendly default -45.0 dB
    m->params.pc_trim_db_x100 = 0;
    m->params.win_vol_db_x100[0] = 0;
    m->params.win_vol_db_x100[1] = 0;
    m->params.win_vol_db_x100[2] = 0;
    m->params.win_mute_mask = 0;
    m->params.eq_enabled = false;
    m->params.rx_pin_mode = 0;
    dsp_eq_init_flat(&m->eq, false);
    recompute_gains_unlocked(m);
    m->cur_game_gain = m->target_game_gain;
    m->cur_pc_gain_l = m->target_pc_gain_l;
    m->cur_pc_gain_r = m->target_pc_gain_r;
    m->cur_master_gain = m->target_master_gain;
}

void dsp_mixer_recompute_gains(dsp_mixer_t *m) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    recompute_gains_unlocked(m);
    dsp_unlock_irq(irq);
}

void dsp_mixer_set_params(dsp_mixer_t *m, const dsp_params_t *p) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    bool turning_on = (!m->params.eq_enabled && p->eq_enabled);
    m->params = *p;
    m->eq.enabled = p->eq_enabled;
    if (turning_on && m->eq.cur_mix <= 1.0e-4f) m->eq.reset_pending = true;
    recompute_gains_unlocked(m);
    dsp_unlock_irq(irq);
}

void dsp_mixer_set_win_mute(dsp_mixer_t *m, uint8_t ch, bool muted) {
    if (ch > 2) return;
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    if (muted) m->params.win_mute_mask |= (uint8_t)(1u << ch);
    else m->params.win_mute_mask &= (uint8_t)~(1u << ch);
    recompute_gains_unlocked(m);
    dsp_unlock_irq(irq);
}

void dsp_mixer_set_win_vol_q8(dsp_mixer_t *m, uint8_t ch, int16_t vol_q8) {
    if (ch > 2) return;
    if (vol_q8 < -15360) vol_q8 = -15360;
    if (vol_q8 > 0) vol_q8 = 0;
    int32_t db_x100 = ((int32_t)vol_q8 * 100) / 256;
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->params.win_vol_db_x100[ch] = db_x100;
    recompute_gains_unlocked(m);
    dsp_unlock_irq(irq);
}

void dsp_mixer_set_eq_enabled(dsp_mixer_t *m, bool enabled) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    if (m->eq.enabled != enabled) {
        bool turning_on = (!m->eq.enabled && enabled);
        m->eq.enabled = enabled;
        m->params.eq_enabled = enabled;
        if (turning_on && m->eq.cur_mix <= 1.0e-4f) m->eq.reset_pending = true;
    }
    dsp_unlock_irq(irq);
}

void dsp_mixer_set_test_tone(dsp_mixer_t *m, bool enabled) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->test_tone_enabled = enabled;
    m->tone_phase_l = 0;
    m->tone_phase_r = 0;
    dsp_unlock_irq(irq);
}

uint32_t dsp_sw_level(const dsp_mixer_t *m) { return m->sw_write - m->sw_read; }
uint32_t dsp_pc_level(const dsp_mixer_t *m) { return m->pc_write - m->pc_read; }

void dsp_sw_flush(dsp_mixer_t *m) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->sw_read = m->sw_write;
    m->sw_primed = false;
    m->sw_ended = false;
    m->sw_fade_in = 0;
    m->sw_low = DSP_RING_CAPACITY;
    dsp_unlock_irq(irq);
}

void dsp_sw_mark_ended(dsp_mixer_t *m) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->sw_ended = true;
    dsp_unlock_irq(irq);
}

void dsp_sw_set_active(dsp_mixer_t *m, bool on) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->sw_active = on;
    if (!on) {
        m->sw_read = m->sw_write;
        m->sw_primed = false;
        m->sw_ended = false;
        m->sw_fade_in = 0;
        m->sw_low = DSP_RING_CAPACITY;
    }
    dsp_unlock_irq(irq);
}

void dsp_sw_push_s16le(dsp_mixer_t *m, const uint8_t *pcm, unsigned frames) {
    if (!frames || frames > 64u) return;
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    if (!m->sw_active) {
        dsp_unlock_irq(irq);
        return;
    }
    m->sw_ended = false;
    uint32_t avail = m->sw_write - m->sw_read;
    if (avail + frames > DSP_SW_HARD_HIGH) {
        m->sw_overflows++;
        m->sw_read = m->sw_write;
        m->sw_primed = false;
        m->sw_fade_in = 0;
    }
    uint32_t w = m->sw_write;
    for (unsigned i = 0; i < frames; i++) {
        uint32_t idx = (w + i) & DSP_RING_MASK;
        m->sw_ring[idx][0] = read_s16le(pcm + i * 4u);
        m->sw_ring[idx][1] = read_s16le(pcm + i * 4u + 2u);
    }
    m->sw_write = w + frames;
    avail = m->sw_write - m->sw_read;
    if (avail > m->sw_high) m->sw_high = avail;
    dsp_unlock_irq(irq);
}

void dsp_pc_flush(dsp_mixer_t *m) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->pc_read = m->pc_write;
    m->pc_primed = false;
    m->pc_phase_q16 = 0;
    m->pc_step_q16 = 65536u;
    m->pc_err_lp = 0.0f;
    memset(m->pc_hist, 0, sizeof(m->pc_hist));
    m->pc_low = DSP_RING_CAPACITY;
    dsp_unlock_irq(irq);
}

void dsp_pc_set_active(dsp_mixer_t *m, bool on) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->pc_active = on;
    m->pc_read = m->pc_write;
    m->pc_primed = false;
    m->pc_phase_q16 = 0;
    m->pc_step_q16 = 65536u;
    m->pc_err_lp = 0.0f;
    memset(m->pc_hist, 0, sizeof(m->pc_hist));
    m->pc_low = DSP_RING_CAPACITY;
    dsp_unlock_irq(irq);
}

void dsp_pc_push_s16le(dsp_mixer_t *m, const uint8_t *pcm, unsigned frames) {
    if (!frames || frames > 64u) return;
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    if (!m->pc_active) {
        dsp_unlock_irq(irq);
        return;
    }
    uint32_t avail = m->pc_write - m->pc_read;
    if (avail + frames > DSP_PC_HARD_HIGH) {
        m->pc_overflows++;
        m->pc_read = m->pc_write;
        m->pc_primed = false;
        m->pc_phase_q16 = 0;
        m->pc_err_lp = 0.0f;
        memset(m->pc_hist, 0, sizeof(m->pc_hist));
    }
    uint32_t w = m->pc_write;
    for (unsigned i = 0; i < frames; i++) {
        uint32_t idx = (w + i) & DSP_RING_MASK;
        m->pc_ring[idx][0] = read_s16le(pcm + i * 4u);
        m->pc_ring[idx][1] = read_s16le(pcm + i * 4u + 2u);
    }
    m->pc_write = w + frames;
    avail = m->pc_write - m->pc_read;
    if (avail > m->pc_high) m->pc_high = avail;
    dsp_unlock_irq(irq);
}

// Fast triangle-smoothed L/R check tone: L = 440 Hz, R = 660 Hz @ -24 dBFS.
// Uses pure integer phase + piecewise sine approximation without <math.h>.
static float tone_sample(uint32_t *phase, uint32_t step) {
    uint32_t p = *phase;
    *phase = p + step;
    int32_t signed_p = (int32_t)p;
    float t = (float)signed_p * (1.0f / 2147483648.0f);
    float at = t < 0.0f ? -t : t;
    float s = 4.0f * t * (1.0f - at);
    float sa = s < 0.0f ? -s : s;
    float clean = 0.225f * (s * sa - s) + s;
    return clean * 0.0630957f; // -24 dBFS before master_gain
}

void dsp_render_i2s_block(dsp_mixer_t *m, int32_t *out_i2s, unsigned frames) {
    if (frames == 0 || frames > DSP_I2S_BLOCK_FRAMES) return;

    int16_t sw_tmp[DSP_I2S_BLOCK_FRAMES][2];
    int16_t pc_tmp[DSP_I2S_BLOCK_FRAMES + 4][2];
    unsigned sw_real = 0;
    unsigned sw_fade_start = 0;
    unsigned pc_pop = 0;
    bool pc_running = false;
    bool tone_on = false;
    bool eq_target_on = false;
    uint32_t pc_phase_start = 0;
    uint32_t pc_step = 65536u;
    float g_game, g_pc_l, g_pc_r, g_master, g_preamp;

    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    m->i2s_blocks++;
    g_game = m->target_game_gain;
    g_pc_l = m->target_pc_gain_l;
    g_pc_r = m->target_pc_gain_r;
    g_master = m->target_master_gain;
    g_preamp = m->eq.preamp_linear;
    eq_target_on = m->eq.enabled;
    dsp_eq_apply_pending_resets(&m->eq);
    tone_on = m->test_tone_enabled;

    uint32_t sw_avail = m->sw_write - m->sw_read;
    if (!m->sw_primed) {
        if (m->sw_active && sw_avail > 0 && (sw_avail >= DSP_SW_PRIME || m->sw_ended)) {
            m->sw_primed = true;
            m->sw_fade_in = 0;
        }
    }
    if (m->sw_primed) {
        sw_fade_start = m->sw_fade_in;
        uint32_t take = sw_avail < frames ? sw_avail : frames;
        uint32_t r = m->sw_read;
        for (unsigned i = 0; i < take; i++) {
            uint32_t idx = (r + i) & DSP_RING_MASK;
            sw_tmp[i][0] = m->sw_ring[idx][0];
            sw_tmp[i][1] = m->sw_ring[idx][1];
        }
        m->sw_read = r + take;
        sw_real = take;
        uint32_t left = m->sw_write - m->sw_read;
        if (left < m->sw_low) m->sw_low = left;
        if (take < frames) {
            if (!m->sw_ended) m->sw_underflows++;
            m->sw_primed = false;
            m->sw_ended = false;
            m->sw_fade_in = 0;
        } else {
            m->sw_fade_in += take;
            if (m->sw_fade_in > DSP_SW_PRIME) m->sw_fade_in = DSP_SW_PRIME;
        }
    }

    uint32_t pc_avail = m->pc_write - m->pc_read;
    if (!m->pc_primed && m->pc_active && pc_avail >= DSP_PC_PRIME) {
        uint32_t r = m->pc_read;
        m->pc_hist[0][0] = 0;
        m->pc_hist[0][1] = 0;
        m->pc_hist[1][0] = 0;
        m->pc_hist[1][1] = 0;
        m->pc_hist[2][0] = m->pc_ring[r & DSP_RING_MASK][0];
        m->pc_hist[2][1] = m->pc_ring[r & DSP_RING_MASK][1];
        m->pc_hist[3][0] = m->pc_ring[(r + 1u) & DSP_RING_MASK][0];
        m->pc_hist[3][1] = m->pc_ring[(r + 1u) & DSP_RING_MASK][1];
        m->pc_read = r + 2u;
        m->pc_phase_q16 = 0;
        m->pc_primed = true;
        pc_avail = m->pc_write - m->pc_read;
    }
    if (m->pc_primed) {
        int32_t err = (int32_t)pc_avail - (int32_t)DSP_PC_TARGET;
        m->pc_err_lp += 0.02f * ((float)err - m->pc_err_lp);
        int32_t delta = (int32_t)(m->pc_err_lp);
        if (delta > 96) delta = 96;
        if (delta < -96) delta = -96;
        pc_step = (uint32_t)(65536 + delta);
        m->pc_step_q16 = pc_step;
        pc_phase_start = m->pc_phase_q16;

        uint32_t total_phase = pc_phase_start + frames * pc_step;
        uint32_t needed = total_phase >> 16;
        if (pc_avail >= needed) {
            pc_running = true;
            pc_pop = needed;
            uint32_t r = m->pc_read;
            for (unsigned i = 0; i < needed; i++) {
                uint32_t idx = (r + i) & DSP_RING_MASK;
                pc_tmp[i][0] = m->pc_ring[idx][0];
                pc_tmp[i][1] = m->pc_ring[idx][1];
            }
            m->pc_read = r + needed;
            uint32_t left = m->pc_write - m->pc_read;
            if (left < m->pc_low) m->pc_low = left;
        } else {
            if (m->pc_active && pc_avail > 0) m->pc_underflows++;
            m->pc_read = m->pc_write;
            m->pc_primed = false;
            m->pc_phase_q16 = 0;
            m->pc_err_lp = 0.0f;
        }
    }
    dsp_unlock_irq(irq);

    const float inv_s16 = 1.0f / 32768.0f;
    const float out_scale = 2147483648.0f;
    uint32_t pc_phase = pc_phase_start;
    unsigned pc_pos = 0;

    for (unsigned i = 0; i < frames; i++) {
        float sw_l, sw_r;
        if (i < sw_real) {
            sw_l = (float)sw_tmp[i][0] * inv_s16;
            sw_r = (float)sw_tmp[i][1] * inv_s16;
            unsigned fidx = sw_fade_start + i;
            if (fidx < DSP_SW_PRIME) {
                float ramp = (float)(fidx + 1u) * (1.0f / (float)DSP_SW_PRIME);
                sw_l *= ramp;
                sw_r *= ramp;
            }
            m->sw_tail_l = sw_l;
            m->sw_tail_r = sw_r;
        } else {
            m->sw_tail_l = dsp_flush_tiny(m->sw_tail_l * 0.90f);
            m->sw_tail_r = dsp_flush_tiny(m->sw_tail_r * 0.90f);
            sw_l = m->sw_tail_l;
            sw_r = m->sw_tail_r;
        }

        // Resample PC audio with 4-point Catmull-Rom Hermite interpolation
        float pc_l = 0.0f, pc_r = 0.0f;
        if (pc_running) {
            float mu = (float)(pc_phase & 0xffffu) * (1.0f / 65536.0f);
            for (unsigned ch = 0; ch < 2; ch++) {
                float y0 = (float)m->pc_hist[0][ch] * inv_s16;
                float y1 = (float)m->pc_hist[1][ch] * inv_s16;
                float y2 = (float)m->pc_hist[2][ch] * inv_s16;
                float y3 = (float)m->pc_hist[3][ch] * inv_s16;
                float c0 = y1;
                float c1 = 0.5f * (y2 - y0);
                float c2 = y0 - 2.5f * y1 + 2.0f * y2 - 0.5f * y3;
                float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
                float v = ((c3 * mu + c2) * mu + c1) * mu + c0;
                if (ch == 0) pc_l = v; else pc_r = v;
            }
            pc_phase += pc_step;
            while (pc_phase >= 65536u) {
                pc_phase -= 65536u;
                m->pc_hist[0][0] = m->pc_hist[1][0];
                m->pc_hist[0][1] = m->pc_hist[1][1];
                m->pc_hist[1][0] = m->pc_hist[2][0];
                m->pc_hist[1][1] = m->pc_hist[2][1];
                m->pc_hist[2][0] = m->pc_hist[3][0];
                m->pc_hist[2][1] = m->pc_hist[3][1];
                if (pc_pos < pc_pop) {
                    m->pc_hist[3][0] = pc_tmp[pc_pos][0];
                    m->pc_hist[3][1] = pc_tmp[pc_pos][1];
                    pc_pos++;
                } else {
                    m->pc_hist[3][0] = 0;
                    m->pc_hist[3][1] = 0;
                }
            }
        }

        // Smooth gains per sample
        m->cur_game_gain += 0.004f * (g_game - m->cur_game_gain);
        m->cur_pc_gain_l += 0.004f * (g_pc_l - m->cur_pc_gain_l);
        m->cur_pc_gain_r += 0.004f * (g_pc_r - m->cur_pc_gain_r);
        m->cur_master_gain += 0.004f * (g_master - m->cur_master_gain);

        float mix_l = sw_l * m->cur_game_gain + pc_l * m->cur_pc_gain_l;
        float mix_r = sw_r * m->cur_game_gain + pc_r * m->cur_pc_gain_r;

        // Apply 11-band headphone EQ to the mixed (Switch 2 + PC) stereo bus
        // before MasterGain with click-free wet/dry crossfade and preamp smoothing;
        // OBS USB Mic branches upstream in rx_route_packet().
        float target_mix = eq_target_on ? 1.0f : 0.0f;
        m->eq.cur_preamp += 0.010f * (g_preamp - m->eq.cur_preamp);
        m->eq.cur_mix += 0.010f * (target_mix - m->eq.cur_mix);
        if (eq_target_on || m->eq.cur_mix > 1.0e-4f) {
            float wet_l = mix_l;
            float wet_r = mix_r;
            dsp_eq_run_biquads(&m->eq, m->eq.cur_preamp, &wet_l, &wet_r);
            float mix_w = m->eq.cur_mix;
            if (eq_target_on && mix_w > 0.9999f) {
                m->eq.cur_mix = 1.0f;
                mix_l = wet_l;
                mix_r = wet_r;
            } else {
                mix_l += mix_w * (wet_l - mix_l);
                mix_r += mix_w * (wet_r - mix_r);
            }
        } else if (m->eq.cur_mix != 0.0f) {
            m->eq.cur_mix = 0.0f;
            dsp_eq_reset_states(&m->eq);
        }

        if (tone_on) {
            mix_l += tone_sample(&m->tone_phase_l, 39370534u);
            mix_r += tone_sample(&m->tone_phase_r, 59055800u);
        }
        mix_l *= m->cur_master_gain;
        mix_r *= m->cur_master_gain;

        // Hard safety limiter for 32-bit signed I2S range
        if (mix_l > 0.9999999f) { mix_l = 0.9999999f; m->clip_count++; }
        else if (mix_l < -1.0f) { mix_l = -1.0f; m->clip_count++; }
        if (mix_r > 0.9999999f) { mix_r = 0.9999999f; m->clip_count++; }
        else if (mix_r < -1.0f) { mix_r = -1.0f; m->clip_count++; }

        float scaled_l = mix_l * out_scale;
        float scaled_r = mix_r * out_scale;
        int32_t s32_l = (scaled_l >= 2147483520.0f) ? 2147483647 :
                        (scaled_l <= -2147483648.0f) ? (-2147483647 - 1) : (int32_t)scaled_l;
        int32_t s32_r = (scaled_r >= 2147483520.0f) ? 2147483647 :
                        (scaled_r <= -2147483648.0f) ? (-2147483647 - 1) : (int32_t)scaled_r;
        out_i2s[i * 2u] = s32_l;
        out_i2s[i * 2u + 1u] = s32_r;
    }

    if (pc_running) {
        dsp_lock_irq(&irq);
        if (m->pc_primed) m->pc_phase_q16 = pc_phase & 0xffffu;
        dsp_unlock_irq(irq);
    }
}
