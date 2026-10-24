# PROTOTYPE results: Claude API round-trip spike in KSP

One in-game pass on 2026-10-24 with KSP 1.12.5 (macOS, Unity 2019.4.18f1, Mono runtime 4.0.30319). The evidence is in [`logs/mac-pass1.txt`](logs/mac-pass1.txt), the `[ClaudiaApiSpike]` lines from `KSP.log`. The log never contains the API key.

## Verdict

A hand-rolled Messages API client works inside KSP, as **Calling the Claude API from a KSP 1.12 plugin** concluded. It needs no SDK, no extra process and no TLS setup. Both `UnityWebRequest` (main thread) and `HttpWebRequest` (worker thread) stream promptly.

## Findings

| Question | Result |
|---|---|
| Round trip | Six full streamed replies from `claude-opus-5-5`, effort `low`, `anthropic-version: 2026-10-21`. The `server-side-fallback-2026-07-01` beta with `fallbacks: "default"` was accepted. |
| `HttpClient` | Not an option without bundling it: the macOS app bundle contains no `System.Net.Http.dll`. |
| `UnityWebRequest` streaming | Prompt. `DownloadHandlerScript.ReceiveData` fires about every 50 ms, roughly once per SSE event, with first text after about 2.3 s. No buffering until the end. |
| `HttpWebRequest` streaming | Prompt, the same timing. `ReadLine` on the response stream, with lines queued to the main thread. |
| TLS | Works with no setup. `ServicePointManager.SecurityProtocol` is already `Tls, Tls11, Tls12` (4032) at load, so OR-ing in TLS 1.2 is a no-op. Never overwrite it (DeepJeb does); an OR is harmless. |
| Errors | Both transports return the JSON error body with its `request_id` (401 `authentication_error` in about 100 ms). `UnityWebRequest` passes the error body to `ReceiveData`; `HttpWebRequest` throws a `WebException` whose `Response` holds it. |
| Prompt caching | Works. The first call wrote 30,784 tokens to the 1-hour cache (`ephemeral_1h_input_tokens`), and the second read all 30,784. |
| Scene changes | Both transports streamed through Space Center → VAB and VAB → Space Center from a `DontDestroyOnLoad` object, and both replies were complete. `UnityWebRequest` stalled for 1.7 s during the scene load (main-thread hitch), then caught up with nothing lost. |
| Pause menu | Streaming continued at the normal rate while the game was paused. |
| Cancel | `HttpWebRequest.Abort()` stopped the stream at once. The stream simply ends with no `stop_reason`, so the client must record a cancel itself. `UnityWebRequest.Abort()` was not tested. |
| JSON | SimpleJSON (MIT, one source file compiled in) built every request and parsed every stream event, including the nested `usage` object. |
| Thinking | At effort `low`, Opus 5.5 sends one thinking block (empty text, display `omitted`) about 0.2 s before the text. Nothing is visible to the player until the text starts. |

## Not tested

- **Windows and Linux.** Deferred by the player; a prebuilt DLL and a short Windows checklist are in the README.
- **`UnityWebRequest.Abort()`.**
