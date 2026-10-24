// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Placeholder so the kerboscript compile check has something to compile until the
// Core Library's Building Blocks land here.

@lazyglobal off.

function claudia_placeholder {
  parameter greeting is "Claudia Kermin".
  local message is greeting + " placeholder".
  if message:length > 0 {
    print message.
  }
  return lexicon("ok", true, "message", message).
}

local result is claudia_placeholder().
wait 0.
