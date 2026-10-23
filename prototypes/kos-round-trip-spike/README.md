# PROTOTYPE: kOS round-trip spike

Throwaway code for the **kOS round-trip spike** ticket (#18). It is not the v1 mod. It lives only on the `prototype/kos-round-trip` branch.

It answers these questions on KSP 1.12.5 + kOS 1.6.0.1:

1. Can C# write a script to a processor, start it, and get a result back through `ADDONS:CLAUDIA:REPORT(lexicon)`?
2. Can the error text from a failed run be recovered from the terminal buffer (`IScreenBuffer.GetBuffer()`) or `KSP.log`?
3. How do addon instances (one per processor) behave across dock, undock, staging and scene reload?
4. Does RSVP v9 run on kOS 1.6.0.1?
5. Does a kOS program, with its triggers, keep running through on-rails warp, including across an SOI change?

It also gathers evidence for **kOS integration method** (#19). It tries all three start methods (A: keystrokes, B: a boot listener fed by `CORE:MESSAGES`, C: pushing a program context directly), two delivery targets (local disk vs `0:/`), `Cpu.BreakExecution` aborts, and assigning a boot file from C#.

## Setup (already done on this Mac)

- Scratch install: `~/KSP_spike`, a copy of `/Applications/KSP_osx`. It has stock KSP, kOS 1.6.0.1 and RSVP v9 in `Ships/Script/rsvp`, and the other mods are removed.
- Build: `dotnet build -c Release` in this folder. It targets `net48` (kOS 1.6.0.1 is built against .NET Framework 4.8). The build copies the DLL and test scripts to `~/KSP_spike/GameData/ClaudiaSpike/` and the boot listener to `~/KSP_spike/Ships/Script/boot/`. Set `KSPDIR` to point the build at another install.

## In-game checklist

Launch `~/KSP_spike/KSP.app`, not the game in `/Applications`. Use the **Sandbox** save. The spike window appears in Flight. Every event also goes to `~/KSP_spike/KSP.log` with the prefix `[ClaudiaSpike]`, so you don't need to write anything down. Just tell Claude when you're done.

**Craft:** build this from the top down.

1. OKTO2 probe core
2. CX-4181 kOS processor, with a name tag of `upper`
3. battery and Communotron 16
4. Clamp-O-Tron port, attached directly to a second Clamp-O-Tron port
5. CX-4181 kOS processor, with a name tag of `lower`
6. OKTO2 probe core, battery, RCS block ×4 and a small monoprop tank
7. decoupler
8. a small tank and engine (anything that reaches orbit, or use cheats)

Leave the boot file set to None.

**Cheats:** Alt+F12 (Option+F12 on Mac) → Cheats → **Set Orbit** saves flying. Kerbin with SMA 700,000 m and ecc 0 gives a low orbit. Kerbin with SMA 60,000,000 m and ecc 0.9 gives an escape trajectory for test 6.

| # | Do this | It answers |
|---|---------|------------|
| 1 | On the launchpad, with `upper`'s row and the **local disk** delivery option: run **ok** with A, then B (B needs step 2 first), then C. | Round trip, all start methods |
| 2 | Click **Install boot listener + reboot** on `upper`, then run **ok** with **B**. Then revert to launch and check whether the listener comes back. | Boot assignment from C# and how it behaves across reloads |
| 3 | Run **runtime_error**, **syntax_error** and **no_report** with C. Then run runtime_error with B. | Error text recovery, and whether B's listener dies after an error |
| 4 | Switch to **archive 0:/** delivery and run **ok** with C, once on the pad and once in orbit with the antenna retracted (no connection). | Delivery to `0:/` vs the local disk |
| 5 | In Kerbin orbit, start **long_run** on `upper` with C. Mid-run: stage (decouple the booster), then **undock** the ports, then RCS back and **re-dock**. Then press **Abort** on another long_run. | How addons behave across staging, undocking and docking, and aborts |
| 6 | Go to the Space Center and back to the vessel (scene reload), then click **Dump addons**. | Stale and new instances |
| 7 | Set the escape orbit, then run **warp_soi** with C. Also run it once in a plain orbit (6 h warp). | Warp, triggers and SOI change |
| 8 | In a low Kerbin orbit with the antenna extended (home connection), run **rsvp_mun** with C. | RSVP v9 on kOS 1.6.0.1 |

When you're done, quit KSP and tell Claude. It reads the results from `KSP.log`.
