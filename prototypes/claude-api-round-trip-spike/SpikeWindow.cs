// PROTOTYPE: throwaway Claude API round-trip spike. Test bench for Claude API round-trip spike in KSP (issue #21).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace ClaudiaApiSpike
{
    // Transport A: UnityWebRequest on the main thread. ReceiveData is called as bytes arrive (or not, if Unity buffers).
    public class SseDownloadHandler : DownloadHandlerScript
    {
        private readonly StreamRun run;
        public SseDownloadHandler(StreamRun run) : base(new byte[4096]) { this.run = run; }
        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return false;
            run.OnBytes(data, dataLength);
            return true;
        }
    }

    // Lives from the main menu onward and survives scene changes, so requests can be tested across them.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class SpikeWindow : MonoBehaviour
    {
        private Rect rect = new Rect(80, 60, 720, 600);
        private bool minimized;
        private Vector2 scroll;
        private StreamRun current;
        private UnityWebRequest currentUwr;
        private HttpWebRequest currentHwr;

        // Worker-thread output, drained on the main thread.
        private readonly object gate = new object();
        private readonly Queue<Action> fromWorker = new Queue<Action>();

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            GameEvents.onGameSceneLoadRequested.Add(s => SpikeLog.Log(string.Format("EVENT scene change requested -> {0} (run active: {1})", s, current != null && !current.Done)));
            GameEvents.onLevelWasLoaded.Add(s => SpikeLog.Log(string.Format("EVENT scene loaded {0} (run active: {1}, chunks so far: {2})", s, current != null && !current.Done, current == null ? 0 : current.ChunkMs.Count)));
            GameEvents.onGamePause.Add(() => SpikeLog.Log("EVENT game paused"));
            GameEvents.onGameUnpause.Add(() => SpikeLog.Log("EVENT game unpaused"));
            string key = Api.LoadKey();
            SpikeLog.Log(string.Format("=== spike loaded. Unity {0}, platform {1}, runtime {2}. Key file {3}: {4}",
                Application.unityVersion, Application.platform, Environment.Version, Api.KeyPath,
                key == null ? "MISSING" : ("length " + key.Length + (key.StartsWith("sk-ant-") ? ", starts sk-ant-" : ", UNEXPECTED PREFIX"))));
            SpikeLog.Log("ServicePointManager.SecurityProtocol at load = " + ServicePointManager.SecurityProtocol + " (" + (int)ServicePointManager.SecurityProtocol + ")");
        }

        private void Update()
        {
            while (true)
            {
                Action a;
                lock (gate) { if (fromWorker.Count == 0) break; a = fromWorker.Dequeue(); }
                a();
            }
        }

        private void OnGUI()
        {
            rect = GUILayout.Window(0x61706973, rect, Draw, "PROTOTYPE: Claudia Claude API spike");
        }

        private void Draw(int id)
        {
            if (GUILayout.Button(minimized ? "Expand" : "Minimize")) minimized = !minimized;
            if (minimized) { rect.height = 60; GUI.DragWindow(); return; }
            bool busy = current != null && !current.Done;

            GUILayout.BeginHorizontal();
            GUI.enabled = !busy;
            if (GUILayout.Button("A: UnityWebRequest stream")) StartCoroutine(RunUwr("A:UWR", Api.Body(Api.StreamPrompt, 4096, null), Api.LoadKey()));
            if (GUILayout.Button("B: HttpWebRequest stream")) RunHwr("B:HWR", Api.Body(Api.StreamPrompt, 4096, null), Api.LoadKey());
            GUI.enabled = busy;
            if (GUILayout.Button("Cancel")) Cancel();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !busy;
            if (GUILayout.Button("OR in TLS 1.2")) OrTls12();
            if (GUILayout.Button("Cache test (A, twice)")) StartCoroutine(CacheTest());
            if (GUILayout.Button("Bad key A")) StartCoroutine(RunUwr("A:UWR bad key", Api.Body("Say OK.", 64, null), "sk-ant-invalid"));
            if (GUILayout.Button("Bad key B")) RunHwr("B:HWR bad key", Api.Body("Say OK.", 64, null), "sk-ant-invalid");
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (current != null)
                GUILayout.Label(string.Format("{0}: {1}, {2} chunks, {3} ms\n{4}", current.Label, current.Outcome, current.ChunkMs.Count,
                    current.Clock.ElapsedMilliseconds, StreamRun.Tail(current.Text.ToString(), 400)));

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(330));
            GUILayout.Label(string.Join("\n", SpikeLog.Lines.Skip(Math.Max(0, SpikeLog.Lines.Count - 40)).ToArray()));
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private bool CheckKey(string key)
        {
            if (key != null) return true;
            SpikeLog.Log("No API key: create " + Api.KeyPath);
            return false;
        }

        private IEnumerator RunUwr(string label, string body, string key)
        {
            if (!CheckKey(key)) yield break;
            var run = current = new StreamRun(label);
            SpikeLog.Log(string.Format("START {0}, body {1} bytes", label, body.Length));
            var req = currentUwr = new UnityWebRequest(Api.Url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new SseDownloadHandler(run);
            req.SetRequestHeader("content-type", "application/json");
            req.SetRequestHeader("x-api-key", key);
            req.SetRequestHeader("anthropic-version", Api.Version);
            req.SetRequestHeader("anthropic-beta", Api.FallbackBeta);
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (run.HttpStatus == 0 && req.responseCode != 0)
                {
                    run.HttpStatus = req.responseCode;
                    run.RequestId = req.GetResponseHeader("request-id") ?? "";
                    SpikeLog.Log(string.Format("{0}: response headers at {1} ms, http {2}", label, run.Clock.ElapsedMilliseconds, req.responseCode));
                }
                yield return null;
            }
            run.HttpStatus = req.responseCode;
            run.RequestId = req.GetResponseHeader("request-id") ?? run.RequestId;
            run.Finish(req.isNetworkError ? "NETWORK ERROR " + req.error : req.isHttpError ? "HTTP ERROR " + req.error : "ok");
            req.Dispose();
            currentUwr = null;
        }

        private void RunHwr(string label, string body, string key)
        {
            if (!CheckKey(key)) return;
            var run = current = new StreamRun(label);
            SpikeLog.Log(string.Format("START {0}, body {1} bytes, SecurityProtocol={2}", label, body.Length, ServicePointManager.SecurityProtocol));
            new Thread(() =>
            {
                try
                {
                    var req = currentHwr = (HttpWebRequest)WebRequest.Create(Api.Url);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Headers["x-api-key"] = key;
                    req.Headers["anthropic-version"] = Api.Version;
                    req.Headers["anthropic-beta"] = Api.FallbackBeta;
                    req.Timeout = 600000;
                    req.ReadWriteTimeout = 600000;
                    req.AllowReadStreamBuffering = false;
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    HttpWebResponse resp;
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException we) when (we.Response != null) { resp = (HttpWebResponse)we.Response; }
                    long headersMs = run.Clock.ElapsedMilliseconds;
                    int status = (int)resp.StatusCode;
                    string rid = resp.Headers["request-id"] ?? "";
                    Post(() => { run.HttpStatus = status; run.RequestId = rid; SpikeLog.Log(string.Format("{0}: response headers at {1} ms, http {2}", label, headersMs, status)); });
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            long ms = run.Clock.ElapsedMilliseconds;
                            string l = line;
                            Post(() => run.OnLine(l, ms));
                        }
                    }
                    Post(() => run.Finish(status >= 400 ? "HTTP ERROR " + status : "ok"));
                }
                catch (Exception e)
                {
                    string msg = e.GetType().Name + ": " + e.Message + (e.InnerException != null ? " / inner " + e.InnerException.GetType().Name + ": " + e.InnerException.Message : "");
                    Post(() => run.Finish("EXCEPTION " + msg));
                }
                finally { currentHwr = null; }
            }) { IsBackground = true, Name = "ClaudiaApiSpike HWR" }.Start();
        }

        private void Post(Action a) { lock (gate) fromWorker.Enqueue(a); }

        private void Cancel()
        {
            SpikeLog.Log("CANCEL requested for " + (current == null ? "-" : current.Label));
            if (currentUwr != null) currentUwr.Abort();
            if (currentHwr != null) currentHwr.Abort();
        }

        private void OrTls12()
        {
            var before = ServicePointManager.SecurityProtocol;
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            SpikeLog.Log(string.Format("SecurityProtocol {0} ({1}) -> {2} ({3})", before, (int)before, ServicePointManager.SecurityProtocol, (int)ServicePointManager.SecurityProtocol));
        }

        private IEnumerator CacheTest()
        {
            string big = Api.BigSystem();
            yield return RunUwr("A:UWR cache 1", Api.Body("Reply with the single word OK.", 256, big), Api.LoadKey());
            yield return RunUwr("A:UWR cache 2", Api.Body("Reply with the single word OK.", 256, big), Api.LoadKey());
        }
    }
}
