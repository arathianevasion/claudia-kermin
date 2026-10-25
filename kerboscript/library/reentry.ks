// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block reentry: brings a capsule down through an atmosphere. Built on claudia_lib.ks;
// block-only helpers are prefixed reentry_. The "//|" lines are Claudia's docs for it; the first is
// its summary.

@lazyglobal off.

//| reentry(options): brings the craft down through the atmosphere of its body: waits (on rails) for
//| the atmosphere if it is still in space, holds the heat shield forward (surface retrograde), drops
//| the heat shield once the worst of the heating is over, opens the parachutes only when it is safe
//| (never staged, never fired by hand) and ends once the craft is landed or splashed.
//| Options:
//|   chute_mode ["safe"]: "safe" opens the parachutes in two stages: drogue chutes first (under 500 m/s,
//|     over 0.02 atm, or at the latest 3000 m above the ground), then the main parachutes once the drogues
//|     are out (under 250 m/s, over 0.04 atm, or at the latest 1500 m above the ground); a craft with no
//|     drogues opens its mains alone. "none" never opens them (for a craft that lands another way).
//|   jettison_stage ["auto"]: the stage number whose decouplers drop the heat shield, "auto" for the
//|     decoupler on or beside the heat shield (just above the capsule), or "none" to keep it.
//|     The decouplers are fired by themselves; other parts of that stage are not staged.
//|   hold ["retrograde"]: "retrograde" points the craft's forward axis at surface retrograde, so a
//|     pod's heat shield leads; "none" leaves the attitude alone.
//|   max_duration ["auto"]: the wait for the atmosphere plus 1800 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block (this block never stages: it holds no burn, so auto-staging never fires).
//| Extra results: max_g (peak g felt, in g), max_temp_pct (hottest part, in percent of its limit),
//| geo (lexicon lat, lng, alt where it ended), splashed (true if it ended in water), touchdown_speed
//| (m/s just before contact; over 15 m/s fails the block). Fails with "body has no atmosphere", an
//| orbit that never reaches the atmosphere, an option it can't read, a hard
//| touchdown, or an abort (a mid-air abort still opens the parachutes if that is safe).
//| Fly it after a return or a de-orbit burn, with the orbit's periapsis inside the atmosphere. It does
//| not plan or burn anything. Example: `local res is reentry(). addons:claudia:report(res).`

// Main parachutes: the most speed (m/s) and least air pressure (atm) at which opening is safe, and the
// height above the ground (m) by which they open regardless. That last resort is not speed-aware on
// purpose: below 1500 m there is no time left to slow down, and an unopened chute is certain death where
// an early one is only a risk. The drogues are staged before the mains, so by then the craft has been
// slowing on them since 3000 m.
function reentry_chute_max_speed { return 250. }
function reentry_chute_min_pressure { return 0.04. }
function reentry_chute_last_agl { return 1500. }
// Drogue chutes are built for the fast part of the descent: higher speed limit, thinner air, and the
// height by which they open whatever the speed (the one chute that can take it, so the cheapest last resort).
function reentry_drogue_max_speed { return 500. }
function reentry_drogue_min_pressure { return 0.02. }
function reentry_drogue_last_agl { return 3000. }
// The heat shield goes at or below this speed (m/s), or by this height above the ground (m). The height
// is not speed-aware on purpose: at 3000 m the air is thick enough that a falling capsule is well under
// the speed at which it heats, so the shield is only dead weight over the chutes, and waiting on a speed
// that never comes would keep it on to the ground.
function reentry_shield_speed { return 400. }
function reentry_shield_last_agl { return 3000. }
// Touchdown faster than this (m/s) fails the block.
function reentry_touchdown_limit { return 15. }

// The block's own options and defaults (max_duration "auto" is resolved once the entry is known).
function reentry_defaults {
  return lexicon("chute_mode", "safe", "jettison_stage", "auto", "hold", "retrograde").
}

// "" or why an option isn't one the block knows.
function reentry_unreadable {
  parameter opt.
  if opt:chute_mode <> "safe" and opt:chute_mode <> "none" {
    return "chute_mode must be 'safe' or 'none', not '" + opt:chute_mode + "'".
  }
  if opt:hold <> "retrograde" and opt:hold <> "none" {
    return "hold must be 'retrograde' or 'none', not '" + opt:hold + "'".
  }
  if opt:jettison_stage:istype("String") {
    if opt:jettison_stage <> "auto" and opt:jettison_stage <> "none" {
      return "jettison_stage must be 'auto', 'none' or a stage number, not '" + opt:jettison_stage + "'".
    }
  }
  return "".
}

// What to do about the parachutes now: "deploy" or "wait". mode: "safe" or "none"; out: they are
// already open; speed in m/s and agl (height above the ground) in m; pressure in atm.
function reentry_chute_action {
  parameter mode, out, speed, agl, pressure.
  if mode = "none" or out {
    return "wait".
  }
  if agl <= reentry_chute_last_agl() {
    return "deploy".
  }
  if speed <= reentry_chute_max_speed() and pressure >= reentry_chute_min_pressure() {
    return "deploy".
  }
  return "wait".
}

// What to do about the drogue chutes now: "deploy" or "wait". Same arguments as reentry_chute_action.
function reentry_drogue_action {
  parameter mode, out, speed, agl, pressure.
  if mode = "none" or out {
    return "wait".
  }
  if agl <= reentry_drogue_last_agl() {
    return "deploy".
  }
  if speed <= reentry_drogue_max_speed() and pressure >= reentry_drogue_min_pressure() {
    return "deploy".
  }
  return "wait".
}

// Which parachutes to open now: "drogues", "mains" or "wait". Drogues go before mains: the mains wait
// for the drogues to be out (or for there to be none). has_drogues: the craft carries drogue chutes;
// drogues_out and mains_out: those are already open.
function reentry_chute_stage {
  parameter mode, has_drogues, drogues_out, mains_out, speed, agl, pressure.
  if mode = "none" or mains_out {
    return "wait".
  }
  if has_drogues and not drogues_out {
    if reentry_drogue_action(mode, drogues_out, speed, agl, pressure) = "deploy" {
      return "drogues".
    }
    return "wait".
  }
  if reentry_chute_action(mode, mains_out, speed, agl, pressure) = "deploy" {
    return "mains".
  }
  return "wait".
}

// True if a parachute part named name is a drogue chute.
function reentry_is_drogue {
  parameter name.
  return name:tolower:contains("drogue").
}

// True if the heat shield should drop now. gone: it has already been dropped (or there is none).
function reentry_shield_due {
  parameter gone, speed, agl.
  if gone {
    return false.
  }
  return speed <= reentry_shield_speed() or agl <= reentry_shield_last_agl().
}

// Touchdown within what the crew and craft survive.
function reentry_touchdown_ok {
  parameter speed.
  return speed <= reentry_touchdown_limit().
}

// The g felt, in g: the craft's change in velocity over dt (s) plus gravity's pull at position pos
// (from the body's centre, m), all in the same non-rotating frame; mu is the body's gravitational
// parameter. Vectors come as plain components (dv = v_now - v_prev) so this stays pure.
function reentry_proper_g {
  parameter dvx, dvy, dvz, dt, px, py, pz, mu.
  local dist is sqrt(px * px + py * py + pz * pz).
  if dt <= 0 or dist <= 0 {
    return 0.
  }
  local pull is mu / (dist * dist * dist).
  local ax is dvx / dt + px * pull.
  local ay is dvy / dt + py * pull.
  local az is dvz / dt + pz * pull.
  return sqrt(ax * ax + ay * ay + az * az) / 9.80665.
}

// reentry_proper_g from two velocity vectors and the craft's position from the body's centre.
function reentry_g_felt {
  parameter v_prev, v_now, dt, pos, mu.
  local dv is v_now - v_prev.
  return reentry_proper_g(dv:x, dv:y, dv:z, dt, pos:x, pos:y, pos:z, mu).
}

// Seconds until an orbit (ecc, sma in m, true anomaly ta in degrees) next falls to radius r_entry
// from above, around a body of gravitational parameter mu. 0 if it is at or under r_entry now;
// -1 if the orbit never gets that low.
function reentry_entry_eta {
  parameter ecc, sma, mu, r_entry, ta.
  if ecc >= 1 or sma <= 0 {
    return -1.
  }
  local semi_latus is sma * (1 - ecc * ecc).
  local r_now is semi_latus / (1 + ecc * cos(ta)).
  if r_now <= r_entry {
    return 0.
  }
  if ecc <= 0 {
    return -1.
  }
  local cos_entry is (semi_latus / r_entry - 1) / ecc.
  if cos_entry > 1 {
    return -1.
  }
  // On the way down the true anomaly is the negative of the one on the way up.
  local ta_entry is 360 - arccos(max(-1, cos_entry)).
  local m_entry is reentry_mean_anomaly(ecc, ta_entry).
  local m_now is reentry_mean_anomaly(ecc, ta).
  local ahead is mod(mod(m_entry - m_now, 360) + 360, 360).
  return ahead / 360 * 2 * 3.141592653589793 * sqrt(sma ^ 3 / mu).
}

// The mean anomaly in degrees for a true anomaly ta in degrees.
function reentry_mean_anomaly {
  parameter ecc, ta.
  local ecc_anomaly is 2 * arctan2(sqrt(1 - ecc) * sin(ta / 2), sqrt(1 + ecc) * cos(ta / 2)).
  return ecc_anomaly - ecc * sin(ecc_anomaly) * 57.29577951308232.
}

// The heat-shield decoupler modules chosen by jettison ("auto", "none" or a stage number).
function reentry_shield_modules {
  parameter jettison.
  local found is list().
  if jettison:istype("String") and jettison = "none" {
    return found.
  }
  for p in ship:parts {
    local wanted is false.
    if claudia_is_decoupler(p) {
      if jettison:istype("String") {
        set wanted to reentry_shield_beside(p).
      } else {
        set wanted to p:stage = jettison.
      }
    }
    if wanted {
      for name in list("ModuleDecouple", "ModuleAnchoredDecoupler", "ModuleJettison") {
        if p:hasmodule(name) and p:getmodule(name):hasevent("decouple") {
          found:add(p:getmodule(name)).
        }
      }
    }
  }
  return found.
}

// True if the decoupler p is a heat shield or sits against one.
function reentry_shield_beside {
  parameter p.
  if p:hasmodule("ModuleAblator") {
    return true.
  }
  if p:hasparent and p:parent:hasmodule("ModuleAblator") {
    return true.
  }
  for child in p:children {
    if child:hasmodule("ModuleAblator") {
      return true.
    }
  }
  return false.
}

// True if the craft carries a drogue chute.
function reentry_has_drogues {
  for p in ship:parts {
    if p:hasmodule("ModuleParachute") and reentry_is_drogue(p:name) {
      return true.
    }
  }
  return false.
}

// Opens every stowed parachute of one kind: drogues (true) or mains (false). Returns how many it opened.
function reentry_open_chutes {
  parameter drogues.
  local opened is 0.
  for p in ship:parts {
    if p:hasmodule("ModuleParachute") and reentry_is_drogue(p:name) = drogues {
      local m is p:getmodule("ModuleParachute").
      if m:hasevent("deploy chute") {
        m:doevent("deploy chute").
        set opened to opened + 1.
      }
    }
  }
  return opened.
}

function reentry_agl {
  return ship:altitude - max(0, ship:geoposition:terrainheight).
}

// The hottest part in percent of its limit. kOS parts have no temperature suffixes, so the addon reads them.
function reentry_hottest {
  return addons:claudia:hottestpart.
}

function reentry_geo {
  return lexicon("lat", round(ship:geoposition:lat, 4), "lng", round(ship:geoposition:lng, 4), "alt", round(ship:altitude)).
}

// What an abort mid-descent leaves behind: parachutes open if that is safe, then a safe craft.
function reentry_abort {
  parameter opt.
  if ship:body:atm:exists and not claudia_on_ground() {
    local pressure is ship:body:atm:altitudepressure(ship:altitude).
    local speed is ship:velocity:surface:mag.
    if reentry_drogue_action(opt:chute_mode, false, speed, reentry_agl(), pressure) = "deploy" {
      reentry_open_chutes(true).
    }
    if reentry_chute_action(opt:chute_mode, false, speed, reentry_agl(), pressure) = "deploy" {
      reentry_open_chutes(false).
    }
  }
  claudia_make_safe().
}

function reentry {
  parameter options is lexicon().
  local extra is lexicon("max_g", 0, "max_temp_pct", 0, "geo", lexicon(), "splashed", false, "touchdown_speed", -1).
  local ctx is claudia_begin("reentry", options, reentry_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is reentry_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local body_ is ship:body.
  if not body_:atm:exists {
    return claudia_finish(ctx, false, "body has no atmosphere", extra).
  }
  local shield is reentry_shield_modules(opt:jettison_stage).
  local shield_gone is shield:length = 0.
  set extra:geo to reentry_geo().

  // Wait (on rails) for the atmosphere if the craft is still above it.
  local entry_in is 0.
  if ship:altitude > body_:atm:height and not claudia_on_ground() {
    set entry_in to reentry_entry_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).
    if entry_in < 0 {
      return claudia_finish(ctx, false, "the orbit never reaches the atmosphere (periapsis " + round(ship:periapsis) + " m)", extra).
    }
  }
  if opt:max_duration:istype("String") {
    set opt:max_duration to entry_in + 1800.
  }
  local held is opt:hold = "retrograde".
  if held {
    lock steering to srfretrograde.
  }
  if entry_in > 0 {
    local waited is claudia_wait_until(ctx, time:seconds + entry_in).
    if waited <> "" {
      reentry_abort(opt).
      return claudia_finish(ctx, false, waited, extra).
    }
  }

  local has_drogues is reentry_has_drogues().
  local drogues_out is false.
  local chutes_out is false.
  local last_speed is ship:velocity:surface:mag.
  local sample_ut is time:seconds.
  local sample_v is ship:velocity:orbit.
  until claudia_on_ground() {
    local now is time:seconds.
    set last_speed to ship:velocity:surface:mag.
    local agl is reentry_agl().
    local pressure is body_:atm:altitudepressure(ship:altitude).

    // Heat and g, sampled a quarter second apart so frame noise doesn't read as g.
    if now - sample_ut >= 0.25 {
      set extra:max_g to max(extra:max_g, reentry_g_felt(sample_v, ship:velocity:orbit, now - sample_ut, ship:position - body_:position, body_:mu)).
      set extra:max_temp_pct to max(extra:max_temp_pct, reentry_hottest()).
      set sample_ut to now.
      set sample_v to ship:velocity:orbit.
    }

    if reentry_shield_due(shield_gone, last_speed, agl) {
      for m in shield {
        m:doevent("decouple").
      }
      set shield_gone to true.
      // The shield leaving is not a part lost: count what is left once it has gone.
      wait 0.5.
      set ctx:parts to ship:parts:length.
    }
    local plan is reentry_chute_stage(opt:chute_mode, has_drogues, drogues_out, chutes_out, last_speed, agl, pressure).
    if plan = "drogues" {
      reentry_open_chutes(true).
      set drogues_out to true.
      // Open parachutes steer the craft themselves.
      unlock steering.
    } else if plan = "mains" {
      reentry_open_chutes(false).
      set chutes_out to true.
      unlock steering.
    }

    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      reentry_abort(opt).
      set extra:max_temp_pct to round(max(extra:max_temp_pct, reentry_hottest()), 1).
      set extra:max_g to round(extra:max_g, 2).
      set extra:geo to reentry_geo().
      return claudia_finish(ctx, false, ticked, extra).
    }
    wait 0.
  }

  unlock steering.
  set extra:touchdown_speed to round(last_speed, 1).
  set extra:max_g to round(extra:max_g, 2).
  set extra:max_temp_pct to round(max(extra:max_temp_pct, reentry_hottest()), 1).
  set extra:geo to reentry_geo().
  set extra:splashed to status = "SPLASHED".
  if not reentry_touchdown_ok(last_speed) {
    return claudia_finish(ctx, false, "touchdown at " + round(last_speed, 1) + " m/s is over " + reentry_touchdown_limit() + " m/s", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
