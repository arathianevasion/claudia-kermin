// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block science: runs the craft's experiments where it is and sends or keeps the data.
// Built on claudia_lib.ks; block-only helpers are prefixed science_. The "//|" lines are Claudia's
// docs for it; the first is its summary.

@lazyglobal off.

//| science(options): runs the craft's science experiments here (current body, situation and biome)
//| and transmits or keeps the data. Never overwrites data an experiment already holds, and never
//| runs a part that is inoperable or has nothing to measure here.
//| Options:
//|   experiments ["all_useful"]: "all_useful" runs every experiment that has science left here (the
//|     addon knows how much); in Sandbox nothing is tracked, so it runs each experiment once. Or a
//|     list of experiment ids ("temperatureScan") or part titles/names, run whether or not science
//|     is left. A name that matches no experiment part fails the block.
//|   transmit ["auto"]: "keep" leaves the data in the experiment; "transmit" sends it home (it stays
//|     if the craft has no antenna or no link); "auto" transmits unless the craft is returning
//|     (it carries crew or parachutes) and transmitting would lose value, then keeps it.
//|   max_duration [1800]: seconds, transmission waits included.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block (this block never stages: it holds no burn, so auto-staging never fires).
//|   Data is never transmitted while electric charge is under min_ec_pct; it is kept.
//| Extra results: results (a list with one lexicon per experiment part: experiment, part, action
//| "ran" or "skipped", detail, and transmit "transmitted", "kept" or "" when it did not run).
//| Data left on board after "keep" comes home with the craft; recover it. Fails on an option it can't
//| read, a name matching no experiment, or an abort. Nothing to run is a success with every
//| experiment skipped. EVA reports and samples are the player's job.
//| Example: `local r is science(lexicon("transmit", "keep")). addons:claudia:report(r).`

// The block's own options and defaults.
function science_defaults {
  return lexicon("experiments", "all_useful", "transmit", "auto").
}

// "" or why an option isn't one the block knows.
function science_unreadable {
  parameter opt.
  if opt:transmit <> "keep" and opt:transmit <> "transmit" and opt:transmit <> "auto" {
    return "transmit must be 'keep', 'transmit' or 'auto', not '" + opt:transmit + "'".
  }
  if opt:experiments:istype("String") {
    if opt:experiments = "" {
      return "experiments must be 'all_useful' or a list of names".
    }
  } else if not opt:experiments:istype("List") {
    return "experiments must be 'all_useful' or a list of names".
  }
  return "".
}

// The chosen experiments as a list: empty for "all_useful".
function science_names {
  parameter experiments.
  if experiments:istype("List") {
    return experiments.
  }
  if experiments = "all_useful" {
    return list().
  }
  return list(experiments).
}

// True if any of names matches the experiment id, part title or part name.
function science_matches {
  parameter names, id, title, part_name.
  for n in names {
    if n = id or n = title or n = part_name {
      return true.
    }
  }
  return false.
}

// "run" or "skip: <why>" for one experiment part. explicit: it was named; has_data/rerunnable/
// inoperable: the module's state; available: the experiment can run in this situation; tracked:
// the game tracks science (not Sandbox); left: science still to gain here.
function science_decision {
  parameter explicit, rerunnable, has_data, inoperable, available, tracked, left.
  if inoperable {
    return "skip: inoperable".
  }
  if has_data {
    if rerunnable {
      return "skip: holds data, so it was not overwritten".
    }
    return "skip: holds data and is not rerunnable, so it was not overwritten".
  }
  if not available {
    return "skip: nothing to measure in this situation".
  }
  if not explicit and tracked and left <= 0.05 {
    return "skip: no science left here".
  }
  return "run".
}

// "transmit" or "keep: <why>" for fresh data. returning: the craft is coming home; loses_value:
// transmitting is worth less than the data in hand; can_send: an antenna with a link; ec_pct and
// min_ec_pct: electric charge left and the least to transmit at.
function science_transmit_choice {
  parameter mode, returning, loses_value, can_send, ec_pct, min_ec_pct.
  if mode = "keep" {
    return "keep: transmit is 'keep'".
  }
  if mode = "auto" and returning and loses_value {
    return "keep: the craft is returning and transmitting would lose value".
  }
  if not can_send {
    return "keep: no antenna or no link".
  }
  if ec_pct < min_ec_pct {
    return "keep: electric charge is under min_ec_pct".
  }
  return "transmit".
}

// The craft comes home: it carries crew or parachutes.
function science_returning {
  if ship:crew:length > 0 {
    return true.
  }
  for p in ship:parts {
    if p:hasmodule("ModuleParachute") {
      return true.
    }
  }
  return false.
}

function science_can_send {
  if not homeconnection:isconnected {
    return false.
  }
  for p in ship:parts {
    if p:hasmodule("ModuleDataTransmitter") {
      return true.
    }
  }
  return false.
}

// True if sending the data in module m would be worth less than keeping it.
function science_loses_value {
  parameter m.
  for d in m:data {
    if d:transmitvalue < d:sciencevalue {
      return true.
    }
  }
  return false.
}

// Waits up to limit s for check to be false; here, for the module to hold (want true) or lose
// (want false) its data. Returns "" or an abort reason.
function science_wait_data {
  parameter ctx, m, want, limit.
  local until_ut is time:seconds + limit.
  until m:hasdata = want or time:seconds > until_ut {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      return ticked.
    }
    wait 0.
  }
  return "".
}

function science {
  parameter options is lexicon().
  local extra is lexicon("results", list()).
  local ctx is claudia_begin("science", options, science_defaults(), 1800).
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is science_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }

  local names is science_names(opt:experiments).
  local modules is ship:modulesnamed("ModuleScienceExperiment").
  local matched is list().
  for n in names {
    local found is false.
    for m in modules {
      local info is addons:claudia:science(m:part).
      if science_matches(list(n), info:experiment, m:part:title, m:part:name) {
        set found to true.
      }
    }
    if not found {
      return claudia_finish(ctx, false, "no experiment named " + n, extra).
    }
  }

  for m in modules {
    local info is addons:claudia:science(m:part).
    local entry is lexicon("experiment", info:experiment, "part", m:part:title, "action", "skipped", "detail", "", "transmit", "").
    local explicit is names:length > 0 and science_matches(names, info:experiment, m:part:title, m:part:name).
    extra:results:add(entry).
    if names:length > 0 and not explicit {
      set entry:detail to "not in experiments".
    } else {
      local decision is science_decision(explicit, m:rerunnable, m:hasdata, m:inoperable, info:available, info:tracked, info:left).
      if decision <> "run" {
        set entry:detail to decision:substring(6, decision:length - 6).
      } else {
        m:deploy().
        local ran is science_wait_data(ctx, m, true, 15).
        if ran <> "" {
          claudia_make_safe().
          return claudia_finish(ctx, false, ran, extra).
        }
        if not m:hasdata {
          set entry:detail to "the experiment gave no data".
        } else {
          set entry:action to "ran".
          set entry:detail to info:situation + " at " + info:biome.
          local choice is science_transmit_choice(opt:transmit, science_returning(), science_loses_value(m), science_can_send(), claudia_ec_pct(), opt:min_ec_pct).
          if choice = "transmit" {
            m:transmit().
            local sent is science_wait_data(ctx, m, false, 120).
            if sent <> "" {
              claudia_make_safe().
              return claudia_finish(ctx, false, sent, extra).
            }
            if m:hasdata {
              set entry:transmit to "kept".
              set entry:detail to entry:detail + "; transmission did not finish, data kept".
            } else {
              set entry:transmit to "transmitted".
            }
          } else {
            set entry:transmit to "kept".
            set entry:detail to entry:detail + "; " + choice:substring(6, choice:length - 6).
          }
        }
      }
    }
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
  }
  return claudia_finish(ctx, true, "", extra).
}
