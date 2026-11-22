// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stdatomic.h>
enum { BRIDGE_SLOTS=8, BRIDGE_PCM=192, BRIDGE_HEADER=48 };
typedef struct { uint64_t session, index; uint32_t seq, us; uint16_t size; uint8_t pcm[BRIDGE_PCM]; } bridge_pcm_t;
typedef struct { bridge_pcm_t slots[BRIDGE_SLOTS]; atomic_uint write, read; } bridge_queue_t;
_Static_assert((BRIDGE_SLOTS & (BRIDGE_SLOTS - 1)) == 0, "BRIDGE_SLOTS must be power of two");
_Static_assert(ATOMIC_INT_LOCK_FREE==2 && sizeof(unsigned)==4,"32-bit lock-free tickets required");
_Static_assert(sizeof(bridge_pcm_t)<=256,"slot budget");
unsigned bridge_count(bridge_queue_t *q);
bool bridge_push(bridge_queue_t *q,const bridge_pcm_t *p);
bool bridge_pop(bridge_queue_t *q,bridge_pcm_t *p);
uint32_t bridge_crc(const uint8_t *p,unsigned n);
unsigned bridge_encode(uint8_t out[256],const bridge_pcm_t *p,uint8_t type,uint16_t flags,uint32_t drops,uint32_t bad);
void bridge_put32(uint8_t *p,uint32_t n);
