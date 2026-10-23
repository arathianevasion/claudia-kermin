# Calling the Claude API from a KSP 1.12 plugin

Research for issue #3. Researched 2026-10-23.

## Answer

**Yes.** A KSP 1.12.5 plugin can call `POST https://api.anthropic.com/v1/messages` directly. No sidecar process is needed.

- **Runtime:** KSP 1.12 runs on Unity 2019.4.18f1 LTS with Unity's .NET 4.x (Mono) scripting runtime. Plugins target .NET Framework 4.x; 4.7.2 is the safe ceiling.
- **TLS 1.2:** Unity 2018.2 and later support TLS 1.2 fully on desktop, both through `UnityWebRequest` and through the .NET 4.x networking APIs (`HttpWebRequest` and friends). `api.anthropic.com` needs no special TLS setup.
- **Transport:** there are two workable options.
  1. **`UnityWebRequest` driven from a coroutine on the main thread.** This is the idiomatic Unity route. For streaming, attach a custom `DownloadHandlerScript` and parse the SSE events in `ReceiveData`.
  2. **`HttpWebRequest` (or `HttpClient`) on a background thread.** Results go back to the main thread through a queue that an `Update()` loop drains. KSP-AVC, a widely used KSP mod, calls `https://api.github.com` this way.
  
  `UnityWebRequest` must not be created or polled from a worker thread.
- **JSON:** KSP gives you nothing usable for the Messages API. Unity's `JsonUtility` cannot handle dictionaries, polymorphic content blocks, or top-level arrays. KSP mods normally bundle a small JSON parser as source (SimpleJSON, MiniJSON) or ship Newtonsoft.Json.
- **Streaming:** recommended. The API docs advise streaming for long requests, and it lets Claudia's chat window fill in as she types. It needs a hand-written SSE parser for either transport.
- **Official Anthropic C# SDK (`Anthropic` on NuGet, v13.x):** not recommended inside KSP. It targets .NET Standard 2.0, so it can load in principle. But it needs `HttpClient` (`System.Net.Http.dll`, which may not ship in KSP's `Managed` folder), `System.Text.Json` 10, `System.Net.ServerSentEvents`, `Microsoft.Extensions.AI.Abstractions`, and several BCL polyfills. All of these would go into a shared `GameData` AppDomain where other mods' copies can clash. Nobody has shown it running on Unity 2019.4 Mono. Use it only after a spike proves it loads. The default plan should be a thin hand-rolled client of about 300 lines: one endpoint, a handful of event types.

## Detail

### 1. Runtime: Unity 2019.4 with .NET 4.x Mono

- KSP 1.12.0 ("On Final Approach", June 2021) "Upgrade[d] project to Unity 2019.4.18f1 LTS". Sources: the 1.12.0 changelog and the KSP wiki 1.12 page, https://wiki.kerbalspaceprogram.com/wiki/1.12. The wiki page was behind an anti-bot wall during this research, so this claim was confirmed through search-result snippets of the wiki and the Steam changelog, not a direct read. 1.12.5 is a later patch on the same engine.
- KSP 1.8 moved the game off .NET 3.5 onto Unity's .NET 4.x runtime. Mods should target .NET Framework 4.x and compile against the assemblies in `KSP_x64_Data/Managed`. Sources: the KSP forum "1.8.0 modders notes", https://forum.kerbalspaceprogram.com/topic/188933-180-modders-notes, and the "Using .NET 5.0 for mods" thread, https://forum.kerbalspaceprogram.com/topic/204882-using-net-50-for-mods-is-possible-will-it-work-with-1122. The second thread says .NET Core / .NET 5+ plugins are impossible because KSP does not ship those runtimes. It also has anecdotal reports that assemblies built for frameworks above 4.7.2 hang or crash on load. **Uncertainty:** the forum pages returned 403 to direct fetches, so these claims come from search-result excerpts.
- Unity does not map its Mono fork to an upstream Mono version: "we pull the Mono source code and modify it". Source: Unity staff (JoshPeterson), https://discussions.unity.com/t/mono-versions-bundled-with-unity/726022. Don't reason from upstream Mono release notes.
- In the Unity player, `async`/`await` continuations resume on the main thread by default through `UnitySynchronizationContext`. The Unity API is not accessible off the main thread. Source: https://learn.microsoft.com/en-us/visualstudio/gamedev/unity/unity-scripting-upgrade.

### 2. TLS 1.2

- "From 2018.2, Unity provides full TLS 1.2 support on all platforms except WebGL", "via the UnityWebRequest API and all .NET 4.x APIs". Only .NET 3.5 and below have no TLS guarantees. Source: https://docs.unity3d.com/2019.4/Documentation/Manual/dotnetProfileSupport.html.
- In practice, KSP-AVC calls `https://api.github.com/repos/.../releases` through plain `HttpWebRequest`. It sets no `ServicePointManager.SecurityProtocol` and no certificate callback. GitHub has required TLS 1.2 since 2018. Source: `KSP-AVC/AddonInfo.cs`, `FetchRemoteData()`, https://github.com/linuxgurugamer/KSPAddonVersionChecker/blob/master/KSP-AVC/AddonInfo.cs.
- **Uncertainty:** I did not verify the root-certificate store behaviour on Linux and macOS KSP builds. Unity's TLS backend normally uses the OS store, but this should be smoke-tested on each OS.

### 3. Transport options

#### a. `UnityWebRequest` in a coroutine (main thread)

- In a coroutine, "you may Yield the result of the `Send()` call to wait for the request to complete". The coroutine yields; it does not block the frame. Sources: https://docs.unity3d.com/2019.4/Documentation/Manual/UnityWebRequest.html and https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Networking.UnityWebRequest.html. The docs also cover `timeout` ("attempt to abort after the number of seconds") and say properties are frozen after `SendWebRequest`.
- `UnityWebRequest` is a Unity API and must be created and used on the main thread. Creating one elsewhere throws "Create can only be called from the main thread". Sources: Unity's threading rule in the Microsoft Learn page above, and community reports at https://discussions.unity.com/t/can-only-be-called-from-the-main-thread/730817. The Unity 2019.4 scripting reference does not state the restriction itself.
- **KSP practice:** the KerbalX mod's API library (KXAPI) keeps a `MonoBehaviour` `RequestHandler`. It calls `StartCoroutine(transmit(...))`, does `yield return request.Send()`, then branches on `responseCode`. Source: https://github.com/Sujimichi/KXAPI/blob/master/RequestHander.cs.
- **Streaming:** `DownloadHandlerScript` is the base class for script-driven download handlers. Override `ReceiveData(byte[] data, int dataLength)`, which is "invoked as data is received from the remote server". That is the hook for incremental SSE parsing. Source: https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Networking.DownloadHandlerScript.html. **Uncertainty:** the docs don't say how big each callback's chunk is or how often it fires. Assume arbitrary chunk boundaries and buffer partial lines.
- POST with a raw JSON body: construct `new UnityWebRequest(url, "POST")` with `UploadHandlerRaw(Encoding.UTF8.GetBytes(json))` and set the headers with `SetRequestHeader`. Avoid `UnityWebRequest.Post(url, string)`, because it form-encodes the body.

#### b. `HttpWebRequest` / `HttpClient` on a worker thread

- KSP-AVC runs its work through `ThreadPool.QueueUserWorkItem(ProcessAddonPopulation)` (`AddonLibrary.cs`) and makes synchronous `HttpWebRequest.GetResponse()` calls from that worker thread. Its source still contains a disabled `#else` branch that busy-waited on `UnityWebRequest` with `Thread.Sleep` off the main thread. That is the anti-pattern this mod moved away from. Source: https://github.com/linuxgurugamer/KSPAddonVersionChecker/tree/master/KSP-AVC.
- `HttpClient` lives in `System.Net.Http.dll`. Under Unity's .NET 4.x profile that assembly "isn't referenced by default", so you must reference it explicitly. Source: https://learn.microsoft.com/en-us/visualstudio/gamedev/unity/unity-scripting-upgrade. **Open question:** does KSP 1.12.5's `KSP_x64_Data/Managed` folder actually contain `System.Net.Http.dll`? Unity player builds ship only the assemblies they reference, and no source I found confirms it either way. Check with `ls KSP_x64_Data/Managed/System.Net.Http.dll` on a real install. `HttpWebRequest` is in `System.dll`, which is certainly present.
- On a worker thread, streaming is easy: read `GetResponseStream()` line by line.
- Results must be marshalled back to the main thread, for example with a `ConcurrentQueue` drained in `Update()`, before they touch any KSP or Unity object.

### 4. Messages API constraints that affect the client

- Headers: `content-type: application/json`, `anthropic-version`, and the key in `x-api-key` or `Authorization: Bearer`. "The Claude API accepts a key in either header." Source: https://platform.claude.com/docs/en/api/migration-2026-10-21.
- API version `2026-10-21` is current as of this research; the SDKs moved to it with C# SDK 13.0.0. Under it, `temperature`, `top_p` and `top_k` return 400. Raw-HTTP callers choose the version themselves by setting the header. Source: same migration page.
- Long requests: "Consider using the streaming Messages API ... for long-running requests, especially those over 10 minutes". Avoid large `max_tokens` without streaming, because "some networks might drop idle connections". Mid-stream errors arrive as an `event: error` after a 200 response. Source: https://platform.claude.com/docs/en/api/errors.
- SSE event flow: `message_start`, then for each content block a `content_block_start`, one or more `content_block_delta` events, and a `content_block_stop`. Then `message_delta` and finally `message_stop`, with `ping` events possible anywhere. Delta types include `text_delta`, `input_json_delta` (tool use) and `thinking_delta`. Usage in `message_delta` is cumulative. Source: https://platform.claude.com/docs/en/build-with-claude/streaming.
- Retryable statuses (for a hand-rolled client to mimic the SDK): connection errors, 408, 409, 429 and 5xx including 529 `overloaded_error`. Sources: https://platform.claude.com/docs/en/api/errors and https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp.

### 5. JSON libraries

- **`JsonUtility`** (built into Unity): works only on `[Serializable]` plain classes and structs. "types like `Dictionary<>` are not supported". It cannot serialize top-level arrays or primitives, and it has no polymorphism: you deserialize to "common" fields and then deserialize again. It is safe to call from background threads. Source: https://docs.unity3d.com/2019.4/Documentation/Manual/JSONSerialization.html. That is a poor fit for Messages content blocks, which are a polymorphic `type`-tagged union, and for tool `input` objects of arbitrary shape.
- **Bundled source parsers are the KSP norm.** KXAPI compiles in `simpleJSON.cs` (https://github.com/Sujimichi/KXAPI/tree/master/assets/plugins). KSP-AVC compiles in MiniJSON (`KSP-AVC/Json.cs`). Either avoids DLL-version clashes.
- **Newtonsoft.Json:** the `netstandard2.0` or `net45` build loads on Unity 2019 Mono. Microsoft's Unity guide uses it as the worked NuGet-in-Unity example (link above). In KSP, other mods may ship their own copy, and the first assembly loaded wins, so a version mismatch is a real risk. **Uncertainty:** I found no canonical "shared Newtonsoft for KSP" package to depend on.
- **System.Text.Json:** on Unity 2019 it needs `System.Memory`, `System.Buffers` and `System.Runtime.CompilerServices.Unsafe`. Those collide with versions other packages pin. Sources: forum and issue-tracker reports, https://discussions.unity.com/t/systen-text-json-net-standard-2-0/868255 and https://forum.unity.com/threads/bug-1214643-2019-3-0f5-system-runtime-compilerservices-unsafe-clashes-with-imported-dll.816426/. Avoid it.

### 6. The official Anthropic C# SDK

- It is official from v10 onward; the latest version is 13.2.1. It requires ".NET Standard 2.0 or later". Streaming returns `IAsyncEnumerable` (`client.Messages.CreateStreaming`). It retries twice by default and has a 10-minute timeout. Sources: https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp and https://www.nuget.org/packages/Anthropic.
- Its package targets are `net8.0;net9.0;netstandard2.0`. On `netstandard2.0` the dependencies are `System.Text.Json` 10.0.6, `System.Net.ServerSentEvents` 10.0.1, `Microsoft.Extensions.AI.Abstractions` 10.5.1, `System.Collections.Immutable` 8, `System.Threading.Tasks.Extensions` 4.6.3 and `System.Diagnostics.DiagnosticSource` 8. Each of these pulls further dependencies. Sources: `src/Anthropic/Anthropic.csproj` and `src/Directory.Build.props`, https://github.com/anthropics/anthropic-sdk-csharp.
- It is built on `HttpClient` (`new HttpClient(new HttpClientHandler{...})` in `src/Anthropic/Core/ClientOptions.cs`). A custom `HttpClient` can be injected through `ClientOptions.HttpClient`. In theory you could inject a handler that wraps `UnityWebRequest`, but that defeats the purpose.
- **Assessment (inference, not tested):** it could plausibly run in KSP 1.12. The cost is about 10 or more extra DLLs in `GameData`, a dependency on `System.Net.Http.dll` being loadable, clashes with other mods' copies of `System.Memory` or `System.Text.Json`, and C# 8+ language features in our own code (`await foreach`). We get no benefit we need, because Claudia uses one endpoint. A hand-rolled client is smaller and has nothing to clash with.

## Recommendation for the decision ticket

Hand-roll a minimal Messages client inside the plugin:

1. `UnityWebRequest` POST from a coroutine on a long-lived `MonoBehaviour`, with `stream: true`, and a `DownloadHandlerScript` subclass that splits SSE lines and feeds a small event accumulator.
2. A bundled source-only JSON parser (SimpleJSON or similar) to read the polymorphic content blocks and tool-use inputs.
3. Retry with backoff on 429, 5xx and 529, and surface errors in Claudia's chat.

Fallback: if `DownloadHandlerScript` turns out to buffer badly in KSP, use `HttpWebRequest` on a worker thread with a main-thread queue, as KSP-AVC does.

## Open questions surfaced

- Does `KSP_x64_Data/Managed` in 1.12.5 contain `System.Net.Http.dll`? This decides whether `HttpClient`, and so the SDK, is even an option. Check on a real install.
- Does `DownloadHandlerScript.ReceiveData` deliver SSE chunks promptly in KSP's Unity build, or does it buffer? This needs a spike.
- Where does the player's API key live? Options include `settings.cfg` under `GameData`, an environment variable, or the OS keychain. How do we keep it out of KSP.log and out of mod-pack zips?
- Which JSON library: bundled SimpleJSON source or a Newtonsoft DLL? And how much of the Messages schema (tools, thinking blocks, structured outputs) does Claudia need?
- Which `anthropic-version` should the client pin, `2026-10-21` or `2023-06-01`, and how do we handle model and version drift over the mod's lifetime?
- How should the client deal with game pause, scene changes (a coroutine dies with its `MonoBehaviour`) and quicksave/quickload in the middle of a request?
- Root-certificate behaviour of Unity's TLS on Linux and macOS KSP builds needs a smoke test.
