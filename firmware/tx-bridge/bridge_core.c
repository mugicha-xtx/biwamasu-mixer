// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#include "bridge_core.h"
#include <string.h>
unsigned bridge_count(bridge_queue_t *q) { return atomic_load_explicit(&q->write,memory_order_acquire)-atomic_load_explicit(&q->read,memory_order_acquire); }
bool bridge_push(bridge_queue_t *q,const bridge_pcm_t *p) {
 unsigned w=atomic_load_explicit(&q->write,memory_order_relaxed);
 if(w-atomic_load_explicit(&q->read,memory_order_acquire)==BRIDGE_SLOTS) return false;
 q->slots[w%BRIDGE_SLOTS]=*p; atomic_store_explicit(&q->write,w+1,memory_order_release); return true;
}
bool bridge_pop(bridge_queue_t *q,bridge_pcm_t *p) {
 unsigned r=atomic_load_explicit(&q->read,memory_order_relaxed);
 if(r==atomic_load_explicit(&q->write,memory_order_acquire)) return false;
 *p=q->slots[r%BRIDGE_SLOTS]; atomic_store_explicit(&q->read,r+1,memory_order_release); return true;
}
void bridge_put32(uint8_t *p,uint32_t n) { for(unsigned i=0;i<4;i++) p[i]=(uint8_t)(n>>(8*i)); }
static void put64(uint8_t *p,uint64_t n) { bridge_put32(p,(uint32_t)n); bridge_put32(p+4,(uint32_t)(n>>32)); }
uint32_t bridge_crc(const uint8_t *p,unsigned n) {
 uint32_t c=0xffffffffu; while(n--) { c^=*p++; for(unsigned i=0;i<8;i++) c=(c>>1)^(0xedb88320u & (0u-(c&1u))); } return c^0xffffffffu;
}
unsigned bridge_encode(uint8_t out[256],const bridge_pcm_t *p,uint8_t type,uint16_t flags,uint32_t drops,uint32_t bad) {
 memset(out,0,BRIDGE_HEADER); memcpy(out,"S2AUD002",8); out[8]=2; out[9]=type; out[10]=(uint8_t)flags; out[11]=(uint8_t)(flags>>8);
 put64(out+12,p->session); bridge_put32(out+20,p->seq); put64(out+24,p->index); bridge_put32(out+32,p->us);
 out[36]=(uint8_t)p->size; out[37]=(uint8_t)(p->size>>8); out[38]=type==1?p->size/4:0;
 bridge_put32(out+40,drops); bridge_put32(out+44,bad); memcpy(out+BRIDGE_HEADER,p->pcm,p->size);
 bridge_put32(out+BRIDGE_HEADER+p->size,bridge_crc(out,BRIDGE_HEADER+p->size)); return (BRIDGE_HEADER+4)+p->size;
}
