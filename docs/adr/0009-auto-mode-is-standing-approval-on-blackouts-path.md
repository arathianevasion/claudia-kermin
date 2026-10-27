# Auto Mode is standing Approval on Blackout's path, and its room check uses worst cases

Auto Mode lets the player give their Approval in advance: every plan Claudia proposes after the first Mission Plan is approved the moment it arrives. Blackout already did something like it: with no link, the player's Approval of the Mission Plan stands for each sequence, so the mod runs her next Sequence Plan and takes a revised Mission Plan without asking (ADR 0002's amendment).

We decided Auto Mode is the **same thing, given by the player instead of by the link**. One approve-in-advance check (`ApproveInAdvance`) runs after every input and every reply, where the Blackout check ran, and decides for the plan in hand whether Approval is already given: in a Blackout, or under Auto Mode. Both share the waits: the first Mission Plan (never approved, so there is nothing to stand for it), a Manual stretch and a Due Time wait for the player; a stale plan is re-planned. The Approval itself is still `Approval()`, so the Script Check (ADR 0003) and the Due Time and late-window rules hold as they do for the Approve button, and a typed "approve?" never starts anything (ADR 0002). With both on, Blackout's rules stand; Auto Mode only adds what Blackout can't do.

What Auto Mode adds goes through the same check: a reduced Mission Goal on offer is approved; an unsafe script is re-planned once per plan (a second failure waits for the player); and an Abort recommendation is taken as if the player pressed Abort, in a Blackout too, so with Auto Mode on a Blackout neither drops the recommendation nor refuses the Abort. An Abort taken this way is never shown to the player as a recommendation to answer.

Auto Mode is a per-Mission bool, not a Mission Setting: changing a Mission Setting asks Claudia for a revised Mission Plan, and flipping Auto Mode must not.

Under Auto Mode, unlike a Blackout, an approval (or a re-plan) also needs the **room check**: Committed, plus the worst case of the Debrief after the plan, plus the worst case of the next Sequence Plan request after that (plus the re-plan's own, when re-planning), must fit the Spending Cap. The same holds for a reduced goal's revised Mission Plan request and an unsafe script's re-plan, each priced in. An Abort needs no room: stopping a run costs nothing, and the Debrief after it is `MaySend`'s to allow. The worst cases use the formula `MaySend` uses (`RequestCharge.WorstCase`: about 3 characters a token, rounded up, plus a full `max_tokens` of output), priced on probe requests: the Debrief by `DebriefReserve`'s probe, the next plan by a `Turn` built as `BeginAfterDebrief` builds it, with the Debrief, a full `max_tokens` reply to it and `DebriefReserve`'s slack added to its input. Each probe can only lean higher than the request it stands for, never lower. Without room the plan waits for the player as any plan does. A Blackout keeps no room check: the player can't answer, so a request at the cap waits (the waiting call) rather than Pausing.

We rejected:
- **A separate Auto Mode path beside Blackout's.** Two places would decide what may fly without the player, and they would drift.
- **A room check on past costs** (what the last Debrief and plan really cost). It is cheaper and usually closer, but a long run report or a big snapshot can make the next request dearer than the last, and then Auto Mode would approve a plan whose Debrief `MaySend` refuses, Pausing an unattended Mission mid-way: the one outcome the check exists to prevent.
- **No room check** (let `MaySend` Pause at the cap). The Mission would stop with a Debrief or plan half-requested instead of at a plan, where the player can approve it themselves or raise the cap.

The cost:
- **The check is conservative.** Worst cases assume a full `max_tokens` reply every time, so Auto Mode stops while there is often room left; the player can approve by hand or raise the cap.
- **It is not a bound.** A tool loop charges per step and a Debrief's run report can outgrow the slack, so `MaySend` and `AfterCharge` stay the backstop.
- **No reserve for a Margin recheck or Second look**: both skip themselves when the cap has no room.

Decided on map #319 (#320, #321, #322); built from spec #327 (#328, #329).
