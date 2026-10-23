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

**Maneuver Sequence**:
One self-contained stretch of flight that Claudia plans and kOS executes in a single go, e.g. launch to orbit, Mun transfer burn, landing.
_Avoid_: step, phase, maneuver (alone), burn

**Debrief**:
Claudia's chat-based evaluation after a Maneuver Sequence: what happened versus the plan, progress toward the Mission Goal, and the proposed next Maneuver Sequence with its reasoning.
_Avoid_: report, evaluation, review

**Approval**:
The player's go-ahead, given in chat, for Claudia to execute the proposed Maneuver Sequence.
_Avoid_: confirmation, sign-off

**Mission**:
One Mission Goal pursued by Claudia with one craft, from its Craft Check until it succeeds or is abandoned.
_Avoid_: flight, run, job

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
