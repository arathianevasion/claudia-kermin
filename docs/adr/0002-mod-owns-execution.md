# The mod owns execution; Claude only advises

The mission loop (Craft Check → timing → plan → Approval → execute → Debrief → next plan) is a state machine in the mod, which calls Claude at defined points with tools to read state and propose plans; Claude can never start a kOS script itself, and only the mod does so, after the player's Approval. We chose this over a free-running agent loop with an "execute" tool so that Approval is a hard guarantee rather than a prompt instruction, and so the Mission's state is explicit, inspectable and persistable.

**Amendment (Blackout):** during a Blackout, the player's Approval of the Mission Plan stands in for each Maneuver Sequence's Approval. Claudia may fly the planned sequences and may revise the Mission Plan herself within the Mission Goal and the Spending Cap; the player sees each revision, with her reasons, when the link returns. A Failed Debrief waits for the link. The mod still starts every script; Claude still only advises.
