@lazyglobal off.

// Claudia Kermin: RSVP v9's main.ks with only its file loading rewritten (see NOTE.md). The source
// file list and compile_to, which compiles the files onto a volume, are left out; import() calls
// the bundled files instead of loading them.

// Local functions are added to this lexicon by the "export" method
// in order to make them available to other scripts while preventing pollution
// of the global namespace.
global rsvp is lex().

export("goto", goto@).
import().

local function goto {
    parameter destination, options is lex().

    // Thoroughly check supplied parameters, options and game state for
    // correctness. If any problems are found, print validation details
    // to the console then exit early.
    local maybe is rsvp:validate_parameters(destination, options).

    if not maybe:success {
        print maybe.
        return maybe.
    }

    // Find the lowest deltav cost transfer using the specified settings.
    local settings is maybe:settings.
    local tuple is rsvp:find_launch_window(destination, settings).
    local craft_transfer is tuple:transfer.
    local result is tuple:result.

    // If no node creation has been requested return predicted transfer details,
    // otherwise choose betwen the 4 combinations of possible transfer types.
    if settings:create_maneuver_nodes <> "none" {
        // Both "origin_type" and "destination_type" are either the string
        // "vessel" or "body", so can be used to construct the function
        // names for transfer, for example "vessel_to_vessel" or "body_to_body".
        local key is settings:origin_type + "_to_" + settings:destination_type.
        set result to rsvp[key](destination, settings, craft_transfer, result).

        if not result:success {
            // Print details to the console
            if settings:verbose {
                print result.
            }

            // In the case of failure delete any manuever nodes created.
            if settings:cleanup_maneuver_nodes {
                for maneuver in allnodes {
                    remove maneuver.
                }
            }
        }
    }

    return result.
}

// Add delegate to the global "rsvp" lexicon.
local function export {
    parameter key, value.

    rsvp:add(key, value).
}

// Add functions from all other scripts into lexicon. Rewritten for Claudia Kermin: the bundle holds
// each file as a function taking export, called here in place of runoncepath(source_path, export@).
local function import {
    rsvp_file_hill_climb(export@).
    rsvp_file_lambert(export@).
    rsvp_file_maneuver(export@).
    rsvp_file_orbit(export@).
    rsvp_file_refine(export@).
    rsvp_file_search(export@).
    rsvp_file_transfer(export@).
    rsvp_file_validate(export@).
}