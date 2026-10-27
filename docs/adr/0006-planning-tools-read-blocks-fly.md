# Planning Tools only read; Building Blocks fly; Mission Scripts call blocks

Claudia's work splits into three layers with a hard line between them.
- **Planning Tools** (e.g. `predict_burn`, `find_landing_site`) are read-only calculations the mod runs while she plans. They work from the turn's Game Snapshot (only terrain, biome and sun come live from the game) and never start, steer or change anything: no maneuver nodes, no writes to the live orbit or the stock delta-v app.
- **Building Blocks** fly the craft at run time. They may call the mod's calculations internally (the site search, the reentry predictor), and they solve their goal again from the real orbit when they fly, rather than trusting numbers worked out at plan time.
- **Mission Scripts** call Building Blocks, pass them goals rather than timings, branch on their Script Results, stage, add the node an `exec_node` call flies, and report. The Script Check forbids them from calling the block-internal calculations (how it tells their lines from the Core Library's: [ADR 0007](0007-script-check-finds-the-core-library-by-the-bundles-tail.md)), and Planning Tools are reachable from kOS only through a bridge that exists while a block test is running.

We chose this over letting a tool act (an "add node" or "warp to" tool), or letting Mission Scripts call the calculations directly, for three reasons:
- **The guarantees hold.** ADR 0002's guarantee that only an approved script flies, and ADR 0003's Script Check, cover everything that moves the craft.
- **Tools agree with the plan.** Tool answers match the Game Snapshot Claudia planned from, and can be tested against recorded Snapshots.
- **Plans don't go stale.** A plan doesn't break when hours pass between Approval and the burn, because the block re-solves the goal it was given.

The cost of the line:
- **Some calculations run twice**, once in a tool to plan and once in a block to fly. That is why the shared C# sits beneath both.
- **New flight logic needs a block.** Flight logic no block covers stays a Custom Script until one is written.

Decided on map #243 (#251, #252, #253, #254); built from spec #265.
