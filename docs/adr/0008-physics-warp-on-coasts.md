# Physics warp on Coasts

ADR 0003 refused physics warp from every mod-started program, and ADR 0004 kept that refusal. So a launch's climb out of the air after cutoff, a fall under parachutes and the low fall before a landing burn all ran at 1×. Rails warp was also fragile: the Core Library asked for it once, stayed at 1× after any cut, and warped to a burn before turning to it. We now allow physics warp from a Mission Script on a **Coast**, a stretch with no engine or RCS thrust, and we harden the rails warp around it (map #297, spec #314).

- **The Coast is checked every frame, not trusted.** While a mod-started program runs, the addon checks the craft every physics frame. It is on a Coast when every engine's thrust is 0 and no RCS thruster fires. If physics warp is above 1× off a Coast, the addon drops it to 1× that frame and logs `physics warp dropped: thrust`. This applies whoever set the warp, the player included, while a sequence is Running.
- **The runtime gate replaces the blanket refusal (supersedes ADR 0003's physics-warp bullet).** From a mod-started program:
  - switching the warp mode to PHYSICS is allowed only at 1× and on a Coast (kOS's `MODE` setter skips stock's 1× check);
  - a physics `WARP` above 1× is allowed only on a Coast, capped at 4×, and only when both switches below are on;
  - `WARPTO` in physics mode stays refused, for ADR 0004's reason.

  Rates below the cap (dynamic pressure, height, the next event) are the Core Library's job, not the gate's. The Script Check's physics-warp rule is removed: whether the craft is on a Coast is known only at run time.
- **Two switches.** `physics_warp` in the player config (default true) and a **Physics warp on Coasts** Mission Setting, defaulting from config and greyed out when config is false. Claudia can't change the setting; asked in chat, she points the player at the toggle.
- **The player's cancel is a signal (extends ADR 0004).** A Harmony postfix on `TimeWarp.assumeWarpRate`, together with markers on the player's warp entry points, tells the player's rate changes from KSP's. `ADDONS:CLAUDIA:WARPCANCELLED` is true once the player lowers or stops warp during the current wait. ADR 0004's "ask again only after a hold by the mod" becomes: **ask again whenever the rate is 1×**, unless warp is held or the player cancelled this wait. KSP drops to 1× for its own reasons (#300), and a one-shot request couldn't tell that apart from the player.
- **The mod raises kOS's IPU while a Mission Script is Running.** It sets `CONFIG:IPU` to `script_ipu` (player config, default 1000), and puts the player's value back when the script ends, is Interrupted, or the player takes Manual Control, and at load after a crash. This is the first global kOS setting the mod itself changes. ADR 0003's gate on `CONFIG` setters still refuses scripts. It is paired with a C# ejection solver, a block-internal call (ADR 0006, ADR 0007), that replaces RSVP in `transfer_to`: RSVP's search took about 3 real minutes at 1× (#299).

## Considered Options

- **Keep physics warp forbidden and only fix rails warp:** rejected. It leaves every launch at about 4 m 40 s of real time, with the coast in the air at 1×.
- **Let the Script Check prove a script only physics-warps on Coasts:** rejected. Thrust is a run-time fact, and a script can warp and then stage.
- **Leave the q, height and event limits to the gate:** rejected. The gate would need each block's knowledge. A Custom Script that sets `WARP` itself gets only the gate, and Claudia's guidance tells her to use the helpers.
- **A Mission Setting Claudia sets from chat:** rejected. A setting stays the player's act, as the other Mission Settings are.
- **Raise the IPU for good, or only during a search:** rejected. A permanent change takes the setting from the player. Raising it only during a search would put the switching in every block, and a Custom Script's own search wouldn't gain from it.
- **Warp during RSVP's search:** rejected. kOS runs the same instructions per real second at any warp, so the search still takes minutes while the window moves hours away.

The cost:
- **More Harmony patches** on KSP's warp internals (the player-cancel markers), with the same coupling risk as ADR 0003.
- **A frame of physics warp under thrust is possible** before the addon drops it; the block tests allow at most one.
- **The mod now writes a global kOS setting** and must always put it back.
