// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#include "selftest_pack.h"
#include <string.h>

#ifdef SELFTEST_STANDALONE
void dsp_lock_irq(uint32_t *state_out) { *state_out = 0; }
void dsp_unlock_irq(uint32_t state) { (void)state; }
#endif

static void u32_le(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
    p[2] = (uint8_t)(v >> 16);
    p[3] = (uint8_t)(v >> 24);
}

static void u64_le(uint8_t *p, uint64_t v) {
    u32_le(p, (uint32_t)v);
    u32_le(p + 4, (uint32_t)(v >> 32));
}

static void u16_le(uint8_t *p, uint16_t v) {
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
}

static unsigned build_v2_packet(uint8_t *out, uint8_t type, uint16_t flags,
                                uint64_t session, uint32_t seq, uint64_t index,
                                uint16_t size, uint16_t frames, const uint8_t *payload) {
    memset(out, 0, 52u + size);
    memcpy(out, "S2AUD002", 8);
    out[8] = 2u;
    out[9] = type;
    u16_le(out + 10, flags);
    u64_le(out + 12, session);
    u32_le(out + 20, seq);
    u64_le(out + 24, index);
    u16_le(out + 36, size);
    u16_le(out + 38, frames);
    if (size && payload) memcpy(out + 48, payload, size);
    u32_le(out + 48 + size, dsp_crc32(out, 48u + size));
    return 52u + size;
}

typedef struct {
    rx_usb_audio_t *mic;
    dsp_mixer_t *mixer;
    unsigned callbacks;
} route_ctx_t;

static void route_cb(const rx_packet_t *p, void *ctx) {
    route_ctx_t *r = (route_ctx_t *)ctx;
    r->callbacks++;
    rx_route_packet(r->mic, r->mixer, p);
}

#define CHECK(cond) do { if (!(cond)) return (uint32_t)__LINE__; } while (0)

uint32_t pack_selftest_run(dsp_mixer_t *m, rx_usb_audio_t *mic) {
    // 1. Standard IEEE CRC32 check ("123456789" == 0xCBF43926)
    const uint8_t vec[] = "123456789";
    CHECK(dsp_crc32(vec, 9u) == 0xcbf43926u);

    // 2. dB -> linear checks
    CHECK(dsp_db_x100_to_linear(-6000) == 0.0f);
    CHECK(dsp_db_x100_to_linear(0) == 1.0f);
    float g_m6 = dsp_db_x100_to_linear(-602);
    CHECK(g_m6 > 0.4995f && g_m6 < 0.5005f);
    float g_m20 = dsp_db_x100_to_linear(-2000);
    CHECK(g_m20 > 0.0999f && g_m20 < 0.1001f);
    float g_m40 = dsp_db_x100_to_linear(-4000);
    CHECK(g_m40 > 0.0099f && g_m40 < 0.0101f);
    float g_m521 = dsp_db_x100_to_linear(-521);
    CHECK(g_m521 > 0.5488f && g_m521 < 0.5490f);

    // 3. Default off/flat/safe -40dB verification + explicit biquad attenuation test
    dsp_mixer_init(m);
    dsp_eq_t *eq = &m->eq;
    CHECK(!m->params.eq_enabled && !eq->enabled);
    CHECK(m->params.master_db_x100 == -4000);
    CHECK(eq->preamp_linear == 1.0f);

    dsp_mixer_set_eq_enabled(m, true);
    float ul = 0.25f, ur = -0.375f;
    dsp_eq_process_stereo(eq, &ul, &ur);
    CHECK(ul == 0.25f && ur == -0.375f);

    const dsp_biquad_coeff_t test_lsc = {
        9.944633599e-01f, -1.974159463e+00f, 9.798027953e-01f, -1.974049841e+00f, 9.743757775e-01f
    };
    dsp_eq_set_preamp(eq, 0.5489085556f);
    dsp_eq_set_band(eq, 0u, &test_lsc);

    float peak_eq_low = 0.0f;
    // Generate ~100 Hz triangle wave (period 480 samples) over 2 periods (960 samples)
    for (unsigned n = 0; n < 960u; n++) {
        unsigned ph = n % 480u;
        float x = (ph < 120u) ? ((float)ph / 240.0f) :
                  (ph < 360u) ? (0.5f - (float)(ph - 120u) / 240.0f) :
                                (-0.5f + (float)(ph - 360u) / 240.0f);
        float l = x, r = -x;
        dsp_eq_process_stereo(eq, &l, &r);
        if (n >= 480u) {
            float al = l < 0.0f ? -l : l;
            if (al > peak_eq_low) peak_eq_low = al;
        }
    }
    CHECK(peak_eq_low > 0.02f && peak_eq_low < 0.22f);

    // EQ bypass check: output must match input verbatim
    dsp_mixer_set_eq_enabled(m, false);
    float bl = 0.25f, br = -0.375f;
    dsp_eq_process_stereo(eq, &bl, &br);
    CHECK(bl == 0.25f && br == -0.375f);

    // 4. Dual-consumer UART routing + QueueLow reset on flush + OBS independent from I2S
    dsp_mixer_init(m);
    dsp_params_t p = m->params;
    p.master_db_x100 = 0; // Unity 0dB for exact numerical test
    dsp_mixer_set_params(m, &p);
    dsp_sw_set_active(m, true);

    rx_usb_init(mic);
    rx_usb_set_active(mic, false); // OBS closed initially

    rx_parser_t parser;
    rx_parser_reset(&parser);
    route_ctx_t ctx = { .mic = mic, .mixer = m, .callbacks = 0 };

    uint8_t wire[256];
    uint8_t pcm[192];
    for (unsigned pkt = 0; pkt < 4u; pkt++) {
        for (unsigned i = 0; i < 48u; i++) {
            int16_t s = (int16_t)(1000 + (int)(pkt * 48u + i));
            u16_le(pcm + i * 4u, (uint16_t)s);
            u16_le(pcm + i * 4u + 2u, (uint16_t)(-s));
        }
        unsigned nbytes = build_v2_packet(wire, 1u, 1u, 100ull, pkt, (uint64_t)pkt * 48ull, 192u, 48u, pcm);
        rx_feed(&parser, wire, nbytes, route_cb, &ctx);
    }
    CHECK(ctx.callbacks == 4u);
    CHECK(rx_usb_level(mic) == 0u);
    CHECK(mic->discarded == 192u);
    CHECK(dsp_sw_level(m) == 192u);

    int32_t i2s[DSP_I2S_BLOCK_FRAMES * 2];
    for (unsigned blk = 0; blk < 6u; blk++) {
        dsp_render_i2s_block(m, i2s, DSP_I2S_BLOCK_FRAMES);
    }
    CHECK(dsp_sw_level(m) == 0u);
    CHECK(i2s[62] > 0 && i2s[63] < 0);

    rx_usb_set_active(mic, true);
    CHECK(mic->low == RX_CAPACITY);
    for (unsigned pkt = 4u; pkt < 10u; pkt++) {
        for (unsigned i = 0; i < 48u; i++) {
            int16_t s = (int16_t)(500 + (int)(pkt * 48u + i));
            u16_le(pcm + i * 4u, (uint16_t)s);
            u16_le(pcm + i * 4u + 2u, (uint16_t)(s * 2));
        }
        unsigned nbytes = build_v2_packet(wire, 1u, 1u, 100ull, pkt, (uint64_t)pkt * 48ull, 192u, 48u, pcm);
        rx_feed(&parser, wire, nbytes, route_cb, &ctx);
    }
    CHECK(rx_usb_level(mic) == 288u);
    uint8_t usb_out[196];
    unsigned real_f = 0;
    unsigned usb_n = rx_usb_packet(mic, usb_out, &real_f);
    CHECK(usb_n == 49u && real_f == 49u);
    CHECK(mic->low == 239u);
    rx_usb_flush(mic);
    CHECK(mic->low == RX_CAPACITY);

    // 5. PC Speaker push + Hermite resample + Mix test
    dsp_sw_flush(m);
    m->sw_tail_l = 0.0f;
    m->sw_tail_r = 0.0f;
    dsp_pc_set_active(m, true);
    for (unsigned pkt = 0; pkt < 8u; pkt++) {
        for (unsigned i = 0; i < 48u; i++) {
            u16_le(pcm + i * 4u, 2048u);
            u16_le(pcm + i * 4u + 2u, (uint16_t)(-2048));
        }
        dsp_pc_push_s16le(m, pcm, 48u);
    }
    CHECK(dsp_pc_level(m) == 384u);
    for (unsigned blk = 0; blk < 10u; blk++) {
        dsp_render_i2s_block(m, i2s, DSP_I2S_BLOCK_FRAMES);
    }
    CHECK(i2s[62] > 100000000 && i2s[63] < -100000000);

    // 6. Simultaneous Switch + PC digital mixing check
    for (unsigned pkt = 0; pkt < 4u; pkt++) {
        for (unsigned i = 0; i < 48u; i++) {
            u16_le(pcm + i * 4u, 1024u);
            u16_le(pcm + i * 4u + 2u, 1024u);
        }
        dsp_sw_push_s16le(m, pcm, 48u);
    }
    for (unsigned pkt = 0; pkt < 2u; pkt++) {
        for (unsigned i = 0; i < 48u; i++) {
            u16_le(pcm + i * 4u, 2048u);
            u16_le(pcm + i * 4u + 2u, (uint16_t)(-2048));
        }
        dsp_pc_push_s16le(m, pcm, 48u);
    }
    dsp_render_i2s_block(m, i2s, DSP_I2S_BLOCK_FRAMES);
    dsp_render_i2s_block(m, i2s, DSP_I2S_BLOCK_FRAMES);
    CHECK(i2s[62] > 140000000 && i2s[63] < -70000000);

    // Clean re-init so caller boots into untouched defaults (EQ off, -40 dB)
    dsp_mixer_init(m);
    rx_usb_init(mic);
    return 0;
}

#ifdef SELFTEST_STANDALONE
static dsp_mixer_t standalone_mixer;
static rx_usb_audio_t standalone_mic;

uint32_t pack_selftest(void) {
    return pack_selftest_run(&standalone_mixer, &standalone_mic);
}
#endif
