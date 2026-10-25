// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block coast: holds a Mission Script until a UT, warping on rails with claudia_warp_to,
// so a sequence waits inside itself for a window, daylight or a few orbits (ADR 0004: no wait cap,
// no Due Time for it). Built on claudia_lib.ks; block-only helpers are prefixed coast_. The "//|"
// lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| coast(options): holds the craft on its path until a UT, warping on rails and back at 1x lead
//| seconds before it, so whatever the script does next (a burn, a landing) starts on time. Use it for
//| any wait the other blocks don't make themselves: a hold of a few orbits, a phase angle, a set time.
//| Options:
//|   until [required]: the UT (seconds) to hold until, e.g. time:seconds + 3 * ship:orbit:period. A
//|     UT already past returns at once.
//|   lead [30]: seconds before until to be back at 1x; never less than 30.
//|   max_duration ["auto"]: the wait plus 600 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| It doesn't steer or burn: the craft keeps the attitude it holds. Warp drops to 1x when the Mission
//| needs the player and resumes after. Extra results: coasted (s held).
//| Fails with "until is required", "until must be a UT in seconds", "lead must be seconds", or an abort.
//| Example: `local res is coast(lexicon("until", time:seconds + 3 * ship:orbit:period)). if res:success { add node(time:seconds + 300, 0, 0, -40). set res to exec_node(). } addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function coast_defaults {
  return lexicon("until", "required", "lead", 30).
}

// "" or why an option value isn't one the block can use.
function coast_unreadable {
  parameter opt.
  if opt["until"]:istype("String") {
    if opt["until"] = "required" {
      return "until is required".
    }
    return "until must be a UT in seconds".
  }
  if not opt["until"]:istype("Scalar") {
    return "until must be a UT in seconds".
  }
  if not opt:lead:istype("Scalar") or opt:lead < 0 {
    return "lead must be seconds".
  }
  return "".
}

function coast {
  parameter options is lexicon().
  local extra is lexicon("coasted", 0).
  local ctx is claudia_begin("coast", options, coast_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is coast_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local end_ut is opt["until"].
  if opt:max_duration:istype("String") {
    set opt:max_duration to max(0, end_ut - time:seconds) + 600.
  }
  if end_ut > time:seconds {
    local waited is claudia_warp_to(ctx, end_ut, opt:lead).
    if waited <> "" {
      claudia_make_safe().
      set extra:coasted to round(time:seconds - ctx:start_ut, 1).
      return claudia_finish(ctx, false, waited, extra).
    }
  }
  set extra:coasted to round(time:seconds - ctx:start_ut, 1).
  return claudia_finish(ctx, true, "", extra).
}
