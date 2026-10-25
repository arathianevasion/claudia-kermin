// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block transfer: flies from orbit around a body to an encounter with one of its moons,
// planning the ejection with RSVP v9 (bundled, kerboscript/rsvp/NOTE.md). Built on claudia_lib.ks
// and exec_node.ks; block-only helpers are prefixed transfer_. return_to_kerbin shares the
// mid-course correction helpers here. The "//|" lines are Claudia's docs for it; the first is its
// summary.

@lazyglobal off.

// RSVP's lexicon of functions, loaded on first use: a bundled file's top-level statements would
// only run after the Mission Script ends, so RSVP's main is a function called here.
function transfer_rsvp {
  if not (defined rsvp) {
    rsvp_file_main().
  }
  return rsvp.
}
