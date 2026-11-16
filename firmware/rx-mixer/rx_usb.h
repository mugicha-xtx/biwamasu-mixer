// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "dsp_core.h"

enum {
    RX_CAPACITY = 2048,
    RX_PRIME = 144,
    RX_TARGET = 144,
    RX_HARD_HIGH = 640
};

typedef struct {
    uint8_t bytes[512];
    unsigned used;
    uint32_t audio, status, crc, bad, discarded, startup_bytes, startup_bad;
    bool locked;
} rx_parser_t;

typedef struct {
    uint64_t session, index;
    uint32_t seq, us, drops, bad;
    uint16_t flags, size;
    uint8_t type;
    const uint8_t *pcm;
} rx_packet_t;

typedef void (*rx_handler_t)(const rx_packet_t *, void *);

typedef struct {
    uint8_t pcm[RX_CAPACITY][4];
    uint32_t write, read, received, consumed, discarded, overflow, underflow, reprimes;
    uint32_t missing, duplicates, sequence_errors, sessions, source_errors, tail_missing;
    uint64_t session, expected;
    uint32_t expected_seq;
    uint32_t high, low, small, normal, large, silence;
    uint32_t prime_ticks, partial_packets, first_index_low, first_seq;
    bool have, primed, active, ended;
} rx_usb_audio_t;

void rx_parser_reset(rx_parser_t *s);
void rx_feed(rx_parser_t *s, const uint8_t *data, unsigned count, rx_handler_t cb, void *ctx);

unsigned rx_usb_level(const rx_usb_audio_t *a);
void rx_usb_init(rx_usb_audio_t *a);
void rx_usb_flush(rx_usb_audio_t *a);
void rx_usb_set_active(rx_usb_audio_t *a, bool on);
void rx_usb_mark_ended(rx_usb_audio_t *a);

// Routes a validated UART packet to both raw USB capture (OBS) and Switch I2S playback.
void rx_route_packet(rx_usb_audio_t *mic, dsp_mixer_t *mixer, const rx_packet_t *p);

// Produces a legal 47/48/49-frame USB IN packet, zero-padding only trailing silence.
unsigned rx_usb_take(rx_usb_audio_t *a, uint8_t out[196]);
unsigned rx_usb_packet(rx_usb_audio_t *a, uint8_t out[196], unsigned *real_frames);
