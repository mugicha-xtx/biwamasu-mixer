// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#include "rx_usb.h"
#include <string.h>

static uint16_t u16le(const uint8_t *b) { return (uint16_t)(b[0] | (b[1] << 8)); }
static uint32_t u32le(const uint8_t *b) {
    return (uint32_t)b[0] | ((uint32_t)b[1] << 8) | ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24);
}
static uint64_t u64le(const uint8_t *b) { return u32le(b) | ((uint64_t)u32le(b + 4) << 32); }

static void discard(rx_parser_t *s, unsigned n) {
    if (s->locked) s->discarded += n;
    else s->startup_bytes += n;
    s->used -= n;
    memmove(s->bytes, s->bytes + n, s->used);
}

void rx_parser_reset(rx_parser_t *s) {
    s->used = 0;
    s->locked = false;
}

void rx_feed(rx_parser_t *s, const uint8_t *data, unsigned count, rx_handler_t cb, void *ctx) {
    while (count--) {
        if (s->used == sizeof(s->bytes)) discard(s, 1);
        s->bytes[s->used++] = *data++;
        while (s->used >= 8u) {
            uint8_t *b = s->bytes;
            if (memcmp(b, "S2AUD002", 8) != 0) {
                discard(s, 1);
                continue;
            }
            if (s->used < 48u) break;
            unsigned type = b[9];
            unsigned len = u16le(b + 36);
            unsigned frames = u16le(b + 38);
            unsigned flags = u16le(b + 10);
            bool valid = (b[8] == 2) && (u64le(b + 12) != 0) && !(flags & ~15u) &&
                         ((type == 1 && !(flags & 8u) && len >= 4u && len <= 192u && !(len % 4u) && frames == len / 4u) ||
                          (type == 2 && len == 64u && frames == 0u));
            if (!valid) {
                if (s->locked) s->bad++;
                else s->startup_bad++;
                discard(s, 1);
                continue;
            }
            unsigned total = 52u + len;
            if (s->used < total) break;
            if (dsp_crc32(b, 48u + len) != u32le(b + 48u + len)) {
                if (s->locked) s->crc++;
                else s->startup_bad++;
                discard(s, 1);
                continue;
            }
            if (type == 2 && (u32le(b + 48) != 48000u || u32le(b + 52) != 2u || u32le(b + 56) != 16u)) {
                if (s->locked) s->bad++;
                else s->startup_bad++;
                discard(s, 1);
                continue;
            }
            s->locked = true;
            rx_packet_t p = {
                .session = u64le(b + 12),
                .index = u64le(b + 24),
                .seq = u32le(b + 20),
                .us = u32le(b + 32),
                .drops = u32le(b + 40),
                .bad = u32le(b + 44),
                .flags = (uint16_t)flags,
                .type = (uint8_t)type,
                .size = (uint16_t)len,
                .pcm = b + 48
            };
            if (type == 1) s->audio++;
            else s->status++;
            if (cb) cb(&p, ctx);
            s->used -= total;
            memmove(b, b + total, s->used);
        }
    }
}

static void rx_usb_recompute_gains_unlocked(rx_usb_audio_t *a) {
    for (unsigned c = 0; c < 2u; c++) {
        bool muted = ((a->mute_mask & 1u) != 0u) ||
                     ((a->mute_mask & (1u << (c + 1u))) != 0u);
        int32_t total_q8 = (int32_t)a->vol_q8[0] + (int32_t)a->vol_q8[c + 1u];
        uint16_t q15;
        if (muted || total_q8 <= DSP_WIN_VOL_Q8_MIN) {
            q15 = 0u;
        } else if (total_q8 >= DSP_WIN_VOL_Q8_MAX) {
            q15 = 32768u;
        } else {
            int32_t db_x100 = (total_q8 * 25 - 32) / 64;
            float lin = dsp_db_x100_to_linear(db_x100);
            uint32_t v = (uint32_t)(lin * 32768.0f + 0.5f);
            if (v > 32768u) v = 32768u;
            q15 = (uint16_t)v;
        }
        if (c == 0u) a->gain_q15_l = q15;
        else a->gain_q15_r = q15;
    }
}

void rx_usb_init(rx_usb_audio_t *a) {
    memset(a, 0, sizeof(*a));
    a->low = RX_CAPACITY;
    a->gain_q15_l = 32768u;
    a->gain_q15_r = 32768u;
}

unsigned rx_usb_level(const rx_usb_audio_t *a) {
    return a->write - a->read;
}

static void rx_usb_flush_unlocked(rx_usb_audio_t *a) {
    a->discarded += rx_usb_level(a);
    a->read = a->write;
    a->primed = false;
    a->prime_ticks = 0;
    a->reprimes++;
    a->ended = false;
    a->low = RX_CAPACITY;
}

void rx_usb_flush(rx_usb_audio_t *a) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    rx_usb_flush_unlocked(a);
    dsp_unlock_irq(irq);
}

void rx_usb_set_active(rx_usb_audio_t *a, bool on) {
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    rx_usb_flush_unlocked(a);
    a->active = on;
    dsp_unlock_irq(irq);
}

void rx_usb_mark_ended(rx_usb_audio_t *a) {
    a->ended = true;
}

void rx_usb_set_mute(rx_usb_audio_t *a, uint8_t ch, bool muted) {
    if (ch > 2u) return;
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    uint8_t bit = (uint8_t)(1u << ch);
    if (muted) a->mute_mask |= bit;
    else a->mute_mask &= (uint8_t)~bit;
    rx_usb_recompute_gains_unlocked(a);
    dsp_unlock_irq(irq);
}

void rx_usb_set_vol_q8(rx_usb_audio_t *a, uint8_t ch, int16_t vol_q8) {
    if (ch > 2u) return;
    if (vol_q8 < DSP_WIN_VOL_Q8_MIN) vol_q8 = DSP_WIN_VOL_Q8_MIN;
    if (vol_q8 > DSP_WIN_VOL_Q8_MAX) vol_q8 = DSP_WIN_VOL_Q8_MAX;
    uint32_t irq = 0;
    dsp_lock_irq(&irq);
    a->vol_q8[ch] = vol_q8;
    rx_usb_recompute_gains_unlocked(a);
    dsp_unlock_irq(irq);
}

void rx_route_packet(rx_usb_audio_t *mic, dsp_mixer_t *mixer, const rx_packet_t *p) {
    if (p->type == 2) {
        if (mic->have && p->session == mic->session && (p->flags & 8u)) {
            if (p->index > mic->expected) {
                mic->tail_missing += (uint32_t)(p->index - mic->expected);
                mic->source_errors++;
            }
            mic->ended = true;
            if (mixer) dsp_sw_mark_ended(mixer);
        }
        return;
    }

    unsigned n = p->size / 4u;
    if (!mic->have || p->session != mic->session) {
        rx_usb_flush(mic);
        if (mixer) dsp_sw_flush(mixer);
        mic->have = true;
        mic->session = p->session;
        mic->expected = p->index;
        mic->expected_seq = p->seq;
        mic->sessions++;
    }
    if (p->index < mic->expected) {
        mic->duplicates++;
        return;
    }
    bool gap = (p->index != mic->expected) || (p->seq != mic->expected_seq);
    if (p->index > mic->expected) mic->missing += (uint32_t)(p->index - mic->expected);
    if (p->seq != mic->expected_seq) mic->sequence_errors++;
    if (gap) {
        rx_usb_flush(mic);
        if (mixer) dsp_sw_flush(mixer);
    }
    if (p->flags & 6u) mic->source_errors++;
    mic->ended = false;
    mic->expected = p->index + n;
    mic->expected_seq = p->seq + 1u;
    mic->received += n;

    // Always feed Switch headphone I2S path regardless of USB mic client state.
    if (mixer) {
        dsp_sw_push_s16le(mixer, p->pcm, n);
    }

    if (!mic->active) {
        mic->discarded += n;
        return;
    }
    if (rx_usb_level(mic) + n > RX_HARD_HIGH) {
        mic->overflow++;
        rx_usb_flush(mic);
    }
    for (unsigned i = 0; i < n; i++) {
        memcpy(mic->pcm[(mic->write + i) % RX_CAPACITY], p->pcm + i * 4u, 4u);
    }
    mic->write += n;
    unsigned level = rx_usb_level(mic);
    if (level > mic->high) mic->high = level;
}

unsigned rx_usb_take(rx_usb_audio_t *a, uint8_t out[196]) {
    unsigned level = rx_usb_level(a);
    if (!a->active) return 0;
    if (!a->primed) {
        if (!level) {
            a->prime_ticks = 0;
            return 0;
        }
        if (level < RX_PRIME && !a->ended && ++a->prime_ticks < 6u) return 0;
        a->primed = true;
        a->prime_ticks = 0;
    }
    unsigned n = level > RX_TARGET + 48u ? 49u : level < RX_TARGET - 48u ? 47u : 48u;
    if (level < n) {
        if (level) {
            n = level;
            a->partial_packets++;
        } else {
            if (!a->ended) a->underflow++;
            rx_usb_flush(a);
            return 0;
        }
    }
    const uint16_t gl = a->gain_q15_l;
    const uint16_t gr = a->gain_q15_r;
    if (gl == 32768u && gr == 32768u) {
        for (unsigned i = 0; i < n; i++) {
            memcpy(out + i * 4u, a->pcm[(a->read + i) % RX_CAPACITY], 4u);
        }
    } else if (gl == 0u && gr == 0u) {
        memset(out, 0, n * 4u);
    } else {
        for (unsigned i = 0; i < n; i++) {
            const uint8_t *src = a->pcm[(a->read + i) % RX_CAPACITY];
            int32_t l = (int16_t)((uint16_t)src[0] | ((uint16_t)src[1] << 8));
            int32_t r = (int16_t)((uint16_t)src[2] | ((uint16_t)src[3] << 8));
            l = (l * (int32_t)gl + 16384) >> 15;
            r = (r * (int32_t)gr + 16384) >> 15;
            uint8_t *dst = out + i * 4u;
            dst[0] = (uint8_t)(int16_t)l;
            dst[1] = (uint8_t)((uint16_t)(int16_t)l >> 8);
            dst[2] = (uint8_t)(int16_t)r;
            dst[3] = (uint8_t)((uint16_t)(int16_t)r >> 8);
        }
    }
    a->read += n;
    a->consumed += n;
    if (n == 47u) a->small++;
    else if (n == 48u) a->normal++;
    else if (n == 49u) a->large++;
    unsigned left = rx_usb_level(a);
    if (left < a->low) a->low = left;
    if (!left) a->primed = false;
    return n;
}

unsigned rx_usb_packet(rx_usb_audio_t *a, uint8_t out[196], unsigned *real_frames) {
    unsigned real = rx_usb_take(a, out);
    unsigned n = real;
    if (n < 47u) {
        n = 48u;
        memset(out + real * 4u, 0, (n - real) * 4u);
    }
    if (real_frames) *real_frames = real;
    return n;
}
