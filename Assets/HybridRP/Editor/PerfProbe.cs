using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// GPU timing of the showcase camera in Play mode, per URP / HybridRP pass, from GPU
    /// <see cref="ProfilerRecorder"/>s (one sample per rendered frame). Driven in four CLI steps because entering Play mode reloads the
    /// domain and an eval body can't wait for frames:
    /// <code>
    /// PerfProbe.Prepare();                                    // 1920x1080 Game view, run in background, enter Play
    /// PerfProbe.Begin("s2_ref", "Overview/Front,Face/LowAmbient");   // queue cases, returns at once
    /// PerfProbe.Status();                                     // poll until it reports the result file
    /// PerfProbe.Finish();                                     // exit Play, restore Game view and settings
    /// </code>
    /// A run is reported INVALID instead of producing numbers when frames did not advance, the camera
    /// marker resolved too few GPU samples, or its readings repeat: the Phase 4 attempt produced
    /// exactly such stale values when the Editor stopped repainting. Frames are capped (120 fps by
    /// default) during the run: uncapped, the Editor outruns the GPU timer queries and close-up
    /// framings resolve almost no samples. Numbers are Editor Play-mode GPU times for comparing
    /// passes and A/B states on this machine, not shipping-player measurements.
    /// </summary>
    public static class PerfProbe
    {
        const string StateKey = "HybridRP.PerfProbe.Restore";
        const string ResolutionLabel = "HybridRP Perf 1080p";

        // URP 17.0 sampler names that carry GPU timing under Render Graph. Names without blocks in a
        // given configuration are reported as absent, not as zero.
        static readonly string[] BuiltInSamplers =
        {
            "Draw Main Light Shadowmap", "MainLightShadow", "Draw Additional Lights Shadowmap",
            "DepthPrepass", "DrawDepthNormalPrepass", "SSAO", "DrawOpaqueObjects", "DrawSkybox",
            "CopyDepth", "CopyColor", "DrawTransparentObjects", "DrawMotionVectors", "ColorGradingLUT",
            "RG_UberPost", "UberPostProcess", "RG_SMAAEdgeDetection", "RG_SMAABlendWeight",
            "RG_SMAANeighborhoodBlend", "RG_FinalBlit",
        };

        [Serializable]
        class RestoreState
        {
            public bool runInBackground;
            public int gameViewSizeIndex = -1;
            public string previousTabType;
        }

        [Serializable]
        public class SamplerResult
        {
            public string name;
            public int samples;
            public int missing;
            public int repeated;
            public float meanMs, medianMs, p95Ms, minMs, maxMs;
        }

        [Serializable]
        public class CaseResult
        {
            public string bookmark, preset;
            public bool valid;
            public string invalidReason;
            public int frames, firstFrame, lastFrame, gpuSamples, maxResolveGapFrames;
            public float wallSeconds, fps;
            public int screenWidth, screenHeight, cameraPixelWidth, cameraPixelHeight;
            public List<SamplerResult> samplers = new List<SamplerResult>();
        }

        [Serializable]
        public class RunResult
        {
            public string label, date, gpu, api, unity;
            public int warmupFrames, measureFrames, frameRateCap;
            public bool valid;
            public List<CaseResult> cases = new List<CaseResult>();
        }

        class Run
        {
            public RunResult result;
            public Queue<(string bookmark, string preset)> pending;
            public (string bookmark, string preset) current;
            public bool hasCurrent;
            public int warmupLeft, lastFrame = -1, caseStartFrame;
            public double caseStartTime;
            public List<string> names;
            public string cameraRecorder;
            public ProfilerRecorder primer;
            public int lastResolved, lastResolveFrame, maxResolveGap;
            public bool primed;
            public int primeStartFrame = -1;
            public int previousTargetFrameRate, previousVSync;
            public double lastFrameTime = -1;
            public ShowcaseRig.State rigState;
            public bool ended;
            public Dictionary<string, ProfilerRecorder> recorders = new Dictionary<string, ProfilerRecorder>();
            public string outputPath, error;
        }

        static Run s_run;

        // The first samples after a recorder starts can belong to frames already in flight.
        const int LeadingSamplesDropped = 4;
        const int MaxFrameFactor = 4;
        const double StallSeconds = 10;

        static string CameraSamplerName(Camera camera) =>
            $"UniversalRenderPipeline.RenderSingleCameraInternal: {camera.name}";

        /// <summary>Fixes the Game view at 1920x1080, keeps Play mode running unfocused and enters Play mode.</summary>
        public static string Prepare(int width = 1920, int height = 1080)
        {
            if (EditorApplication.isPlaying)
                return "already playing";
            if (string.IsNullOrEmpty(SessionState.GetString(StateKey, null)))
            {
                var state = new RestoreState
                {
                    runInBackground = PlayerSettings.runInBackground,
                    gameViewSizeIndex = GetGameViewSizeIndex(),
                    previousTabType = VisibleTabSharingGameView(),
                };
                SessionState.SetString(StateKey, JsonUtility.ToJson(state));
            }
            PlayerSettings.runInBackground = true;
            // Play mode only advances frames while the Game view is the visible tab of its dock
            // area; docked behind the Scene view it stays at frame 1 even with Run In Background.
            var gameView = FindGameView();
            if (gameView != null)
            {
                gameView.Show();
                gameView.Focus();
            }
            PlayModeWindow.SetCustomRenderingResolution((uint)width, (uint)height, ResolutionLabel);
            EditorApplication.EnterPlaymode();
            return $"entering Play mode at {width}x{height}";
        }

        /// <summary>
        /// Queues "bookmark/preset" cases (comma-separated; either part may be empty) and starts
        /// measuring. Each case applies its framing, renders <paramref name="warmupFrames"/> frames,
        /// then records <paramref name="frames"/> frames.
        /// </summary>
        public static string Begin(string label, string cases, int frames = 300, int warmupFrames = 120,
                                   string extraSamplers = null, int frameRateCap = 120)
        {
            if (!EditorApplication.isPlaying)
                return "ERROR: not in Play mode; call Prepare() first";
            if (s_run != null && s_run.outputPath == null && s_run.error == null)
                return "ERROR: a run is already in progress";
            var rig = ShowcaseRig.Find();
            if (rig == null || rig.Camera == null)
                return "ERROR: no ShowcaseRig camera";

            var names = new List<string> { CameraSamplerName(rig.Camera) };
            names.AddRange(BuiltInSamplers);
            var all = new List<string>();
            Sampler.GetNames(all);
            names.AddRange(all.Where(n => n.StartsWith("HybridRP.", StringComparison.Ordinal)));
            if (!string.IsNullOrWhiteSpace(extraSamplers))
                names.AddRange(extraSamplers.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            names = names.Distinct().ToList();

            var run = new Run
            {
                result = new RunResult
                {
                    label = label,
                    date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    gpu = SystemInfo.graphicsDeviceName,
                    api = SystemInfo.graphicsDeviceType.ToString(),
                    unity = Application.unityVersion,
                    warmupFrames = warmupFrames,
                    measureFrames = frames,
                    frameRateCap = frameRateCap,
                },
                previousTargetFrameRate = Application.targetFrameRate,
                previousVSync = QualitySettings.vSyncCount,
                pending = new Queue<(string, string)>(),
                names = names,
                rigState = rig.Capture(),
            };
            foreach (string item in cases.Split(','))
            {
                string trimmed = item.Trim();
                if (trimmed.Length == 0)
                    continue;
                string[] parts = trimmed.Split('/');
                run.pending.Enqueue((parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : ""));
            }
            if (run.pending.Count == 0)
                return "ERROR: no cases";
            run.result.measureFrames = Math.Max(10, frames);
            // The first GPU recorders created in a Play session stay empty: GPU profiling is switched
            // on by them a few frames later and does not attach to recorders that already exist. This
            // one stays alive for the whole run; cases start only once it has delivered GPU samples.
            run.primer = new ProfilerRecorder(ProfilerCategory.Scripts, CameraSamplerName(rig.Camera), 8,
                ProfilerRecorderOptions.GpuRecorder | ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.StartImmediately);
            // vSyncCount belongs to the quality settings asset; only touch it when it differs.
            if (QualitySettings.vSyncCount != 0)
                QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = frameRateCap;
            s_run = run;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return $"started {run.pending.Count} case(s), {names.Count} markers";
        }

        public static string Status()
        {
            if (s_run == null)
                return "idle";
            if (s_run.error != null)
                return "ERROR: " + s_run.error;
            if (s_run.outputPath != null)
                return Summary(s_run.result) + "\n" + s_run.outputPath;
            int done = s_run.result.cases.Count;
            int resolvedNow = s_run.cameraRecorder != null && s_run.recorders.TryGetValue(s_run.cameraRecorder, out var cam) ? cam.Count : 0;
            string phase = s_run.warmupLeft > 0 ? $"warm-up {s_run.warmupLeft} left"
                : $"{resolvedNow}/{s_run.result.measureFrames} samples after {Time.frameCount - s_run.caseStartFrame} frames";
            return $"running case {done + 1}: {s_run.current.bookmark}/{s_run.current.preset} {phase}";
        }

        /// <summary>Exits Play mode (if needed) and restores the Game view size and run-in-background flag.</summary>
        public static string Finish()
        {
            EditorApplication.update -= Tick;
            EndRun();
            if (EditorApplication.isPlaying)
            {
                // Restoration has to happen after the domain reload that leaving Play mode triggers.
                EditorApplication.ExitPlaymode();
                return "exiting Play mode; call Finish() again to restore settings";
            }
            string json = SessionState.GetString(StateKey, null);
            if (string.IsNullOrEmpty(json))
                return "nothing to restore";
            var state = JsonUtility.FromJson<RestoreState>(json);
            PlayerSettings.runInBackground = state.runInBackground;
            if (state.gameViewSizeIndex >= 0)
                SetGameViewSizeIndex(state.gameViewSizeIndex);
            RestoreVisibleTab(state.previousTabType);
            SessionState.EraseString(StateKey);
            return $"restored runInBackground={state.runInBackground}, Game view size index {state.gameViewSizeIndex}";
        }

        static void Tick()
        {
            var run = s_run;
            if (run == null || run.outputPath != null || run.error != null)
            {
                EditorApplication.update -= Tick;
                return;
            }
            try
            {
                if (!EditorApplication.isPlaying)
                    throw new InvalidOperationException("Play mode ended during the run");
                // The Editor ticks more often than the player renders; only real frames count.
                int frame = Time.frameCount;
                double now = EditorApplication.timeSinceStartup;
                if (frame == run.lastFrame)
                {
                    // Paused Play mode or an Editor that stopped rendering: fail, don't wait forever.
                    if (run.lastFrameTime >= 0 && now - run.lastFrameTime > StallSeconds)
                        throw new InvalidOperationException($"frames stopped advancing for {StallSeconds} s at frame {frame}");
                    return;
                }
                run.lastFrame = frame;
                run.lastFrameTime = now;

                if (!run.primed)
                {
                    if (run.primeStartFrame < 0)
                        run.primeStartFrame = frame;
                    if (run.primer.Count > 0 && run.primer.LastValue > 0)
                        run.primed = true;
                    else if (frame - run.primeStartFrame > 600)
                        throw new InvalidOperationException("GPU profiling delivered no samples in 600 frames");
                    return;
                }
                if (!run.hasCurrent)
                {
                    if (run.pending.Count == 0)
                    {
                        Complete(run);
                        return;
                    }
                    StartCase(run, run.pending.Dequeue());
                    return;
                }
                if (run.warmupLeft > 0)
                {
                    if (--run.warmupLeft == 0)
                    {
                        run.caseStartFrame = frame;
                        run.caseStartTime = EditorApplication.timeSinceStartup;
                        // Reset also stops a recorder, so restart it to begin the measured window.
                        foreach (var recorder in run.recorders.Values)
                        {
                            recorder.Reset();
                            recorder.Start();
                        }
                        run.lastResolved = 0;
                        run.lastResolveFrame = frame;
                        run.maxResolveGap = 0;
                    }
                    return;
                }
                // GPU queries resolve several frames late and the Editor occasionally drops a
                // batch, so wait for enough resolved camera samples instead of a frame count, with
                // a bound so a profiler that stopped delivering fails the case instead of hanging.
                int elapsed = frame - run.caseStartFrame;
                int resolved = run.cameraRecorder != null && run.recorders.TryGetValue(run.cameraRecorder, out var cam) ? cam.Count : 0;
                if (resolved != run.lastResolved)
                {
                    run.lastResolved = resolved;
                    run.lastResolveFrame = frame;
                }
                run.maxResolveGap = Math.Max(run.maxResolveGap, frame - run.lastResolveFrame);
                if (resolved >= run.result.measureFrames + LeadingSamplesDropped || elapsed >= run.result.measureFrames * MaxFrameFactor)
                    EndCase(run, frame);
            }
            catch (Exception e)
            {
                run.error = e.Message;
                EditorApplication.update -= Tick;
                EndRun();
                Debug.LogError("[PerfProbe] " + e);
            }
        }

        static void StartCase(Run run, (string bookmark, string preset) item)
        {
            var rig = ShowcaseRig.Find();
            if (rig == null)
                throw new InvalidOperationException("ShowcaseRig disappeared");
            if (!string.IsNullOrEmpty(item.preset) && !rig.ApplyLighting(item.preset))
                throw new ArgumentException($"Unknown lighting preset '{item.preset}'");
            if (!string.IsNullOrEmpty(item.bookmark) && !rig.Apply(item.bookmark))
                throw new ArgumentException($"Unknown camera bookmark '{item.bookmark}'");
            run.current = item;
            run.hasCurrent = true;
            run.warmupLeft = Math.Max(1, run.result.warmupFrames);
            StartRecorders(run);
        }

        static void StartRecorders(Run run)
        {
            DisposeRecorders(run);
            // GPU samples resolve a few frames late; spare capacity keeps the newest frames.
            int capacity = run.result.measureFrames * MaxFrameFactor + 16;
            // Markers can only be GPU-recorded as a per-frame sum (several scopes may share a name).
            const ProfilerRecorderOptions options = ProfilerRecorderOptions.GpuRecorder |
                ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.StartImmediately;
            foreach (string name in run.names)
                run.recorders[name] = new ProfilerRecorder(ProfilerCategory.Scripts, name, capacity, options);
            var rig = ShowcaseRig.Find();
            run.cameraRecorder = rig != null && rig.Camera != null ? CameraSamplerName(rig.Camera) : null;
        }

        static void DisposeRecorders(Run run)
        {
            foreach (var recorder in run.recorders.Values)
                recorder.Dispose();
            run.recorders.Clear();
        }

        static void EndCase(Run run, int frame)
        {
            var rig = ShowcaseRig.Find();
            var camera = rig != null ? rig.Camera : null;
            double wall = EditorApplication.timeSinceStartup - run.caseStartTime;
            int frames = frame - run.caseStartFrame;
            var result = new CaseResult
            {
                bookmark = run.current.bookmark,
                preset = run.current.preset,
                frames = frames,
                firstFrame = run.caseStartFrame,
                lastFrame = frame,
                wallSeconds = (float)wall,
                fps = wall > 0 ? (float)(frames / wall) : 0f,
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                cameraPixelWidth = camera != null ? camera.pixelWidth : 0,
                cameraPixelHeight = camera != null ? camera.pixelHeight : 0,
            };

            var reasons = new List<string>();
            string cameraName = camera != null ? CameraSamplerName(camera) : null;
            foreach (var pair in run.recorders)
            {
                var recorder = pair.Value;
                recorder.Stop();
                var raw = new List<long>();
                for (int i = LeadingSamplesDropped; i < recorder.Count; i++)
                    raw.Add(recorder.GetSample(i).Value);
                if (raw.All(v => v == 0))
                {
                    if (pair.Key == cameraName)
                        reasons.Add("camera recorder returned no GPU samples");
                    continue; // pass not present in this configuration
                }
                int repeated = 0;
                for (int i = 1; i < raw.Count; i++)
                    if (raw[i] == raw[i - 1] && raw[i] != 0)
                        repeated++;
                // A zero inside a pass that is otherwise present is an unresolved query, not 0 ms.
                // A marker that is zero most of the time does no GPU work in this configuration.
                int missing = raw.Count(v => v == 0);
                if (pair.Key != cameraName && missing > raw.Count / 2)
                    continue;
                raw = raw.Where(v => v != 0).ToList();
                if (pair.Key == cameraName)
                {
                    result.gpuSamples = raw.Count;
                    if (raw.Count < run.result.measureFrames)
                        reasons.Add($"only {raw.Count}/{run.result.measureFrames} camera GPU samples resolved in {frames} frames");
                }
                var ms = raw.Select(ns => ns / 1e6).OrderBy(v => v).ToList();
                result.samplers.Add(new SamplerResult
                {
                    name = pair.Key,
                    samples = ms.Count,
                    missing = missing,
                    repeated = repeated,
                    meanMs = (float)ms.Average(),
                    medianMs = (float)Percentile(ms, 0.5),
                    p95Ms = (float)Percentile(ms, 0.95),
                    minMs = (float)ms[0],
                    maxMs = (float)ms[ms.Count - 1],
                });
                // The whole-camera time practically never repeats to the nanosecond; repeats there
                // mean old frames were served again (the Phase 4 failure). Sub-0.1 ms passes repeat
                // legitimately because GPU timestamps are quantised, so they are only reported.
                if (pair.Key == cameraName && repeated > ms.Count / 20)
                    reasons.Add($"camera total: {repeated}/{ms.Count} repeated readings (stale)");
                if (pair.Key == cameraName && missing > ms.Count / 10)
                    reasons.Add($"camera total: {missing} unresolved samples");
            }
            if (frames < run.result.measureFrames)
                reasons.Add($"only {frames} frames advanced");
            result.maxResolveGapFrames = run.maxResolveGap;
            // The Editor resolves GPU timings for a subset of frames (they come in bursts); that
            // only lowers the sample count. A long stall means the measurement window was not
            // sampled evenly.
            if (run.maxResolveGap > frames / 3)
                reasons.Add($"GPU samples stalled for {run.maxResolveGap} of {frames} frames");
            if (camera != null && (result.cameraPixelWidth != 1920 || result.cameraPixelHeight != 1080))
                reasons.Add($"camera rendered {result.cameraPixelWidth}x{result.cameraPixelHeight}, not 1920x1080");
            result.valid = reasons.Count == 0;
            result.invalidReason = string.Join("; ", reasons);
            result.samplers = result.samplers.OrderByDescending(s => s.meanMs).ToList();
            DisposeRecorders(run);
            run.result.cases.Add(result);
            run.hasCurrent = false;
        }

        static void Complete(Run run)
        {
            EditorApplication.update -= Tick;
            EndRun();
            run.result.valid = run.result.cases.Count > 0 && run.result.cases.All(c => c.valid);
            string directory = Path.Combine(HybridCapture.CaptureDirectory, "perf");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, run.result.label + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(run.result, true));
            run.outputPath = path;
        }

        /// <summary>Releases the recorders and restores frame pacing and the rig. Safe to call repeatedly.</summary>
        static void EndRun()
        {
            var run = s_run;
            if (run == null || run.ended)
                return;
            run.ended = true;
            DisposeRecorders(run);
            run.primer.Dispose();
            Application.targetFrameRate = run.previousTargetFrameRate;
            if (QualitySettings.vSyncCount != run.previousVSync)
                QualitySettings.vSyncCount = run.previousVSync;
            var rig = ShowcaseRig.Find();
            if (rig != null)
                rig.Restore(run.rigState);
        }

        static double Percentile(List<double> sorted, double p)
        {
            double index = p * (sorted.Count - 1);
            int lo = (int)Math.Floor(index), hi = (int)Math.Ceiling(index);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo);
        }

        /// <summary>Readable table of one result (also returned by <see cref="Status"/> when done).</summary>
        public static string Summary(RunResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{result.label}: {(result.valid ? "VALID" : "INVALID")} ({result.gpu}, {result.api}, " +
                          $"{result.measureFrames} frames after {result.warmupFrames} warm-up, cap {result.frameRateCap} fps)");
            foreach (var c in result.cases)
            {
                sb.AppendLine($"[{c.bookmark}/{c.preset}] {(c.valid ? "valid" : "INVALID: " + c.invalidReason)} " +
                              $"{c.cameraPixelWidth}x{c.cameraPixelHeight} {c.fps:F1} fps, frames {c.firstFrame}-{c.lastFrame}, " +
                              $"{c.gpuSamples} GPU samples, max gap {c.maxResolveGapFrames}");
                foreach (var s in c.samplers)
                    sb.AppendLine($"  {s.name,-70} mean {s.meanMs,7:F3}  med {s.medianMs,7:F3}  p95 {s.p95Ms,7:F3} ms" +
                                  (s.repeated > 0 || s.missing > 0 ? $"  (rep {s.repeated}, miss {s.missing})" : ""));
            }
            return sb.ToString();
        }

        // The Game view's size dropdown has no public API; reflection keeps the user's choice restorable.
        static Type GameViewType => typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");

        static EditorWindow FindGameView()
        {
            var type = GameViewType;
            return type == null ? null : Resources.FindObjectsOfTypeAll(type).FirstOrDefault() as EditorWindow;
        }

        // The tab that was in front of the Game view in the same dock area, so Finish can put it back.
        static string VisibleTabSharingGameView()
        {
            var view = FindGameView();
            if (view == null || !view.docked)
                return null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var parent = typeof(EditorWindow).GetField("m_Parent", flags)?.GetValue(view);
            var actual = parent?.GetType().GetProperty("actualView", flags)?.GetValue(parent) as EditorWindow;
            return actual != null && actual != view ? actual.GetType().AssemblyQualifiedName : null;
        }

        static void RestoreVisibleTab(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return;
            var type = Type.GetType(typeName);
            var window = type != null ? Resources.FindObjectsOfTypeAll(type).FirstOrDefault() as EditorWindow : null;
            window?.Show();
            window?.Focus();
        }

        static int GetGameViewSizeIndex()
        {
            var view = FindGameView();
            var property = GameViewType?.GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return view != null && property != null ? (int)property.GetValue(view) : -1;
        }

        static void SetGameViewSizeIndex(int index)
        {
            var view = FindGameView();
            var method = GameViewType?.GetMethod("SizeSelectionCallback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (view != null && method != null)
                method.Invoke(view, new object[] { index, null });
        }
    }
}
