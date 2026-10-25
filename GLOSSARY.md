# Claudia Kermin

A Kerbal Space Program mod in which an AI mission controller plans and flies missions through kOS, turning a player's stated goal into a sequence of executed maneuvers.

## Language

**Claudia Kermin**:
The name of the mod as a whole.
_Avoid_: Kermin (alone), the plugin

**Claudia**:
The AI mission controller the player talks to, who is also a Kerbal crew member (a standard female Kerbal) that can be assigned to missions.
_Avoid_: the bot, the chatbot, the AI, Claude

**Kerbin**:
The home planet in KSP. The mod name's "Kermin" never refers to the planet.
_Avoid_: Kermin (for the planet)

**Super Intelligence**:
Claudia's crew role, combining the abilities of a Pilot, an Engineer and a Scientist.
_Avoid_: trait (alone), class, profession

**Mission Goal**:
The player's plain-language statement of what a mission must achieve, e.g. "Land on the Mun, run 3 experiments, return to Kerbin."
_Avoid_: objective, prompt, task

**Mission Settings**:
The player's choices for how Claudia meets a Mission Goal (e.g. daylight landing, time versus delta-v), set when the Mission starts and editable later; changing one makes Claudia revise the Mission Plan.
_Avoid_: mission parameters, options (which are Building Block settings)

**Craft Check**:
Claudia's assessment of whether a player-supplied craft can achieve a Mission Goal.
_Avoid_: validation, craft verification

**Blocker**:
A Craft Check finding that prevents the Mission from starting; it cannot be overridden.
_Avoid_: error, hard fail

**Warning**:
A Craft Check finding the player must acknowledge before the Mission starts.
_Avoid_: soft fail, notice

**Game Snapshot**:
A plain copy of the game at one moment (the craft, its stages and parts, orbit, situation, resources, crew, science and link state) that the mod takes and gives to Claudia and the Craft Check, so neither reads the live game; Game Snapshots recorded from real play are also what the mod's tests run against.
_Avoid_: game state, telemetry (which Telemetry Checks measure while Running), dump

**Delta-v Map**:
The reference table of delta-v needed for each leg of travel in the Kerbin system, against which a Mission Plan is budgeted.
_Avoid_: dv chart, cheat sheet

**Maneuver Sequence**:
One self-contained stretch of flight that Claudia plans and kOS executes in a single go, e.g. launch to orbit, Mun transfer burn, landing.
_Avoid_: step, phase, maneuver (alone), burn

**Mission Plan**:
The ordered list of Maneuver Sequences Claudia intends to fly to reach a Mission Goal, each with its delta-v budget and Due Time or rough timing; approved once to start the Mission and revised as Debriefs require.
_Avoid_: flight plan, roadmap

**Due Time**:
The earliest time a Maneuver Sequence may start, set in its Sequence Plan when a wait is too long to sit inside the sequence; the player may fly elsewhere until then.
_Avoid_: window (which is a transfer window), scheduled time

**Sequence Plan**:
Claudia's proposal for the next Maneuver Sequence: its intent, Building Blocks and settings, Due Time, target outcome with tolerances, expected delta-v and duration, abort conditions, Telemetry Checks and risks.
_Avoid_: maneuver plan, proposal, step plan

**Telemetry Check**:
A quantity the mod can measure during a Maneuver Sequence (e.g. delta-v used, apoapsis, electric charge) with a threshold, set in the Sequence Plan or by default; while Running, the mod checks it, and crossing it raises an Alert. A crossed Telemetry Check rules out a Nominal Debrief.
_Avoid_: watch limit, alarm, tripwire

**Alert**:
The mod's message to Claudia that a Telemetry Check was crossed while Running; she answers by letting the sequence carry on or recommending the player Abort.
_Avoid_: alarm (the stock Alarm Clock's word), warning (a Craft Check finding)

**Debrief**:
Claudia's chat-based evaluation after a Maneuver Sequence: what happened versus the plan, progress toward the Mission Goal, and the proposed next Maneuver Sequence with its reasoning.
_Avoid_: report (what a script does with its Script Result), evaluation, review

**Margin recheck**:
The Craft Check run again inside every Debrief, on the craft as it is now against the Maneuver Sequences still to fly in the Mission Plan; charged to the Mission like any other request, and skipped when the Spending Cap has no room for it beside the Debrief.
_Avoid_: second Craft Check, re-check

**Nominal**:
A Debrief outcome: the Maneuver Sequence ended within its Sequence Plan's tolerances.

**Off-Nominal**:
A Debrief outcome: the Maneuver Sequence ended outside tolerance, but the Mission Goal is still reachable.

**Failed**:
A Debrief outcome: the Mission Goal can no longer be reached as stated.
_Avoid_: Lost (which is a Mission outcome, not a Debrief outcome)

**Approval**:
The player's go-ahead, given in chat, for Claudia to execute the proposed Maneuver Sequence.
_Avoid_: confirmation, sign-off

**Mission**:
One Mission Goal pursued by Claudia with one craft and any Child Vessels, from its Craft Check until it succeeds or is abandoned.
_Avoid_: flight, run, job

**Child Vessel**:
A vessel that separated from a Mission's craft during the Mission and carries a kOS processor, which Claudia may fly with Maneuver Sequences of its own while she has a link to it. The craft stays the vessel Claudia is aboard (or, for a remote Mission, the one with the original processor).
_Avoid_: probe, drone, secondary craft

**Spending Cap**:
The most a Mission may spend on Claudia's API calls, set by the player when the Mission starts and raisable later.
_Avoid_: budget (which means delta-v), limit, quota

**Running**:
The state of a Mission while a Maneuver Sequence's script executes; it ends at the Debrief. A Mission can be Running during a Blackout; taking Manual Control ends the run.
_Avoid_: executing, active, in progress

**Paused**:
The state of a Mission that has stopped making progress, e.g. at its Spending Cap or on an API error, and can be resumed by the player.
_Avoid_: suspended, on hold, abandoned

**Blackout**:
A period when Claudia has no link to Kerbin and carries on without the player's Approval: she flies the approved Mission Plan, and may revise it within the Mission Goal and Spending Cap; her messages and revisions wait for the player until the link returns. A Mission in Blackout is not Paused.
_Avoid_: autonomous mode, offline, comms loss

**Manual Control**:
The state of a Mission while the player flies the craft and Claudia only advises; it ends when the player hands the craft back. A Mission in Manual Control is not Paused.
_Avoid_: takeover, override, manual mode

**Interrupted**:
Of a Maneuver Sequence: it was Running when the scene changed, a save was loaded, or the game quit or crashed, so its script is gone. It is never restarted; Claudia gives it an interrupted-sequence Debrief when the player is back in Flight, judging from the craft as it now is. Not a Debrief outcome: the Debrief still ends Nominal, Off-Nominal or Failed.
_Avoid_: restarted, resumed (an interrupted sequence is Debriefed, never run again)

**Hand back**:
The player's act, with an optional message about what they did, of giving the craft back to Claudia and ending Manual Control. Claudia gives the manual stretch a short Debrief (judged against its target if it was planned, a plain account if not), then revises the Mission Plan for Approval.
_Avoid_: give back, return control, release

**Manual stretch**:
A Maneuver Sequence in the Mission Plan that the player flies by hand (e.g. docking, EVA science), so it has no script. Approving it starts Manual Control; the player's Hand back ends it.
_Avoid_: manual sequence, manual mode (Manual Control is the state)

**Lost**:
The outcome of a Mission that ends because Claudia was killed aboard the craft.
_Avoid_: failed, crashed, KIA (for the Mission)

**Capcom strip**:
The slim bar in Flight (under the altimeter unless the player drags it elsewhere) through which the player follows a Mission without leaving the flight view: Claudia's portrait and latest line, where the Mission is in its Mission Plan, the Spending Cap, what is locked, and a tab that expands the Mission window docked to it (amber when Claudia needs the player).
_Avoid_: HUD, status bar, ticker

### Scripts

**Core Library**:
The fixed set of Building Blocks that ships with Claudia Kermin and is the same for every Mission.
_Avoid_: primitives, stdlib, script bundle

**Building Block**:
One reusable flight routine in the Core Library (e.g. launch, transfer_to, land) that Claudia sets up for a Maneuver Sequence.
_Avoid_: primitive, routine, module

**Mission Script**:
A script Claudia writes for one Mission to carry out a Maneuver Sequence, kept with that Mission.
_Avoid_: generated script, glue script

**Custom Script**:
A Mission Script written from scratch instead of from Building Blocks, used when no Building Block fits.
_Avoid_: raw script, freeform script

**Script Result**:
The values a Mission Script hands back once, when its Maneuver Sequence ends (e.g. apoapsis, delta-v used, situation), which the mod passes to Claudia as data for her Debrief; a script that ends without one has failed. The script is said to report it.
_Avoid_: report (as a noun), outcome (which is a Debrief's), return value

**Script Check**:
The mod's check of a compiled script against a forbidden list (e.g. save and load, file operations, changing other vessels) before Approval; a script that fails is rewritten or never offered.
_Avoid_: sandbox, validation

**Couldn't produce a safe script**:
The state of a Mission when a Sequence Plan's script failed the Script Check, was rewritten once automatically (billed under the Spending Cap), and failed again. The plan is dropped, nothing can be approved, and the player needs to act: **Retry** asks Claudia for the script again, **Retry with a note** does so with the player's guidance, and **Re-plan** has her plan the Maneuver Sequence afresh.
_Avoid_: script error, unsafe script (as a name for the state), rejected script
