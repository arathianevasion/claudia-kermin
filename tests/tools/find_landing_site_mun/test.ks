// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// find_landing_site scenario (tests/tools, debug_tools): the craft arrives in a 30 km circular equatorial orbit round an airless
// body with no stage fired. The script asks find_landing_site through the test-only bridge three ways (any biome, a named biome,
// near a point on the far side), reads KSP's own biome and terrain height at each site the tool gives, and warps to the pass of
// one site to see the craft's ground track really is over it. It flies nothing. Spec #265: biome and terrain match KSP at the
// spot; each call is timed (call_ms, against the per-frame budget in KSP.log).

@lazyglobal off.

wait until ship:unpacked.

local b is ship:body.

// How the tool's sites compare with KSP's own at each: biome names that differ, and the largest terrain height difference, m.
function compare {
  parameter sites.
  local biome_misses is 0.
  local terrain_worst is 0.
  for site in sites {
    local spot is b:geopositionlatlng(site:latitude, site:longitude).
    if addons:claudia:biome(spot) <> site:biome { set biome_misses to biome_misses + 1. }
    set terrain_worst to max(terrain_worst, abs(spot:terrainheight - site:terrain_altitude)).
  }
  return lexicon("biome_misses", biome_misses, "terrain_worst", terrain_worst).
}

// The lowest and highest sun elevation and the steepest slope among the sites.
function extent {
  parameter sites, key.
  local low is 1e9.
  local high is -1e9.
  for site in sites {
    set low to min(low, site[key]).
    set high to max(high, site[key]).
  }
  return lexicon("low", low, "high", high).
}

local result is lexicon("body", b:name).

// 1. Any biome, night allowed: the sites the track passes soonest.
local any is addons:claudia:planningtool("find_landing_site", lexicon("body", b:name, "daylight", false, "within_hours", 1, "max_slope_deg", 10)).
if any:haskey("error") {
  addons:claudia:report(lexicon("success", false, "reason", "find_landing_site refused: " + any:error)).
} else {
  local sites is any:sites.
  local seen is compare(sites).
  result:add("sites_any", sites:length).
  result:add("biome_misses", seen:biome_misses).
  result:add("terrain_worst_m", seen:terrain_worst).
  result:add("call_ms_any", any:call_ms).
  result:add("snapshot_ms", any:snapshot_ms).
  result:add("slope_worst_deg", extent(sites, "slope_deg"):high).
  if sites:length > 0 {
    // 2. The biome of the first site, asked for by name: every site must be in it, and it must find at least that one.
    local want is sites[0]:biome.
    local named is addons:claudia:planningtool("find_landing_site", lexicon("body", b:name, "biome", want, "daylight", false, "within_hours", 1, "max_slope_deg", 10)).
    if named:haskey("error") {
      result:add("named_error", named:error).
    } else {
      local named_seen is compare(named:sites).
      result:add("sites_named", named:sites:length).
      result:add("named_biome_misses", named_seen:biome_misses).
      result:add("named_terrain_worst_m", named_seen:terrain_worst).
      result:add("call_ms_named", named:call_ms).
      local wrong is 0.
      for site in named:sites { if site:biome <> want { set wrong to wrong + 1. } }
      result:add("named_wrong_biome", wrong).
    }
  }

  // 3. Near the far side of the body from where the craft is now, in daylight: the pass is about half an orbit away, and the
  // craft is warped there to see where its ground track really is.
  local here is ship:geoposition.
  local far_lng is here:lng + 180.
  if far_lng > 180 { set far_lng to far_lng - 360. }
  local near is addons:claudia:planningtool("find_landing_site", lexicon("body", b:name, "daylight", true, "within_hours", 1, "max_slope_deg", 10,
    "near", lexicon("latitude", 0, "longitude", far_lng))).
  if near:haskey("error") {
    result:add("near_error", near:error).
  } else {
    result:add("sites_near", near:sites:length).
    result:add("call_ms_near", near:call_ms).
    local near_seen is compare(near:sites).
    result:add("near_biome_misses", near_seen:biome_misses).
    result:add("near_terrain_worst_m", near_seen:terrain_worst).
    if near:sites:length > 0 {
      result:add("near_sun_low_deg", extent(near:sites, "sun_elevation_deg"):low).
      local site is near:sites[0].
      local spot is b:geopositionlatlng(site:latitude, site:longitude).
      kuniverse:timewarp:warpto(site:pass_ut).
      wait until time:seconds >= site:pass_ut.
      // The craft's ground point as the pass begins against the site, m on the surface.
      result:add("track_error_m", (ship:geoposition:position - spot:position):mag).
    }
  }
  print "find_landing_site scenario at " + b:name + ": " + any:sites:length + " sites, " + seen:biome_misses + " biome misses, terrain off by at most " + round(seen:terrain_worst, 2) + " m, " + round(any:call_ms, 1) + " ms.".
  result:add("success", true).
  addons:claudia:report(result).
}
