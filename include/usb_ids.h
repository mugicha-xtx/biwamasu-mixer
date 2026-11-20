// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
#pragma once

/*
 * Authorized USB Vendor ID / Product ID configuration.
 *
 * Leave these as 0x0000 until you receive officially allocated open-source PIDs
 * (for example via raspberrypi/usb-pid or pid.codes).
 * Once allocated, replace the three 0x0000 values below with your assigned hex IDs.
 */
#ifndef USB_VID
#define USB_VID         0x0000u
#endif

#ifndef BRIDGE_USB_PID
#define BRIDGE_USB_PID  0x0000u
#endif

#ifndef MIXER_USB_PID
#define MIXER_USB_PID   0x0000u
#endif

#if (USB_VID == 0x0000u) || (BRIDGE_USB_PID == 0x0000u) || (MIXER_USB_PID == 0x0000u)
#  if defined(ALLOW_DEV_USB_IDS) && (ALLOW_DEV_USB_IDS != 0)
#    undef USB_VID
#    undef BRIDGE_USB_PID
#    undef MIXER_USB_PID
#    define USB_VID         0xcafeu
#    define BRIDGE_USB_PID  0x401du
#    define MIXER_USB_PID   0x4026u
#  else
#    error "Authorized USB VID/PID not configured yet. Set USB_VID, BRIDGE_USB_PID, and MIXER_USB_PID in include/usb_ids.h after official PID allocation (or pass -DevTestIds to scripts/Build-Release.ps1 for local testing only)."
#  endif
#endif

#if (USB_VID == 0xcafeu) && (!defined(ALLOW_DEV_USB_IDS) || (ALLOW_DEV_USB_IDS == 0))
#  error "Development test VID (0xCAFE) requires ALLOW_DEV_USB_IDS=1 (-DevTestIds) and must never be used for public release binaries."
#endif

#if (BRIDGE_USB_PID == MIXER_USB_PID)
#  error "BRIDGE_USB_PID and MIXER_USB_PID must be two distinct USB Product IDs."
#endif
