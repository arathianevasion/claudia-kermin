# The mod owns execution; Claude only advises

The mission loop (Craft Check → timing → plan → Approval → execute → Debrief → next plan) is a state machine in the mod, which calls Claude at defined points with tools to read state and propose plans; Claude can never start a kOS script itself, and only the mod does so, after the player's Approval. We chose this over a free-running agent loop with an "execute" tool so that Approval is a hard guarantee rather than a prompt instruction, and so the Mission's state is explicit, inspectable and persistable.
