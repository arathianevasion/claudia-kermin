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

**Craft Check**:
Claudia's assessment of whether a player-supplied craft can achieve a Mission Goal.
_Avoid_: validation, craft verification

**Blocker**:
A Craft Check finding that prevents the Mission from starting; it cannot be overridden.
_Avoid_: error, hard fail

**Warning**:
A Craft Check finding the player must acknowledge before the Mission starts.
_Avoid_: soft fail, notice

**Delta-v Map**:
The reference table of delta-v needed for each leg of travel in the Kerbin system, against which a Mission Plan is budgeted.
_Avoid_: dv chart, cheat sheet

**Maneuver Sequence**:
One self-contained stretch of flight that Claudia plans and kOS executes in a single go, e.g. launch to orbit, Mun transfer burn, landing.
_Avoid_: step, phase, maneuver (alone), burn

**Mission Plan**:
The ordered list of Maneuver Sequences Claudia intends to fly to reach a Mission Goal, each with its delta-v budget and rough timing; approved once to start the Mission and revised as Debriefs require.
_Avoid_: flight plan, roadmap

**Sequence Plan**:
Claudia's proposal for the next Maneuver Sequence: its intent, Building Blocks and settings, target outcome with tolerances, expected delta-v and duration, abort conditions, Telemetry Checks and risks.
_Avoid_: maneuver plan, proposal, step plan

**Telemetry Check**:
A quantity the mod can measure during a Maneuver Sequence (e.g. delta-v used, apoapsis, electric charge) with a threshold, set in the Sequence Plan or by default; while Running, the mod checks it, and crossing it raises an Alert. A crossed Telemetry Check rules out a Nominal Debrief.
_Avoid_: watch limit, alarm, tripwire

**Alert**:
The mod's message to Claudia that a Telemetry Check was crossed while Running; she answers by letting the sequence carry on or recommending the player Abort.
_Avoid_: alarm (the stock Alarm Clock's word), warning (a Craft Check finding)

**Debrief**:
Claudia's chat-based evaluation after a Maneuver Sequence: what happened versus the plan, progress toward the Mission Goal, and the proposed next Maneuver Sequence with its reasoning.
_Avoid_: report, evaluation, review

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

**Lost**:
The outcome of a Mission that ends because Claudia was killed aboard the craft.
_Avoid_: failed, crashed, KIA (for the Mission)

### Scripts

**Core Library**:
The fixed set of Building Blocks that ships with Claudia Kermin and is the same for every Mission.
_Avoid_: primitives, stdlib, script bundle

**Building Block**:
One reusable flight routine in the Core Library (e.g. launch, transfer, land) that Claudia sets up for a Maneuver Sequence.
_Avoid_: primitive, routine, module

**Mission Script**:
A script Claudia writes for one Mission to carry out a Maneuver Sequence, kept with that Mission.
_Avoid_: generated script, glue script

**Custom Script**:
A Mission Script written from scratch instead of from Building Blocks, used when no Building Block fits.
_Avoid_: raw script, freeform script
