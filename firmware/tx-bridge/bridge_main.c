// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#include <string.h>
#include <limits.h>
#include "pico/stdlib.h"
#include "pico/bootrom.h"
#include "pico/rand.h"
#include "hardware/sync.h"
#include "hardware/uart.h"
#include "hardware/dma.h"
#include "tusb.h"
#include "device/dcd.h"
#include "bridge_core.h"

static bridge_queue_t queue;
typedef struct {
 uint64_t session,index,last_rx;
 uint32_t seq,qdrop,stale,shorts,zlp,bad,dma_errors,gaps,high,boundary_drop;
 uint32_t callback_max,queue_age_max,loop_max;
 uint32_t tx_age_max,tx_starts,tx_age_buckets[5];
 uint16_t flags,pending_flags;
 bool enabled,alt,pending;
 uint8_t target_state,state;
} state_t;
static volatile state_t s;
static uint32_t baud,last_status;
static int channel,active=-1,ready=-1;
static uint8_t wire[2][256];
static unsigned wire_size[2];
static bridge_pcm_t ready_meta;
static uint32_t dma_started;
static uint32_t boot_requested = 0;
static bool end_sent;

static void transition(uint8_t state,uint16_t reason) {
 uint32_t irq=save_and_disable_interrupts();
 s.enabled=false; s.pending=true; s.target_state=state; s.flags|=reason; s.pending_flags|=reason;
 restore_interrupts(irq);
}
static void uart_setup(void) {
 baud=uart_init(uart0,3000000); uart_set_format(uart0,8,1,UART_PARITY_NONE);
 uart_set_hw_flow(uart0,false,false); uart_set_fifo_enabled(uart0,true);
 gpio_set_function(0,GPIO_FUNC_UART);
 gpio_set_drive_strength(0,GPIO_DRIVE_STRENGTH_2MA);
 gpio_set_slew_rate(0,GPIO_SLEW_RATE_SLOW);
 dma_channel_config c=dma_channel_get_default_config(channel);
 channel_config_set_transfer_data_size(&c,DMA_SIZE_8); channel_config_set_read_increment(&c,true);
 channel_config_set_write_increment(&c,false); channel_config_set_dreq(&c,uart_get_dreq(uart0,true));
 dma_channel_configure(channel,&c,&uart_get_hw(uart0)->dr,NULL,0,false);
}
static state_t snapshot(void) { uint32_t irq=save_and_disable_interrupts(); state_t x=s; restore_interrupts(irq); return x; }
static void start_ready(void) {
 if(active>=0 || ready<0) return;
 uint32_t age=time_us_32()-ready_meta.us;
 if(ready_meta.size && age>=4000) {
  s.stale+=ready_meta.size/4; ready=-1; return;
 }
 if(ready_meta.size) {
  if(age>s.tx_age_max) s.tx_age_max=age;
  s.tx_starts++;
  const uint32_t limits[]={250,500,1000,2000,4000};
  for(unsigned i=0;i<5;i++) if(age<=limits[i]) s.tx_age_buckets[i]++;
 }
 active=ready; ready=-1; dma_started=time_us_32();
 dma_channel_set_read_addr(channel,wire[active],false); dma_channel_set_trans_count(channel,wire_size[active],true);
}
static void status_packet(bool end) {
 state_t x=snapshot(); bridge_pcm_t p={.session=x.session,.index=x.index,.seq=x.seq,.us=time_us_32(),.size=64};
 uint64_t age=x.last_rx?time_us_64()-x.last_rx:UINT64_MAX;
 uint32_t v[16]={48000,2,16,baud,x.state,bridge_count(&queue),x.high,x.qdrop,x.stale,x.shorts,x.zlp,x.bad,x.dma_errors,x.gaps,age>UINT32_MAX?UINT32_MAX:(uint32_t)age,3};
 for(unsigned i=0;i<16;i++) bridge_put32(p.pcm+i*4,v[i]);
 int b=active==0?1:0;
 wire_size[b]=bridge_encode(wire[b],&p,2,(x.enabled?1:0)|(x.flags&6)|(end?8:0),x.qdrop+x.stale,x.bad);
 ready=b; ready_meta.size=0; last_status=time_us_32();
}
static void pump(void) {
 uint32_t now=time_us_32();
 if(active>=0 && !dma_channel_is_busy(channel)) active=-1;
 if(active>=0 && (uint32_t)(now-dma_started)>5000) {
  hw_clear_bits(&dma_hw->ch[(uint)channel].al1_ctrl,DMA_CH0_CTRL_TRIG_EN_BITS);
  dma_channel_abort(channel); active=-1; uart_deinit(uart0); uart_setup(); s.dma_errors++; transition(5,4);
 }
 if(s.pending) {
  if(ready>=0) { if(ready_meta.size) s.stale+=ready_meta.size/4; ready=-1; }
  bridge_pcm_t p; while(bridge_pop(&queue,&p)) s.stale+=p.size/4;
  if(active>=0) return;
  if(!end_sent) { status_packet(true); start_ready(); end_sent=true; return; }
  uint32_t irq=save_and_disable_interrupts();
  s.session++; if(!s.session) s.session=1;
  s.index=0; s.seq=0; s.last_rx=0; s.state=s.target_state;
  s.flags=s.pending_flags; s.pending_flags=0; // Error bits are sticky within an epoch, not forever.
  s.enabled=s.alt && (s.target_state==2 || s.target_state==5); s.pending=false;
  restore_interrupts(irq); end_sent=false;
  status_packet(false); start_ready(); return;
 }
 start_ready();
 if(ready>=0) return;
 if((uint32_t)(now-last_status)>=100000) { status_packet(false); start_ready(); return; }
 bridge_pcm_t p;
 if(bridge_pop(&queue,&p)) {
  uint32_t age=time_us_32()-p.us; if(age>s.queue_age_max) s.queue_age_max=age;
  if(p.session!=s.session || age>=4000) { s.stale+=p.size/4; return; }
  state_t x=snapshot(); int b=active==0?1:0;
  wire_size[b]=bridge_encode(wire[b],&p,1,1|(x.flags&6),x.qdrop+x.stale,x.bad);
  ready=b; ready_meta=p; start_ready();
 }
}
bool tud_audio_rx_done_isr(uint8_t port,uint16_t n,uint8_t func,uint8_t ep,uint8_t alt) {
 (void)port;(void)func;(void)ep;(void)alt;
 uint32_t start=time_us_32(); bridge_pcm_t p;
 if(n>192 || n%4 || tud_audio_available()!=n) goto bad;
 if(tud_audio_read(p.pcm,n)!=n || tud_audio_available()!=0) goto bad;
 if(!n) { s.zlp++; goto done; }
 if(!s.enabled) { s.boundary_drop+=n/4; goto done; }
 uint64_t now=time_us_64();
 if(s.last_rx && now-s.last_rx>2000) s.gaps++;
 // A timing gap alone does not change the PCM format/position. Keep the
 // epoch and accept this first packet; alt/reset/error still create epochs.
 p.session=s.session; p.index=s.index; p.seq=s.seq++; p.us=start; p.size=n;
 s.index+=n/4; s.last_rx=now; s.state=3; if(n<192) s.shorts++;
 if(!bridge_push(&queue,&p)) s.qdrop+=n/4;
 unsigned count=bridge_count(&queue); if(count>s.high) s.high=count;
 goto done;
bad:
 tud_audio_clear_ep_out_ff(); s.bad++; transition(5,2);
done:;
 uint32_t elapsed=time_us_32()-start; if(elapsed>s.callback_max) s.callback_max=elapsed;
 return true;
}
bool tud_audio_set_itf_cb(uint8_t port,const tusb_control_request_t *r) {
 (void)port; if(r->wIndex!=1 || r->wValue>1) return false;
 if((bool)r->wValue!=s.alt) { s.alt=r->wValue!=0; transition(s.alt?2:1,0); } return true;
}
bool tud_audio_set_itf_close_ep_cb(uint8_t port,const tusb_control_request_t *r) { (void)port; if(r->wIndex==1 && r->wValue==0) { s.alt=false; transition(1,0); } return true; }
void tud_mount_cb(void) { if(!s.alt) transition(1,0); }
void tud_event_hook_cb(uint8_t port,uint32_t event,bool in_isr) {
 (void)port; (void)in_isr;
 // A USB bus reset tears down the class without invoking tud_umount_cb.
 if(event==DCD_EVENT_BUS_RESET_START || event==DCD_EVENT_BUS_RESET_END) { s.alt=false; transition(0,0); }
}
void tud_umount_cb(void) { s.alt=false; transition(0,0); }
void tud_suspend_cb(bool remote) { (void)remote; transition(4,0); }
void tud_resume_cb(void) { transition(s.alt?2:1,0); }
static bool rate_request(const tusb_control_request_t *r) { return r->wIndex==1 && r->wValue==0x100 && r->wLength==3; }
bool tud_audio_set_req_ep_cb(uint8_t port,const tusb_control_request_t *r,uint8_t *b) { (void)port; return rate_request(r)&&r->bRequest==AUDIO10_CS_REQ_SET_CUR&&b[0]==0x80&&b[1]==0xbb&&b[2]==0; }
bool tud_audio_get_req_ep_cb(uint8_t port,const tusb_control_request_t *r) {
 if(!rate_request(r)) return false;
 if(r->bRequest==AUDIO10_CS_REQ_GET_CUR || r->bRequest==AUDIO10_CS_REQ_GET_MIN || r->bRequest==AUDIO10_CS_REQ_GET_MAX) {
  uint8_t b[]={0x80,0xbb,0}; return tud_audio_buffer_and_schedule_control_xfer(port,r,b,3);
 }
 if(r->bRequest==AUDIO10_CS_REQ_GET_RES) {
  uint8_t b[]={0,0,0}; return tud_audio_buffer_and_schedule_control_xfer(port,r,b,3);
 }
 return false;
}
#if PROBE_HID
uint16_t tud_hid_get_report_cb(uint8_t itf,uint8_t id,hid_report_type_t type,uint8_t *out,uint16_t len) {
 (void)itf; if(id || (type!=HID_REPORT_TYPE_FEATURE && type!=HID_REPORT_TYPE_INPUT)) return 0;
 state_t x=snapshot(); uint8_t r[63]={0}; memcpy(r,"S2B4",4); r[4]=2; r[5]=x.state;
 bridge_put32(r+8,x.callback_max); bridge_put32(r+12,x.loop_max); bridge_put32(r+16,x.queue_age_max);
 bridge_put32(r+20,x.boundary_drop); bridge_put32(r+24,x.bad); bridge_put32(r+28,x.qdrop+x.stale);
 bridge_put32(r+32,x.tx_age_max); bridge_put32(r+36,x.tx_starts);
 for(unsigned i=0;i<5;i++) bridge_put32(r+40+4*i,x.tx_age_buckets[i]);
 len=TU_MIN(len,63); memcpy(out,r,len); return len;
}
void tud_hid_set_report_cb(uint8_t itf,uint8_t id,hid_report_type_t type,const uint8_t *b,uint16_t len) {
 (void)itf;
 if(!id && (type==HID_REPORT_TYPE_FEATURE || type==HID_REPORT_TYPE_OUTPUT) && len>=1u && b[0]==0x7fu) boot_requested=time_us_32();
}
#endif
int main(void) {
 s.session=get_rand_64(); if(!s.session) s.session=1;
 channel=dma_claim_unused_channel(true); uart_setup();
 const tusb_rhport_init_t init={.role=TUSB_ROLE_DEVICE,.speed=TUSB_SPEED_FULL}; tusb_init(0,&init);
 uint32_t previous=time_us_32(); last_status=previous-100000;
 while(true) {
  uint32_t now=time_us_32(),elapsed=now-previous; previous=now; if(elapsed>s.loop_max) s.loop_max=elapsed;
  pump(); tud_task();
  if(boot_requested && (uint32_t)(time_us_32()-boot_requested)>25000u) reset_usb_boot(0,0);
 }
}
