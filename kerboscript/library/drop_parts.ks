// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block drop_parts: fires the decouplers that hold the named parts, now. Built on
// claudia_lib.ks (the same part matching and decoupler firing as the drop_empty option); block-only
// helpers are prefixed drop_parts_. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| drop_parts(options): drops the named parts now, by firing the decoupler that holds each, and
//| reports what it dropped. Use it to shed a spent booster before a landing, with no hand staging.
//| Options:
//|   parts [required]: a list of part names or tags (e.g. list("radialTank", "boosters")), or one name.
//|     Each must match a part on the craft.
//|   The common options are accepted but do nothing here: it never burns, stages or waits, and its own
//|     drops are not part loss. It ignores stage_limit: you named the parts, so they go.
//| It drops whatever the decoupler holds, fuel or not, so name only what you mean to lose; use the
//| drop_empty option on a burning block to drop tanks only once they are empty. It refuses a part with
//| no decoupler, and one whose drop would take the part running the script or the root part, and
//| drops nothing in that case. Extra results: dropped (the names of every part that left),
//| parts_dropped (how many).
//| Fails with "parts is required", "parts must be a list of part names or tags", "no part matches
//| <name>", "<name> has no decoupler", "<name> would drop the part running the script", or
//| "nothing separated: the decouplers did not fire".
//| Tool: stage_performance for the delta-v left once the parts are gone.
//| Example: `local res is drop_parts(lexicon("parts", list("boosters"))). addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function drop_parts_defaults {
  return lexicon("parts", "required").
}

// The wanted names as a list, or the reason the option can't be used (a String).
function drop_parts_names {
  parameter parts_opt.
  if parts_opt:istype("String") {
    if parts_opt = "required" {
      return "parts is required".
    }
    if parts_opt = "" {
      return "parts must be a list of part names or tags".
    }
    return list(parts_opt).
  }
  if not parts_opt:istype("List") or parts_opt:length = 0 {
    return "parts must be a list of part names or tags".
  }
  return parts_opt.
}

function drop_parts {
  parameter options is lexicon().
  local extra is lexicon("parts_dropped", 0).
  local ctx is claudia_begin("drop_parts", options, drop_parts_defaults(), 60).
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local names is drop_parts_names(ctx:opt:parts).
  if names:istype("String") {
    return claudia_finish(ctx, false, names, extra).
  }
  // Work out every decoupler to fire before firing any, so a name that can't be dropped drops nothing.
  local plan is claudia_drop_plan(names).
  if plan:error <> "" {
    return claudia_finish(ctx, false, plan:error, extra).
  }
  local before is ship:parts:length.
  local not_fired is claudia_fire_drop(ctx, plan).
  set extra:parts_dropped to before - ship:parts:length.
  if not_fired <> "" {
    return claudia_finish(ctx, false, not_fired, extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
