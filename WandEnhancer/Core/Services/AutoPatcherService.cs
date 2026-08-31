using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WandEnhancer.Models;
using WandEnhancer.Utils;
using WandEnhancer.View.MainWindow;

namespace WandEnhancer.Core.Services
{
    /// <summary>
    /// Ultimate auto-repair service.
    ///
    /// Wand/Squirrel drops a brand new `app-*` folder (with a pristine, unpatched
    /// app.asar and no version.dll) every time it updates. A previously applied patch
    /// therefore silently disappears and the app runs unpatched until the user
    /// remembers to re-run the patcher.
    ///
    /// This service keeps Wand patched across updates by watching the install root
    /// for new/updated `app-*` folders and, when the running config enables auto
    /// repair, re-applying the last selected patch vectors automatically.
    /// </summary>
    public static class AutoPatcherService
    {
        private static readonly string StateFileName = "wand-enhancer-state.json";
        private static readonly string StatePath = Path.Combine(Common.GetCurrentDir(), StateFileName);

        private static volatile bool _busy;
        private static FileSystemWatcher _watcher;
        private static Action<string, ELogType> _logger;

        public static string LastPatchedRoot { get; private set; }
        public static string LastPatchedHash { get; private set; }

        public static bool Initialize(Action<string, ELogType> logger)
        {
            _logger = logger;
            LoadState();
            return true;
        }

        public static void StartWatch(Action<string, ELogType> logger)
        {
            _logger = logger;
            try
            {
                var weaveConfig = Extensions.FindWeMod();
                if (weaveConfig == null)
                {
                    return;
                }

                var installRoot = Directory.GetParent(weaveConfig.RootDirectory)?.FullName;
                if (string.IsNullOrEmpty(installRoot) || !Directory.Exists(installRoot))
                {
                    return;
                }

                _watcher = new FileSystemWatcher(installRoot)
                {
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false
                };

                _watcher.Created += OnChanged;
                _watcher.Changed += OnChanged;
                _watcher.Renamed += OnRenamed;
                _watcher.EnableRaisingEvents = true;

                _logger?.Invoke($"Watching '{installRoot}' for Wand updates (auto-repair active).", ELogType.Info);
            }
            catch (Exception e)
            {
                _logger?.Invoke($"Auto-repair watcher could not start: {e.Message}", ELogType.Warn);
            }
        }

        public static void StopWatch()
        {
            if (_watcher == null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        private static void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.Name != null && e.Name.IndexOf("app-", StringComparison.OrdinalIgnoreCase) == 0)
            {
                OnUpdateDetected();
            }
        }

        private static void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (e.Name != null && e.Name.IndexOf("app-", StringComparison.OrdinalIgnoreCase) == 0)
            {
                OnUpdateDetected();
            }
        }

        private static async void OnUpdateDetected()
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            try
            {
                // Give Squirrel a moment to finish unpacking before we touch files.
                await Task.Delay(3000);

                var config = Extensions.FindWeMod();
                if (config == null)
                {
                    return;
                }

                if (!NeedsRepair(config.RootDirectory))
                {
                    return;
                }

                _logger?.Invoke($"Detected a new Wand version at '{config.RootDirectory}'. Re-applying patch...", ELogType.Info);

                var vectors = LoadPatchVectors();
                if (vectors == null || vectors.Count == 0)
                {
                    _logger?.Invoke("Auto-repair skipped: no patch vectors saved.", ELogType.Warn);
                    return;
                }

                var patchConfig = new PatchConfig
                {
                    PatchTypes = vectors,
                    CustomScriptPaths = LoadCustomScriptPaths(),
                    AutoApplyPatches = true
                };

                try
                {
                    var logSink = _logger ?? new Action<string, ELogType>((m, t) => { });
                    new Enhancer(config, logSink, patchConfig).Patch();
                    LastPatchedRoot = config.RootDirectory;
                    LastPatchedHash = ComputeFingerprint(config.RootDirectory);
                    SaveState();
                    SavePatchVectors(patchConfig);
                    _logger?.Invoke("Auto-repair applied the patch to the updated Wand.", ELogType.Success);
                }
                catch (Exception e)
                {
                    _logger?.Invoke($"Auto-repair failed to patch updated Wand: {e.Message}", ELogType.Error);
                }
            }
            finally
            {
                _busy = false;
            }
        }

        public static void RecordAppliedPatch(WeModConfig config, PatchConfig patchConfig)
        {
            LastPatchedRoot = config.RootDirectory;
            LastPatchedHash = ComputeFingerprint(config.RootDirectory);
            SaveState();
            SavePatchVectors(patchConfig);
        }

        private static bool NeedsRepair(string rootDirectory)
        {
            // If we've never patched anything, let the manual UI drive.
            if (string.IsNullOrEmpty(LastPatchedRoot))
            {
                return false;
            }

            // If this root is different from the last one we patched, treat as update.
            if (!string.Equals(LastPatchedRoot, rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Same folder, but the content changed (re-install / re-extract).
            var fingerprint = ComputeFingerprint(rootDirectory);
            return fingerprint != LastPatchedHash;
        }

        private static string ComputeFingerprint(string rootDirectory)
        {
            try
            {
                var asar = Path.Combine(rootDirectory, "resources", "app.asar");
                if (File.Exists(asar))
                {
                    return Common.ComputeSha256Hash(new FileInfo(asar).LastWriteTimeUtc.Ticks.ToString());
                }

                var unpacked = Path.Combine(rootDirectory, "resources", "app.asar.unpacked");
                if (Directory.Exists(unpacked))
                {
                    return Common.ComputeSha256Hash(Directory.GetLastWriteTimeUtc(unpacked).Ticks.ToString());
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }

        private static void SaveState()
        {
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    lastPatchedRoot = LastPatchedRoot,
                    lastPatchedHash = LastPatchedHash,
                    savedAt = DateTime.UtcNow
                });
                File.WriteAllText(StatePath, json);
            }
            catch
            {
                // non-critical
            }
        }

        private static void LoadState()
        {
            try
            {
                if (!File.Exists(StatePath))
                {
                    return;
                }

                var json = File.ReadAllText(StatePath);
                var state = Newtonsoft.Json.JsonConvert.DeserializeObject<AutoPatchState>(json);
                LastPatchedRoot = state?.LastPatchedRoot;
                LastPatchedHash = state?.LastPatchedHash;
            }
            catch
            {
                // non-critical
            }
        }

        private static readonly string VectorsPath = Path.Combine(Common.GetCurrentDir(), "wand-enhancer-vectors.json");

        private static void SavePatchVectors(PatchConfig patchConfig)
        {
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    vectors = patchConfig.PatchTypes.Select(x => (int)x).ToArray(),
                    customScriptPaths = patchConfig.CustomScriptPaths
                });
                File.WriteAllText(VectorsPath, json);
            }
            catch
            {
                // non-critical
            }
        }

        public static System.Collections.Generic.HashSet<EPatchType> LoadPatchVectors()
        {
            try
            {
                if (!File.Exists(VectorsPath))
                {
                    return null;
                }

                var payload = Newtonsoft.Json.JsonConvert.DeserializeObject<VectorsPayload>(File.ReadAllText(VectorsPath));
                if (payload?.Vectors == null)
                {
                    return null;
                }

                return new System.Collections.Generic.HashSet<EPatchType>(
                    payload.Vectors.Where(v => Enum.IsDefined(typeof(EPatchType), v)).Select(v => (EPatchType)v));
            }
            catch
            {
                return null;
            }
        }

        public static System.Collections.Generic.List<string> LoadCustomScriptPaths()
        {
            try
            {
                if (!File.Exists(VectorsPath))
                {
                    return new System.Collections.Generic.List<string>();
                }

                var payload = Newtonsoft.Json.JsonConvert.DeserializeObject<VectorsPayload>(File.ReadAllText(VectorsPath));
                return payload?.CustomScriptPaths ?? new System.Collections.Generic.List<string>();
            }
            catch
            {
                return new System.Collections.Generic.List<string>();
            }
        }

        private sealed class AutoPatchState
        {
            public string LastPatchedRoot { get; set; }
            public string LastPatchedHash { get; set; }
        }

        private sealed class VectorsPayload
        {
            public int[] Vectors { get; set; }
            public System.Collections.Generic.List<string> CustomScriptPaths { get; set; }
        }
    }
}
