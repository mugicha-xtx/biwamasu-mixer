# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
cmake_minimum_required(VERSION 3.17)
set(PICO_BOARD pico2 CACHE STRING "Pico 2 H (non-wireless)")
if(NOT PICO_SDK_PATH)
  set(PICO_SDK_PATH "${CMAKE_CURRENT_LIST_DIR}/../../.deps/pico-sdk" CACHE PATH "Pinned SDK")
endif()
if(NOT PICO_TINYUSB_PATH)
  set(PICO_TINYUSB_PATH "${CMAKE_CURRENT_LIST_DIR}/../../.deps/tinyusb" CACHE PATH "Pinned TinyUSB")
endif()
include(${PICO_SDK_PATH}/external/pico_sdk_import.cmake)
project(pico_audio_pack C CXX ASM)
set(CMAKE_C_STANDARD 11)
pico_sdk_init()

add_executable(pico_audio_pack main.c dsp_core.c rx_usb.c flash_cfg.c selftest_pack.c usb_descriptors.c)
pico_generate_pio_header(pico_audio_pack ${CMAKE_CURRENT_LIST_DIR}/i2s_out.pio)
target_include_directories(pico_audio_pack PRIVATE
  ${CMAKE_CURRENT_LIST_DIR}
  ${CMAKE_CURRENT_LIST_DIR}/../../include
)
foreach(def USB_VID BRIDGE_USB_PID MIXER_USB_PID ALLOW_DEV_USB_IDS)
  if(DEFINED ${def} AND NOT "${${def}}" STREQUAL "")
    target_compile_definitions(pico_audio_pack PRIVATE ${def}=${${def}})
  endif()
endforeach()
target_compile_options(pico_audio_pack PRIVATE -Wall -Wextra -Werror)
target_link_libraries(pico_audio_pack
  pico_stdlib
  pico_unique_id
  hardware_pio
  hardware_dma
  hardware_uart
  hardware_flash
  hardware_irq
  hardware_clocks
  hardware_sync
  tinyusb_device
)
pico_enable_stdio_usb(pico_audio_pack 0)
pico_enable_stdio_uart(pico_audio_pack 0)
pico_add_extra_outputs(pico_audio_pack)
