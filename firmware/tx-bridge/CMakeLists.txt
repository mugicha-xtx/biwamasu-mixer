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
project(switch2_usb_bridge C CXX ASM)
set(CMAKE_C_STANDARD 11)
pico_sdk_init()

add_executable(switch2_usb_bridge bridge_main.c bridge_core.c usb_descriptors.c)
target_include_directories(switch2_usb_bridge PRIVATE
  ${CMAKE_CURRENT_LIST_DIR}
  ${CMAKE_CURRENT_LIST_DIR}/../../include
)
target_compile_definitions(switch2_usb_bridge PRIVATE PROBE_HID=1 PICO_STACK_SIZE=0x1000)
foreach(def USB_VID BRIDGE_USB_PID MIXER_USB_PID ALLOW_DEV_USB_IDS)
  if(DEFINED ${def} AND NOT "${${def}}" STREQUAL "")
    target_compile_definitions(switch2_usb_bridge PRIVATE ${def}=${${def}})
  endif()
endforeach()
target_compile_options(switch2_usb_bridge PRIVATE -Wall -Wextra -Werror)
target_link_libraries(switch2_usb_bridge
  pico_stdlib
  pico_unique_id
  pico_rand
  hardware_uart
  hardware_dma
  tinyusb_device
)
pico_enable_stdio_usb(switch2_usb_bridge 0)
pico_enable_stdio_uart(switch2_usb_bridge 0)
pico_add_extra_outputs(switch2_usb_bridge)
