# PROTOTYPE: Claude API round-trip spike

Throwaway code for the **Claude API round-trip spike in KSP** ticket (#21). It is not the v1 mod. It lives only on the `prototype/claude-api-round-trip` branch.

It answers these questions on KSP 1.12.5:

1. Does a hand-rolled Messages API client work inside KSP, with no SDK and no extra process?
2. Does `UnityWebRequest` with a `DownloadHandlerScript` deliver stream chunks as they arrive, or buffer them until the end? How does `HttpWebRequest` on a worker thread compare?
3. Does TLS work with no setup, and does OR-ing TLS 1.2 into `ServicePointManager.SecurityProtocol` (rather than overwriting it) change anything?
4. Does a request survive scene changes and the pause menu? Can it be cancelled?
5. Do API errors (a bad key) come back readable on both transports?
6. Does prompt caching work through the hand-rolled client (`cache_read_input_tokens` on the second call)?
7. Does SimpleJSON (MIT, compiled in from `ThirdParty/`) handle building requests and parsing stream events on KSP's Mono?

The requests use `claude-opus-5-5` at effort `low`, `anthropic-version: 2026-10-21`, and server-side refusal fallbacks (`fallbacks: "default"`).

Already known without running anything: KSP for macOS ships no `System.Net.Http.dll` anywhere in its app bundle, so `HttpClient` is unavailable unless a mod bundles it.

## Setup

- Scratch install: `~/KSP_spike`, the same copy used by the kOS spike.
- Build: `dotnet build -c Release` in this folder. The build copies the DLL to `~/KSP_spike/GameData/ClaudiaApiSpike/Plugins/`.
- API key: the spike reads it from `~/KSP_spike/GameData/ClaudiaApiSpike/PluginData/api_key.txt`. It never logs the key, only its length.

## In-game checklist

The spike window appears from the main menu onward. Every result also goes to `~/KSP_spike/KSP.log` with the prefix `[ClaudiaApiSpike]`, so you don't need to write anything down. The whole run costs well under US$1.

| # | Do this | It answers |
|---|---------|------------|
| 1 | At the main menu, click **B: HttpWebRequest stream** first, before touching TLS. Wait for `END`. | TLS with no setup on a worker thread |
| 2 | Click **A: UnityWebRequest stream** and watch the reply in the window: does the text grow as it arrives, or appear all at once? | Whether `DownloadHandlerScript` streams |
| 3 | Click **OR in TLS 1.2**, then **B** again. | The DeepJeb TLS question |
| 4 | Click **Bad key A**, then **Bad key B**. | Error bodies on both transports |
| 5 | Click **Cache test (A, twice)** and wait for both `END` lines. | Prompt caching |
| 6 | Load the Sandbox save. At the Space Center, start **A**, and while the text is still arriving, enter the VAB. Then start **B** and go back to the Space Center while it streams. | Requests across scene changes |
| 7 | Launch any craft. In Flight, start **A**, press Esc to pause for about 5 seconds while it streams, then resume. | Requests while the game is paused |
| 8 | Start **B** and press **Cancel** partway through. Then do the same with **A**. | Cancelling a request |

When you're done, quit KSP and tell Claude.
