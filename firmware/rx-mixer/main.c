// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#include <string.h>
#include "pico/stdlib.h"
#include "pico/bootrom.h"
#include "hardware/clocks.h"
#include "hardware/dma.h"
#include "hardware/irq.h"
#include "hardware/pio.h"
#include "hardware/sync.h"
#include "hardware/uart.h"
#include "tusb.h"
#include "device/dcd.h"
#include "i2s_out.pio.h"
#include "dsp_core.h"
#include "rx_usb.h"
#include "flash_cfg.h"
#include "selftest_pack.h"
#include "usb_descriptors.h"

enum {
    PIN_I2S_DATA = 9,
    PIN_I2S_BCK = 10,
    PIN_I2S_LRCK = 11,
    PIN_AUDIO_MUTE = 22,
    UART_RX_PIN_PRIMARY = 13,
    UART_RX_PIN_ALT = 1
};

// RP2350 endless mode UART RX ring (8 KB aligned)
static volatile uint16_t uart_ring[4096] __attribute__((aligned(8192)));

// Ping-pong DMA output buffers for 32 stereo frames (Left, Right) x 32-bit I2S
static int32_t i2s_buf[2][DSP_I2S_BLOCK_FRAMES * 2] __attribute__((aligned(16)));

static dsp_mixer_t mixer;
static rx_usb_audio_t mic;
static rx_parser_t parser;

static int uart_dma_ch = -1;
static int dma_i2s_a = -1;
static int dma_i2s_b = -1;
static uint pio_offset = 0;

static unsigned uart_cursor, uart_prev_write, uart_available;
static uint32_t baud, selftest_line;
static uint32_t poll_last, last_audio, last_rx_activity_us, last_rx_byte_us;
static uint32_t loop_last, loop_max_us, usb_isr_max_us, i2s_isr_max_us, last_servo_us;
static uint32_t uart_errors, dma_overrun, dma_faults, dma_unread_high;
static uint32_t source_drops, source_bad, source_state;
static uint32_t usb_mic_frames, usb_mic_packets, usb_zlp, usb_bad, short_writes, usb_silence;
static uint32_t pc_usb_packets, mic_starts, spk_starts, bus_resets, mic_recoveries, timeout_count;
static uint32_t last_mic_tx_us;

static uint32_t pio_base_div_256, pio_cur_div_256;
static float sw_lvl_lp = (float)DSP_SW_TARGET;

static uint8_t active_rx_pin = UART_RX_PIN_PRIMARY;
static uint32_t rx_pin_switches = 0;
static uint32_t mode = 0; // 0 = mix, 1 = L/R test tone
static uint32_t hid_page = 0;
static uint32_t boot_requested = 0;
static uint32_t flash_saves = 0;
static bool flash_loaded = false;
static bool save_requested = false;
static bool reset_requested = false;
static bool spk_active = false;
static bool mic_active = false;
static bool suspended = false;
static bool timed_out = false;
static int16_t win_vol_q8[3] = {0, 0, 0};

void dsp_lock_irq(uint32_t *state_out) {
    *state_out = save_and_disable_interrupts();
}

void dsp_unlock_irq(uint32_t state) {
    restore_interrupts(state);
}

static uint16_t get_u16le(const uint8_t *b) {
    return (uint16_t)b[0] | ((uint16_t)b[1] << 8);
}

static int16_t get_i16le(const uint8_t *b) {
    return (int16_t)get_u16le(b);
}

static float get_f32le(const uint8_t *b) {
    float v;
    memcpy(&v, b, sizeof(v));
    return v;
}

static void put32(uint8_t *b, uint32_t n) {
    for (unsigned i = 0; i < 4u; i++) b[i] = (uint8_t)(n >> (8u * i));
}

static void uart_dma_start(void) {
    dma_channel_abort(uart_dma_ch);
    dma_channel_config c = dma_channel_get_default_config(uart_dma_ch);
    channel_config_set_transfer_data_size(&c, DMA_SIZE_16);
    channel_config_set_read_increment(&c, false);
    channel_config_set_write_increment(&c, true);
    channel_config_set_ring(&c, true, 13);
    channel_config_set_dreq(&c, uart_get_dreq(uart0, false));
    dma_channel_configure(uart_dma_ch, &c, uart_ring, &uart_get_hw(uart0)->dr,
                          dma_encode_endless_transfer_count(), true);
    uart_cursor = 0;
    uart_prev_write = 0;
    uart_available = 0;
    poll_last = time_us_32();
}

static void apply_uart_rx_pin(uint8_t pin) {
    uint8_t other = (pin == UART_RX_PIN_PRIMARY) ? UART_RX_PIN_ALT : UART_RX_PIN_PRIMARY;
    gpio_deinit(other);
    gpio_pull_up(pin);
    gpio_set_function(pin, UART_FUNCSEL_NUM(uart0, pin));
    active_rx_pin = pin;
    while (uart_is_readable(uart0)) (void)uart_get_hw(uart0)->dr;
    uart_get_hw(uart0)->rsr = 0;
    rx_parser_reset(&parser);
    rx_usb_flush(&mic);
    dsp_sw_flush(&mixer);
    uart_dma_start();
}

static void __isr i2s_dma_irq0_handler(void) {
    uint32_t start = time_us_32();
    uint32_t status = dma_hw->ints0;

    if (status & (1u << (unsigned)dma_i2s_a)) {
        dma_channel_acknowledge_irq0((uint)dma_i2s_a);
        dsp_render_i2s_block(&mixer, i2s_buf[0], DSP_I2S_BLOCK_FRAMES);
        dma_channel_set_read_addr((uint)dma_i2s_a, i2s_buf[0], false);
        dma_channel_set_trans_count((uint)dma_i2s_a, DSP_I2S_BLOCK_FRAMES * 2u, false);
    }
    if (status & (1u << (unsigned)dma_i2s_b)) {
        dma_channel_acknowledge_irq0((uint)dma_i2s_b);
        dsp_render_i2s_block(&mixer, i2s_buf[1], DSP_I2S_BLOCK_FRAMES);
        dma_channel_set_read_addr((uint)dma_i2s_b, i2s_buf[1], false);
        dma_channel_set_trans_count((uint)dma_i2s_b, DSP_I2S_BLOCK_FRAMES * 2u, false);
    }

    uint32_t elapsed = time_us_32() - start;
    if (elapsed > i2s_isr_max_us) i2s_isr_max_us = elapsed;
}

static void i2s_configure_and_start(void) {
    memset(i2s_buf, 0, sizeof(i2s_buf));

    pio_sm_set_enabled(pio0, 0, false);
    pio_sm_clear_fifos(pio0, 0);

    pio_sm_config smc = i2s_out_32_program_get_default_config(pio_offset);
    sm_config_set_out_pins(&smc, PIN_I2S_DATA, 1);
    sm_config_set_sideset_pins(&smc, PIN_I2S_BCK);
    sm_config_set_out_shift(&smc, false, true, 32);
    sm_config_set_fifo_join(&smc, PIO_FIFO_JOIN_TX);
    sm_config_set_clkdiv_int_frac8(&smc, pio_cur_div_256 >> 8, (uint8_t)(pio_cur_div_256 & 0xffu));

    pio_sm_init(pio0, 0, pio_offset + i2s_out_32_offset_entry_point, &smc);

    dma_channel_config ca = dma_channel_get_default_config((uint)dma_i2s_a);
    channel_config_set_transfer_data_size(&ca, DMA_SIZE_32);
    channel_config_set_read_increment(&ca, true);
    channel_config_set_write_increment(&ca, false);
    channel_config_set_dreq(&ca, pio_get_dreq(pio0, 0, true));
    channel_config_set_chain_to(&ca, (uint)dma_i2s_b);
    dma_channel_configure((uint)dma_i2s_a, &ca, &pio0->txf[0], i2s_buf[0], DSP_I2S_BLOCK_FRAMES * 2u, false);

    dma_channel_config cb = dma_channel_get_default_config((uint)dma_i2s_b);
    channel_config_set_transfer_data_size(&cb, DMA_SIZE_32);
    channel_config_set_read_increment(&cb, true);
    channel_config_set_write_increment(&cb, false);
    channel_config_set_dreq(&cb, pio_get_dreq(pio0, 0, true));
    channel_config_set_chain_to(&cb, (uint)dma_i2s_a);
    dma_channel_configure((uint)dma_i2s_b, &cb, &pio0->txf[0], i2s_buf[1], DSP_I2S_BLOCK_FRAMES * 2u, false);

    dma_channel_acknowledge_irq0((uint)dma_i2s_a);
    dma_channel_acknowledge_irq0((uint)dma_i2s_b);
    dma_channel_set_irq0_enabled((uint)dma_i2s_a, true);
    dma_channel_set_irq0_enabled((uint)dma_i2s_b, true);

    irq_set_enabled(DMA_IRQ_0, true);
    pio_sm_set_enabled(pio0, 0, true);
    dma_channel_start((uint)dma_i2s_a);
}

static void pause_audio_for_flash(void) {
    gpio_put(PIN_AUDIO_MUTE, 0);
    sleep_ms(12);
    irq_set_enabled(DMA_IRQ_0, false);
    dma_channel_set_irq0_enabled((uint)dma_i2s_a, false);
    dma_channel_set_irq0_enabled((uint)dma_i2s_b, false);
    dma_channel_abort((uint)dma_i2s_a);
    dma_channel_abort((uint)dma_i2s_b);
    dma_channel_abort((uint)uart_dma_ch);
    pio_sm_set_enabled(pio0, 0, false);
    pio_sm_clear_fifos(pio0, 0);
}

static void resume_audio_after_flash(void) {
    i2s_configure_and_start();
    apply_uart_rx_pin(active_rx_pin);
    last_rx_activity_us = time_us_32();
    last_rx_byte_us = last_rx_activity_us;
    sleep_ms(15);
    gpio_put(PIN_AUDIO_MUTE, 1);
}

static void on_uart_packet(const rx_packet_t *p, void *ctx) {
    (void)ctx;
    if (p->type == 2) {
        source_drops = p->drops;
        source_bad = p->bad;
        source_state = get_u16le(p->pcm + 16) | ((uint32_t)get_u16le(p->pcm + 18) << 16);
    }
    if (p->type == 1) {
        last_audio = time_us_32();
        timed_out = false;
    }

    uint32_t irq = save_and_disable_interrupts();
    rx_route_packet(&mic, &mixer, p);
    restore_interrupts(irq);

    if (p->type == 2 && (p->flags & 8u) && p->session == mic.session) {
        timed_out = true;
    }
}

static void uart_poll(void) {
    uint32_t now = time_us_32();
    if ((uint32_t)(now - poll_last) > 13000u) {
        dma_overrun++;
        rx_parser_reset(&parser);
        rx_usb_flush(&mic);
        dsp_sw_flush(&mixer);
        uart_dma_start();
        return;
    }
    poll_last = now;
    if (!dma_channel_is_busy((uint)uart_dma_ch)) {
        dma_faults++;
        rx_parser_reset(&parser);
        rx_usb_flush(&mic);
        dsp_sw_flush(&mixer);
        uart_dma_start();
        return;
    }

    unsigned write = (unsigned)(((uintptr_t)dma_channel_hw_addr((uint)uart_dma_ch)->write_addr - (uintptr_t)uart_ring) / 2u);
    write &= 4095u;
    uart_available += (write - uart_prev_write) & 4095u;
    uart_prev_write = write;
    if (uart_available > dma_unread_high) dma_unread_high = uart_available;
    if (uart_available > 4096u - 512u) {
        dma_overrun++;
        rx_parser_reset(&parser);
        rx_usb_flush(&mic);
        dsp_sw_flush(&mixer);
        uart_dma_start();
        return;
    }

    unsigned count = uart_available;
    if (count > 256u) count = 256u;
    uint16_t scratch[256];
    for (unsigned i = 0; i < count; i++) scratch[i] = uart_ring[(uart_cursor + i) & 4095u];
    uart_cursor = (uart_cursor + count) & 4095u;
    uart_available -= count;

    if (count > 0u) {
        last_rx_activity_us = now;
        last_rx_byte_us = now;
    }

    for (unsigned i = 0; i < count; i++) {
        if (scratch[i] & 0xf00u) {
            uart_errors++;
            uart_get_hw(uart0)->rsr = 0;
            rx_parser_reset(&parser);
            rx_usb_flush(&mic);
            dsp_sw_flush(&mixer);
            continue;
        }
        uint8_t byte = (uint8_t)scratch[i];
        rx_feed(&parser, &byte, 1u, on_uart_packet, NULL);
    }

    if (mixer.params.rx_pin_mode == 0u) {
        if (!parser.locked && (uint32_t)(now - last_rx_activity_us) > 150000u) {
            uint8_t next_pin = (active_rx_pin == UART_RX_PIN_PRIMARY) ? UART_RX_PIN_ALT : UART_RX_PIN_PRIMARY;
            rx_pin_switches++;
            last_rx_activity_us = now;
            apply_uart_rx_pin(next_pin);
        } else if (parser.locked && (uint32_t)(now - last_rx_byte_us) > 1000000u) {
            rx_parser_reset(&parser);
            last_rx_activity_us = now;
        }
    }

    if (last_audio && !timed_out && (uint32_t)(time_us_32() - last_audio) > 100000u) {
        uint32_t irq = save_and_disable_interrupts();
        rx_usb_mark_ended(&mic);
        dsp_sw_mark_ended(&mixer);
        restore_interrupts(irq);
        timed_out = true;
        timeout_count++;
    }
}

static void pio_clock_servo_poll(uint32_t now) {
    if ((uint32_t)(now - last_servo_us) < 10000u) return;
    last_servo_us = now;

    int32_t delta = 0;
    if (mixer.sw_primed) {
        uint32_t lvl = dsp_sw_level(&mixer);
        sw_lvl_lp += 0.10f * ((float)lvl - sw_lvl_lp);
        if (sw_lvl_lp > (float)DSP_SW_TARGET + 40.0f) delta = -1;
        else if (sw_lvl_lp < (float)DSP_SW_TARGET) delta = +1;
    } else {
        sw_lvl_lp = (float)DSP_SW_TARGET;
    }

    uint32_t target_div = (uint32_t)((int32_t)pio_base_div_256 + delta);
    if (target_div != pio_cur_div_256) {
        pio_cur_div_256 = target_div;
        pio_sm_set_clkdiv_int_frac8(pio0, 0, target_div >> 8, (uint8_t)(target_div & 0xffu));
    }
}

static void fill_mic(void) {
    uint8_t pcm[196];
    unsigned real = 0;
    unsigned n = rx_usb_packet(&mic, pcm, &real);
    usb_silence += n - real;
    unsigned wrote = tud_audio_write(pcm, (uint16_t)(n * 4u));
    if (wrote != n * 4u) {
        short_writes++;
        tud_audio_clear_ep_in_ff();
        rx_usb_flush(&mic);
    }
}

bool tud_audio_rx_done_isr(uint8_t port, uint16_t n_bytes, uint8_t func, uint8_t ep, uint8_t alt) {
    (void)port; (void)n_bytes; (void)func; (void)ep; (void)alt;
    uint32_t start = time_us_32();
    uint8_t buf[196];
    while (tud_audio_available() >= 4u) {
        uint16_t got = tud_audio_read(buf, sizeof(buf));
        unsigned frames = got / 4u;
        if (frames && spk_active && !suspended) {
            dsp_pc_push_s16le(&mixer, buf, frames);
            pc_usb_packets++;
        }
    }
    if (tud_audio_available() > 0u) tud_audio_clear_ep_out_ff();
    uint32_t elapsed = time_us_32() - start;
    if (elapsed > usb_isr_max_us) usb_isr_max_us = elapsed;
    return true;
}

bool tud_audio_tx_done_isr(uint8_t port, uint16_t n, uint8_t f, uint8_t ep, uint8_t alt) {
    (void)port; (void)f; (void)ep; (void)alt;
    uint32_t start = time_us_32();
    last_mic_tx_us = start;
    if (mic_active && !suspended) {
        if (!n) usb_zlp++;
        else if ((n % 4u) || n < 188u || n > 196u) usb_bad++;
        else {
            usb_mic_packets++;
            usb_mic_frames += n / 4u;
        }
        fill_mic();
    }
    uint32_t elapsed = time_us_32() - start;
    if (elapsed > usb_isr_max_us) usb_isr_max_us = elapsed;
    return true;
}

bool tud_audio_set_itf_cb(uint8_t port, const tusb_control_request_t *r) {
    (void)port;
    uint8_t itf = TU_U16_LOW(r->wIndex);
    uint8_t alt = TU_U16_LOW(r->wValue);
    if ((itf != ITF_NUM_AUDIO_STREAMING_SPK && itf != ITF_NUM_AUDIO_STREAMING_MIC) || alt > 1u) return false;

    uint32_t irq = save_and_disable_interrupts();
    suspended = false;
    if (itf == ITF_NUM_AUDIO_STREAMING_SPK) {
        spk_active = (alt == 1u);
        tud_audio_clear_ep_out_ff();
        dsp_pc_set_active(&mixer, spk_active);
        if (spk_active) spk_starts++;
    } else {
        mic_active = (alt == 1u);
        rx_usb_set_active(&mic, mic_active);
        if (mic_active) {
            mic_starts++;
            last_mic_tx_us = time_us_32();
            tud_audio_clear_ep_in_ff();
            tu_fifo_set_overwritable(tud_audio_get_ep_in_ff(), false);
            fill_mic();
        }
    }
    restore_interrupts(irq);
    return true;
}

bool tud_audio_set_itf_close_ep_cb(uint8_t port, const tusb_control_request_t *r) {
    (void)port;
    uint8_t itf = TU_U16_LOW(r->wIndex);
    uint32_t irq = save_and_disable_interrupts();
    if (itf == ITF_NUM_AUDIO_STREAMING_SPK) {
        spk_active = false;
        dsp_pc_set_active(&mixer, false);
    } else if (itf == ITF_NUM_AUDIO_STREAMING_MIC) {
        mic_active = false;
        rx_usb_set_active(&mic, false);
    }
    restore_interrupts(irq);
    return true;
}

void tud_mount_cb(void) {
    uint32_t irq = save_and_disable_interrupts();
    suspended = false;
    restore_interrupts(irq);
}

void tud_event_hook_cb(uint8_t port, uint32_t event, bool isr) {
    (void)port; (void)isr;
    if (event == DCD_EVENT_BUS_RESET_START || event == DCD_EVENT_BUS_RESET_END) {
        spk_active = false;
        mic_active = false;
        suspended = false;
        dsp_pc_set_active(&mixer, false);
        rx_usb_set_active(&mic, false);
        bus_resets++;
    }
}

void tud_umount_cb(void) {
    uint32_t irq = save_and_disable_interrupts();
    spk_active = false;
    mic_active = false;
    suspended = false;
    dsp_pc_set_active(&mixer, false);
    rx_usb_set_active(&mic, false);
    restore_interrupts(irq);
}

void tud_suspend_cb(bool remote) {
    (void)remote;
    uint32_t irq = save_and_disable_interrupts();
    suspended = true;
    dsp_pc_set_active(&mixer, false);
    rx_usb_set_active(&mic, false);
    restore_interrupts(irq);
}

void tud_resume_cb(void) {
    uint32_t irq = save_and_disable_interrupts();
    suspended = false;
    dsp_pc_set_active(&mixer, spk_active);
    rx_usb_set_active(&mic, mic_active);
    if (mic_active) {
        last_mic_tx_us = time_us_32();
        tud_audio_clear_ep_in_ff();
        fill_mic();
    }
    restore_interrupts(irq);
}

bool tud_audio_get_req_entity_cb(uint8_t port, const tusb_control_request_t *r) {
    uint8_t entity_id = TU_U16_HIGH(r->wIndex);
    uint8_t ctrl_sel = TU_U16_HIGH(r->wValue);
    uint8_t ch = TU_U16_LOW(r->wValue);

    if (entity_id == UAC2_ENTITY_SPK_CLOCK || entity_id == UAC2_ENTITY_MIC_CLOCK) {
        if (ctrl_sel == AUDIO20_CS_CTRL_SAM_FREQ && r->bRequest == AUDIO20_CS_REQ_CUR) {
            audio20_control_cur_4_t curf = { .bCur = 48000 };
            return tud_audio_buffer_and_schedule_control_xfer(port, r, &curf, sizeof(curf));
        }
        if (ctrl_sel == AUDIO20_CS_CTRL_SAM_FREQ && r->bRequest == AUDIO20_CS_REQ_RANGE) {
            audio20_control_range_4_n_t(1) rangef = {
                .wNumSubRanges = 1,
                .subrange[0] = { .bMin = 48000, .bMax = 48000, .bRes = 0 }
            };
            return tud_audio_buffer_and_schedule_control_xfer(port, r, &rangef, sizeof(rangef));
        }
        if (ctrl_sel == AUDIO20_CS_CTRL_CLK_VALID && r->bRequest == AUDIO20_CS_REQ_CUR) {
            audio20_control_cur_1_t valid = { .bCur = 1 };
            return tud_audio_buffer_and_schedule_control_xfer(port, r, &valid, sizeof(valid));
        }
        return false;
    }

    if (entity_id == UAC2_ENTITY_SPK_FEATURE_UNIT || entity_id == UAC2_ENTITY_MIC_FEATURE_UNIT) {
        TU_VERIFY(ch <= 2u);
        bool is_mic = (entity_id == UAC2_ENTITY_MIC_FEATURE_UNIT);
        if (ctrl_sel == AUDIO20_FU_CTRL_MUTE && r->bRequest == AUDIO20_CS_REQ_CUR) {
            uint8_t mask = is_mic ? mic.mute_mask : mixer.params.win_mute_mask;
            audio20_control_cur_1_t m = { .bCur = (int8_t)((mask >> ch) & 1u) };
            return tud_audio_buffer_and_schedule_control_xfer(port, r, &m, sizeof(m));
        }
        if (ctrl_sel == AUDIO20_FU_CTRL_VOLUME) {
            if (r->bRequest == AUDIO20_CS_REQ_RANGE) {
                audio20_control_range_2_n_t(1) rv = {
                    .wNumSubRanges = 1,
                    .subrange[0] = { .bMin = (int16_t)(-60 * 256), .bMax = 0, .bRes = 128 }
                };
                return tud_audio_buffer_and_schedule_control_xfer(port, r, &rv, sizeof(rv));
            }
            if (r->bRequest == AUDIO20_CS_REQ_CUR) {
                audio20_control_cur_2_t cv = { .bCur = is_mic ? mic.vol_q8[ch] : win_vol_q8[ch] };
                return tud_audio_buffer_and_schedule_control_xfer(port, r, &cv, sizeof(cv));
            }
        }
    }
    return false;
}

bool tud_audio_set_req_entity_cb(uint8_t port, const tusb_control_request_t *r, uint8_t *buf) {
    (void)port;
    uint8_t entity_id = TU_U16_HIGH(r->wIndex);
    uint8_t ctrl_sel = TU_U16_HIGH(r->wValue);
    uint8_t ch = TU_U16_LOW(r->wValue);

    if (entity_id == UAC2_ENTITY_SPK_CLOCK || entity_id == UAC2_ENTITY_MIC_CLOCK) {
        if (ctrl_sel == AUDIO20_CS_CTRL_SAM_FREQ && r->bRequest == AUDIO20_CS_REQ_CUR && r->wLength == 4u) {
            uint32_t rate = (uint32_t)((const audio20_control_cur_4_t *)buf)->bCur;
            return rate == 48000u;
        }
        return false;
    }

    if (entity_id == UAC2_ENTITY_SPK_FEATURE_UNIT || entity_id == UAC2_ENTITY_MIC_FEATURE_UNIT) {
        TU_VERIFY(ch <= 2u && r->bRequest == AUDIO20_CS_REQ_CUR);
        bool is_mic = (entity_id == UAC2_ENTITY_MIC_FEATURE_UNIT);
        if (ctrl_sel == AUDIO20_FU_CTRL_MUTE) {
            TU_VERIFY(r->wLength == 1u);
            if (is_mic) rx_usb_set_mute(&mic, ch, buf[0] != 0u);
            else dsp_mixer_set_win_mute(&mixer, ch, buf[0] != 0u);
            return true;
        }
        if (ctrl_sel == AUDIO20_FU_CTRL_VOLUME) {
            TU_VERIFY(r->wLength == 2u);
            int16_t vq8 = ((const audio20_control_cur_2_t *)buf)->bCur;
            if (vq8 < -15360) vq8 = -15360;
            if (vq8 > 0) vq8 = 0;
            if (is_mic) {
                rx_usb_set_vol_q8(&mic, ch, vq8);
            } else {
                win_vol_q8[ch] = vq8;
                dsp_mixer_set_win_vol_q8(&mixer, ch, vq8);
            }
            return true;
        }
    }
    return false;
}

static bool float_sane(float x) {
    return (x == x) && (x > -20.0f && x < 20.0f);
}

static bool biquad_stable(const dsp_biquad_coeff_t *c) {
    if (!float_sane(c->b0) || !float_sane(c->b1) || !float_sane(c->b2) ||
        !float_sane(c->a1) || !float_sane(c->a2)) return false;
    if (!(c->a2 > -1.0f && c->a2 < 1.0f)) return false;
    float abs_a1 = c->a1 < 0.0f ? -c->a1 : c->a1;
    return (1.0f + c->a2) > abs_a1;
}

uint16_t tud_hid_get_report_cb(uint8_t itf, uint8_t id, hid_report_type_t type, uint8_t *out, uint16_t len) {
    (void)itf;
    if (id || type != HID_REPORT_TYPE_FEATURE) return 0;
    uint8_t b[63] = {0};
    memcpy(b, "UAP1", 4);
    b[4] = 1u;
    b[5] = (uint8_t)hid_page;
    uint32_t irq = save_and_disable_interrupts();
    b[6] = (uint8_t)((mic_active ? 1u : 0u) |
                     (spk_active ? 2u : 0u) |
                     (mixer.sw_primed ? 4u : 0u) |
                     (mixer.pc_primed ? 8u : 0u) |
                     (parser.locked ? 16u : 0u) |
                     (mixer.test_tone_enabled ? 32u : 0u) |
                     (flash_loaded ? 64u : 0u));
    b[7] = (uint8_t)mode;
    uint32_t v[13] = {0};
    if (hid_page == 0u) {
        uint32_t p[13] = {
            selftest_line, baud, parser.audio, parser.status, parser.crc, parser.bad,
            uart_errors, dma_overrun, loop_max_us, usb_isr_max_us, i2s_isr_max_us,
            active_rx_pin, rx_pin_switches
        };
        memcpy(v, p, sizeof(v));
    } else if (hid_page == 1u) {
        uint32_t p[13] = {
            mic.received, mic.consumed, rx_usb_level(&mic), mic.high, mic.low,
            mic.underflow, mic.overflow, mic.missing, mic.small, mic.normal,
            mic.large, usb_mic_packets, mic_recoveries
        };
        memcpy(v, p, sizeof(v));
    } else if (hid_page == 2u) {
        uint32_t p[13] = {
            dsp_sw_level(&mixer), mixer.sw_underflows, mixer.sw_overflows,
            dsp_pc_level(&mixer), mixer.pc_underflows, mixer.pc_overflows,
            pc_usb_packets, mixer.i2s_blocks, mixer.clip_count,
            pio_cur_div_256, mixer.pc_step_q16, spk_starts, bus_resets
        };
        memcpy(v, p, sizeof(v));
    } else {
        uint32_t p[13] = {
            mixer.params.eq_enabled ? 1u : 0u,
            (uint32_t)mixer.params.game_db_x100,
            (uint32_t)mixer.params.master_db_x100,
            (uint32_t)mixer.params.pc_trim_db_x100,
            (uint32_t)mixer.params.win_vol_db_x100[0],
            (uint32_t)mixer.params.win_vol_db_x100[1],
            (uint32_t)mixer.params.win_vol_db_x100[2],
            mixer.params.win_mute_mask,
            mixer.params.rx_pin_mode,
            flash_loaded ? 1u : 0u,
            flash_saves,
            source_drops,
            source_state
        };
        memcpy(v, p, sizeof(v));
    }
    b[60] = active_rx_pin;
    b[61] = mixer.params.eq_enabled ? 1u : 0u;
    b[62] = mixer.params.rx_pin_mode;
    restore_interrupts(irq);

    for (unsigned i = 0; i < 13u; i++) put32(b + 8u + i * 4u, v[i]);
    len = TU_MIN(len, (uint16_t)sizeof(b));
    memcpy(out, b, len);
    return len;
}

void tud_hid_set_report_cb(uint8_t itf, uint8_t id, hid_report_type_t type, const uint8_t *b, uint16_t len) {
    (void)itf;
    if (id || type != HID_REPORT_TYPE_FEATURE || len < 2u) return;

    if (b[0] == 0x10u && b[1] <= 3u) {
        hid_page = b[1];
    } else if (b[0] == 0x20u && b[1] <= 1u) {
        mode = b[1];
        dsp_mixer_set_test_tone(&mixer, mode == 1u);
    } else if (b[0] == 0x40u && len >= 9u) {
        dsp_params_t np = mixer.params;
        np.eq_enabled = (b[1] != 0u);
        int32_t g = get_i16le(b + 2);
        int32_t mast = get_i16le(b + 4);
        int32_t pc = get_i16le(b + 6);
        uint8_t pm = b[8];
        if (g < -6000) g = -6000;
        if (g > 1200) g = 1200;
        if (mast < -6000) mast = -6000;
        if (mast > 600) mast = 600;
        if (pc < -2400) pc = -2400;
        if (pc > 1200) pc = 1200;
        if (pm != 0u && pm != 1u && pm != 13u) pm = np.rx_pin_mode;
        np.game_db_x100 = g;
        np.master_db_x100 = mast;
        np.pc_trim_db_x100 = pc;
        np.rx_pin_mode = pm;
        dsp_mixer_set_params(&mixer, &np);
        if ((pm == 1u || pm == 13u) && pm != active_rx_pin) {
            apply_uart_rx_pin(pm);
        }
    } else if (b[0] == 0x42u && len >= 22u && b[1] < DSP_EQ_BANDS) {
        dsp_biquad_coeff_t c = {
            .b0 = get_f32le(b + 2),
            .b1 = get_f32le(b + 6),
            .b2 = get_f32le(b + 10),
            .a1 = get_f32le(b + 14),
            .a2 = get_f32le(b + 18)
        };
        if (biquad_stable(&c)) {
            dsp_eq_set_band(&mixer.eq, b[1], &c);
        }
    } else if (b[0] == 0x43u && len >= 5u) {
        float pre = get_f32le(b + 1);
        if (float_sane(pre) && pre >= 0.01f && pre <= 4.0f) {
            dsp_eq_set_preamp(&mixer.eq, pre);
        }
    } else if (b[0] == 0x50u && b[1] == 0xa5u) {
        save_requested = true;
    } else if (b[0] == 0x51u && b[1] == 0xa5u) {
        reset_requested = true;
    } else if (b[0] == 0x7fu) {
        gpio_put(PIN_AUDIO_MUTE, 0);
        boot_requested = time_us_32();
    }
}

int main(void) {
    // Keep headphone amp cleanly muted during initialization
    gpio_init(PIN_AUDIO_MUTE);
    gpio_set_dir(PIN_AUDIO_MUTE, GPIO_OUT);
    gpio_put(PIN_AUDIO_MUTE, 0);

    selftest_line = pack_selftest_run(&mixer, &mic);
    dsp_mixer_init(&mixer);
    rx_usb_init(&mic);
    rx_parser_reset(&parser);
    flash_loaded = flash_cfg_load(&mixer);
    if (mixer.params.rx_pin_mode == 1u) active_rx_pin = UART_RX_PIN_ALT;
    else active_rx_pin = UART_RX_PIN_PRIMARY;

    // Configure PIO0 SM0 for 48 kHz 32-bit 64fs Philips I2S
    pio_offset = pio_add_program(pio0, &i2s_out_32_program);
    pio_gpio_init(pio0, PIN_I2S_DATA);
    pio_gpio_init(pio0, PIN_I2S_BCK);
    pio_gpio_init(pio0, PIN_I2S_LRCK);
    pio_sm_set_consecutive_pindirs(pio0, 0, PIN_I2S_DATA, 1, true);
    pio_sm_set_consecutive_pindirs(pio0, 0, PIN_I2S_BCK, 2, true);

    uint64_t target_hz = 48000ULL * 128ULL;
    uint32_t sys_hz = clock_get_hz(clk_sys);
    pio_base_div_256 = (uint32_t)(((uint64_t)sys_hz * 256ULL + (target_hz / 2ULL)) / target_hz);
    pio_cur_div_256 = pio_base_div_256;

    dma_i2s_a = dma_claim_unused_channel(true);
    dma_i2s_b = dma_claim_unused_channel(true);
    irq_set_exclusive_handler(DMA_IRQ_0, i2s_dma_irq0_handler);
    irq_set_priority(DMA_IRQ_0, 0xC0); // Yield to USBCTRL_IRQ (0x80)
    i2s_configure_and_start();

    // Unmute PAM8908 after I2S clocks stabilize
    sleep_ms(20);
    gpio_put(PIN_AUDIO_MUTE, 1);

    // Initialize UART0 at 3 Mbps with endless RX DMA
    baud = uart_init(uart0, 3000000);
    uart_set_format(uart0, 8, 1, UART_PARITY_NONE);
    uart_set_hw_flow(uart0, false, false);
    uart_set_fifo_enabled(uart0, true);
    uart_dma_ch = dma_claim_unused_channel(true);
    apply_uart_rx_pin(active_rx_pin);
    dsp_sw_set_active(&mixer, true);

    const tusb_rhport_init_t init = { .role = TUSB_ROLE_DEVICE, .speed = TUSB_SPEED_FULL };
    tusb_init(0, &init);

    uint32_t now0 = time_us_32();
    loop_last = now0;
    last_servo_us = now0;
    last_rx_activity_us = now0;
    last_rx_byte_us = now0;

    while (true) {
        uint32_t now = time_us_32();
        uint32_t gap = now - loop_last;
        loop_last = now;
        if (gap > loop_max_us) loop_max_us = gap;

        uart_poll();
        pio_clock_servo_poll(now);
        tud_task();

        if (tud_mounted() && mic_active && !suspended && (uint32_t)(now - last_mic_tx_us) > 5000u) {
            uint32_t irq = save_and_disable_interrupts();
            tu_fifo_t *ff = tud_audio_get_ep_in_ff();
            if (mic_active && !suspended && (uint32_t)(time_us_32() - last_mic_tx_us) > 5000u &&
                ff != NULL && tu_fifo_count(ff) == 0u) {
                tud_audio_clear_ep_in_ff();
                fill_mic();
                last_mic_tx_us = time_us_32();
                mic_recoveries++;
            }
            restore_interrupts(irq);
        }

        if (reset_requested) {
            reset_requested = false;
            uint32_t irq = save_and_disable_interrupts();
            int32_t wvol0 = mixer.params.win_vol_db_x100[0];
            int32_t wvol1 = mixer.params.win_vol_db_x100[1];
            int32_t wvol2 = mixer.params.win_vol_db_x100[2];
            uint8_t wmute = mixer.params.win_mute_mask;
            dsp_eq_init_flat(&mixer.eq, false);
            mixer.params.game_db_x100 = 0;
            mixer.params.master_db_x100 = -4000;
            mixer.params.pc_trim_db_x100 = 0;
            mixer.params.win_vol_db_x100[0] = wvol0;
            mixer.params.win_vol_db_x100[1] = wvol1;
            mixer.params.win_vol_db_x100[2] = wvol2;
            mixer.params.win_mute_mask = wmute;
            mixer.params.eq_enabled = false;
            mixer.params.rx_pin_mode = 0u;
            mixer.eq.reset_pending = true;
            restore_interrupts(irq);
            dsp_mixer_recompute_gains(&mixer);
        }

        if (save_requested) {
            save_requested = false;
            if (flash_cfg_save(&mixer, pause_audio_for_flash, resume_audio_after_flash)) {
                flash_saves++;
                flash_loaded = true;
            }
        }

        if (boot_requested && (uint32_t)(time_us_32() - boot_requested) > 25000u) {
            reset_usb_boot(0, 0);
        }
    }
}
