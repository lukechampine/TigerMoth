using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("com.speedrun.tigermoth", "TigerMoth", "1.4.0")]
public class TigerMothPlugin : BaseUnityPlugin
{
    // ── Split definitions (hardcoded order) ───────────────
    private static readonly string[] SplitNames = { "Church", "Gift", "Tower", "End" };

    private static TigerMothPlugin _instance;

    // ── Harmony: block game's NewSplit after we take over ─
    [HarmonyPatch(typeof(SpeedrunSplits), "NewSplit")]
    private class PatchNewSplit
    {
        static bool Prefix(bool first)
        {
            return _instance == null || !_instance._runActive || first;
        }
    }

    // ── Harmony: capture EndRun for final split ───────────
    [HarmonyPatch(typeof(SpeedrunSplits), "EndRun")]
    private class PatchEndRun
    {
        static void Postfix()
        {
            if (_instance != null && _instance._runActive)
                _instance.OnEndRun();
        }
    }

    private static readonly KeyCode[] CpKeys =
        { KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5 };

    private static readonly KeyCode[] DumpKeys =
        { KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0 };

    // ── Saved state ───────────────────────────────────────
    private class SavedState
    {
        public Vector2 rbPosition;
        public Vector2 rbVelocity;
        public int jumps;
        public float chargingTime;
        public int facingDirection;
        public bool jumpCanceled;
        public bool hitstopActive;
        public bool queuedJump;
        public Vector2 velocityBeforeHitstop;

        public int currentSplitIndex;
        public float[] splitTimeValues;

        public bool extraJumpUsed;
        public float cameraTargetSize;

        public Vector3 cameraPosition;

        public Vector3 spiderPosition;
        public bool spiderCanFollow;

        public int animStateHash;
        public float animNormalizedTime;
    }

    private struct GhostFrame
    {
        public float x, y;
        public int animHash;
        public float animTime;
        public bool flipX;
    }

    // ── Fields ────────────────────────────────────────────
    private MothController _moth;
    private Rigidbody2D _rb;
    private GameObject _ghost;
    private Animator _ghostAnimator;

    // Ghost recording / playback
    private List<GhostFrame> _ghostRecording;
    private int[] _ghostSegmentStarts;
    private GhostFrame[] _ghostPlaybackFrames;   // PB ghost (full run)
    private GhostFrame[][] _goldGhostFrames;     // per-split gold ghosts
    private GhostFrame[] _ghostActivePlayback;   // current playback source
    private int _ghostPlaybackIndex;
    private SavedState _savedState;
    private SavedState[] _checkpoints;
    private bool _pendingLoad;

    // Reflection — MothController
    private FieldInfo _jumpsField;
    private FieldInfo _maxJumpsField;
    private FieldInfo _chargingTimeField;
    private FieldInfo _facingDirectionField;
    private FieldInfo _jumpCanceledField;
    private FieldInfo _hitstopField;
    private FieldInfo _queuedJumpField;
    private FieldInfo _velocityBeforeHitstopField;

    // Reflection — SpeedrunSplits / Split
    private FieldInfo _splitsRunningField;
    private FieldInfo _splitsListField;
    private FieldInfo _splitPrefabField;
    private FieldInfo _splitTickingField;
    private FieldInfo _splitTimeValueField;
    private FieldInfo _splitLabelField;
    private FieldInfo _splitTimerTextField;
    private FieldInfo _timerBackgroundField;

    // Reflection — ExtraJump
    private FieldInfo _extraJumpUsedField;
    private FieldInfo _extraJumpFunctionalChildField;

    // Reflection — SpiderBrain
    private FieldInfo _spiderCanFollowField;

    // Reflection — Animator
    private FieldInfo _animatorField;

    // Reflection — ScreenTransition, AdvancedCamera, SaveSystem (cached for load)
    private FieldInfo _maskField;
    private FieldInfo _pureTransformField;
    private MethodInfo _saveSystemClearMethod;
    private object _saveSystemGameBucket;

    // Cached scene objects
    private ExtraJump _extraJump;

    // Split management
    private List<Split> _managedSplits = new List<Split>();
    private int _currentSplitIndex = -1;
    private bool _runActive;

    // Area triggers — colliders for position-based split detection
    private Dictionary<string, Collider2D> _areaColliders = new Dictionary<string, Collider2D>();

    // Practice mode — entered on any load, exited on game reset (R)
    private bool _practiceMode;
    private int _practiceSkipIndex;

    // Camera zoom
    private float _defaultZoom;
    private int _zoomSteps;

    // Personal best / gold tracking
    private float[] _pbTotalTimes;
    private float[] _bestSegments;
    private float[] _bestSegmentsSnapshot; // frozen at run start for display deltas
    private float[] _runTotals;

    // Display data (read by OnGUI)
    private float[] _displaySegTimes;
    private float[] _displayTotalTimes;
    private bool[] _splitLocked;
    private bool[] _splitIsGold;

    // GUI
    private GUIStyle _headerStyle;
    private GUIStyle _keycapStyle;
    private GUIStyle _actionStyle;
    private GUIStyle _infoStyle;
    private Texture2D _keycapTex;
    private GUIStyle _splitNameStyle;
    private GUIStyle _splitTimeStyle;
    private GUIStyle _timerStyle;
    private Texture2D _panelBgTex;
    private GUIStyle _panelBgStyle;
    private Texture2D _splitActiveTex;
    private GUIStyle _splitActiveStyle;

    // ── Lifecycle ─────────────────────────────────────────

    void Awake()
    {
        _instance = this;
        new Harmony("com.speedrun.tigermoth").PatchAll();
        _checkpoints = CreateCheckpoints();
        LoadPB();
        LoadGhost();
        Logger.LogInfo("TigerMoth loaded");
    }

    void Update()
    {
        if (_moth == null)
        {
            var moth = FindObjectOfType<MothController>();
            if (moth == null)
                return;

            _moth = moth;

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _jumpsField = typeof(MothController).GetField("jumps", flags);
            _maxJumpsField = typeof(MothController).GetField("maxJumps", flags);
            _chargingTimeField = typeof(MothController).GetField("chargingTime", flags);
            _facingDirectionField = typeof(MothController).GetField("facingDirection", flags);
            _jumpCanceledField = typeof(MothController).GetField("jumpCanceled", flags);
            _hitstopField = typeof(MothController).GetField("hitstopActive", flags);
            _queuedJumpField = typeof(MothController).GetField("queuedJump", flags);
            _velocityBeforeHitstopField = typeof(MothController).GetField("velocityBeforeHitstop", flags);
            _animatorField = typeof(MothController).GetField("animator", flags);

            var rbField = typeof(MothController).GetField("rb", flags);
            if (rbField != null)
                _rb = (Rigidbody2D)rbField.GetValue(_moth);

            if (Camera.main != null)
            {
                _defaultZoom = Camera.main.orthographicSize;
                if (!_pendingLoad)
                {
                    _zoomSteps = 0;
                    _practiceMode = false;
                }
            }

            _splitsRunningField = typeof(SpeedrunSplits).GetField("running", flags);
            _splitsListField = typeof(SpeedrunSplits).GetField("splits", flags);
            _splitPrefabField = typeof(SpeedrunSplits).GetField("splitPrefab", flags | BindingFlags.Public);
            _splitTickingField = typeof(Split).GetField("ticking", flags);
            _splitTimeValueField = typeof(Split).GetField("timeValue", flags);
            _splitLabelField = typeof(Split).GetField("label", flags);
            _splitTimerTextField = typeof(Split).GetField("timerText", flags);
            _timerBackgroundField = typeof(Split).GetField("timerBackground", flags);

            _extraJumpUsedField = typeof(ExtraJump).GetField("used", flags);
            _extraJumpFunctionalChildField = typeof(ExtraJump).GetField("functionalChild", flags);
            _extraJump = FindObjectOfType<ExtraJump>();

            _spiderCanFollowField = typeof(SpiderBrain).GetField("canFollowPlayer", flags);

            _maskField = typeof(ScreenTransition).GetField("mask", flags | BindingFlags.Public);
            _pureTransformField = typeof(AdvancedCamera).GetField("pureTransform", flags);

            if (_saveSystemClearMethod == null)
            {
                var bucketType = typeof(SaveSystem).GetNestedType("BucketName");
                _saveSystemGameBucket = System.Enum.Parse(bucketType, "Game");
                _saveSystemClearMethod = typeof(SaveSystem).GetMethod("Clear");
            }

            // Find area colliders for position-based split triggers
            var areaNames = new[] { "The Ruined Church", "The Tower" };
            foreach (var lta in FindObjectsOfType<LocationTitleArea>())
            {
                foreach (var areaName in areaNames)
                {
                    if (lta.gameObject.name == areaName)
                    {
                        var col = lta.GetComponentInChildren<Collider2D>();
                        if (col != null)
                        {
                            _areaColliders[areaName] = col;
                            Logger.LogInfo("TigerMoth: found trigger collider for " + areaName);
                        }
                        else
                            Logger.LogWarning("TigerMoth: '" + areaName + "' has no Collider2D");
                    }
                }
            }

            Logger.LogInfo("TigerMoth found MothController");

            // Create ghost moth — build from scratch, sprites only
            if (_ghost == null)
            {
                _ghost = new GameObject("GhostMoth");
                CopySprites(_moth.transform, _ghost.transform);
                _ghost.transform.position = _moth.transform.position;

                // Add Animator for animation playback
                var mothAnimator = (Animator)_animatorField.GetValue(_moth);
                if (mothAnimator != null)
                {
                    _ghostAnimator = _ghost.AddComponent<Animator>();
                    _ghostAnimator.runtimeAnimatorController = mothAnimator.runtimeAnimatorController;
                    _ghostAnimator.avatar = mothAnimator.avatar;
                }

                // Hide until a run starts with playback data
                _ghost.SetActive(false);
                Logger.LogInfo("TigerMoth: ghost moth spawned");
            }
        }

        // Apply saved state after scene reload
        if (_pendingLoad)
        {
            _pendingLoad = false;
            // Skip the iris transition — just show the scene immediately
            var st = Singleton<ScreenTransition>.Instance;
            if (st != null && _maskField != null)
            {
                var mask = (GameObject)_maskField.GetValue(st);
                LeanTween.cancel(mask);
                mask.transform.localScale = Vector3.one * 10000f;
            }
            if (_savedState != null)
                ApplyState();
            return;
        }

        ManageSplits();

        // Ghost: record frame
        if (_runActive && _ghostRecording != null)
        {
            var mothAnimator = (Animator)_animatorField.GetValue(_moth);
            var info = mothAnimator != null
                ? mothAnimator.GetCurrentAnimatorStateInfo(0)
                : default(AnimatorStateInfo);
            _ghostRecording.Add(new GhostFrame
            {
                x = _moth.transform.position.x,
                y = _moth.transform.position.y,
                animHash = info.fullPathHash,
                animTime = info.normalizedTime,
                flipX = _moth.transform.eulerAngles.y > 90f
            });
        }

        // Ghost: playback frame
        if (_ghost != null && _ghost.activeSelf && _ghostActivePlayback != null)
        {
            if (_ghostPlaybackIndex < _ghostActivePlayback.Length)
            {
                var f = _ghostActivePlayback[_ghostPlaybackIndex];
                _ghost.transform.position = new Vector3(f.x, f.y, 0f);
                _ghost.transform.eulerAngles = new Vector3(0f, f.flipX ? 180f : 0f, 0f);
                if (_ghostAnimator != null && f.animHash != 0)
                    _ghostAnimator.Play(f.animHash, 0, f.animTime);
                _ghostPlaybackIndex++;
            }
            else
            {
                _ghost.SetActive(false);
            }
        }

        if (Input.GetKeyDown(KeyCode.H))
            SaveState();

        if (Input.GetKeyDown(KeyCode.I))
        {
            if (_savedState == null)
                Logger.LogWarning("TigerMoth: no saved state to load");
            else
            {
                _practiceMode = true;
                _practiceSkipIndex = _savedState.currentSplitIndex;
                ReloadAndRestore();
            }
        }

        // Checkpoint 1: reload scene at game start (practice first split)
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            _savedState = null;
            _practiceMode = true;
            _practiceSkipIndex = -1;
            ReloadAndRestore();
        }

        // Checkpoints: 2,3,4,5 → load hardcoded checkpoints
        for (int i = 0; i < CpKeys.Length; i++)
        {
            if (Input.GetKeyDown(CpKeys[i]))
            {
                _savedState = _checkpoints[i];
                _practiceMode = true;
                _practiceSkipIndex = _savedState.currentSplitIndex;
                ReloadAndRestore();
            }
        }

        // Dump state: 7,8,9,0 → write JSON to plugins/TigerMoth/
        for (int i = 0; i < 4; i++)
        {
            if (Input.GetKeyDown(DumpKeys[i]))
            {
                SaveState();
                DumpStateJson(i + 1);
            }
        }

        if (Input.GetKeyDown(KeyCode.LeftBracket))
        {
            _zoomSteps--;
            ApplyZoom();
        }

        if (Input.GetKeyDown(KeyCode.RightBracket))
        {
            _zoomSteps++;
            ApplyZoom();
        }
    }

    // ── Load state via scene reload ───────────────────────

    private void ReloadAndRestore()
    {
        _pendingLoad = true;
        _moth = null;
        _rb = null;
        _ghost = null;
        _ghostAnimator = null;
        _runActive = false;
        _managedSplits.Clear();
        _currentSplitIndex = -1;

        LeanTween.cancelAll();
        _saveSystemClearMethod.Invoke(null, new object[] { _saveSystemGameBucket });
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // ── Split management ──────────────────────────────────

    private void ManageSplits()
    {
        if (_splitsListField == null)
            return;
        var instance = Singleton<SpeedrunSplits>.Instance;
        if (instance == null)
            return;

        // Detect run was reset (splits destroyed externally, e.g. game restart)
        if (_runActive && (_managedSplits.Count == 0 || _managedSplits[0] == null))
        {
            _runActive = false;
            _managedSplits.Clear();
            _currentSplitIndex = -1;
            _ghostRecording = null;
            if (_ghost != null) _ghost.SetActive(false);
        }

        // Detect new run start (game created first split)
        if (!_runActive)
        {
            var splits = (List<Split>)_splitsListField.GetValue(instance);
            bool running = (bool)_splitsRunningField.GetValue(instance);
            if (running && splits.Count == 1)
                SetupManagedRun(splits);
            return;
        }

        // Check trigger for current split
        if (_currentSplitIndex >= 0 && _currentSplitIndex < SplitNames.Length && CheckTrigger())
            AdvanceSplit();
    }

    private bool CheckTrigger()
    {
        switch (_currentSplitIndex)
        {
            case 0: // "Church" — moth enters The Ruined Church
                return AreaOverlap("The Ruined Church");
            case 1: // "Gift" — ExtraJump collected
                return _extraJump != null && (bool)_extraJumpUsedField.GetValue(_extraJump);
            case 2: // "Tower" — moth enters The Tower
                return AreaOverlap("The Tower");
            default: // "End" — triggered by game's EndRun
                return false;
        }
    }

    private bool AreaOverlap(string areaName)
    {
        Collider2D col;
        return _areaColliders.TryGetValue(areaName, out col) && col.OverlapPoint(_rb.position);
    }

    private void SetupManagedRun(List<Split> gameSplits)
    {
        _runActive = true;
        _currentSplitIndex = 0;
        _managedSplits.Clear();
        _runTotals = new float[SplitNames.Length];
        _displaySegTimes = new float[SplitNames.Length];
        _displayTotalTimes = new float[SplitNames.Length];
        _splitLocked = new bool[SplitNames.Length];
        _splitIsGold = new bool[SplitNames.Length];
        _bestSegmentsSnapshot = _bestSegments != null
            ? (float[])_bestSegments.Clone() : null;

        // Destroy the game's split — we create all of ours from scratch
        Object.Destroy(gameSplits[0].gameObject);
        gameSplits.Clear();

        var splitsInstance = Singleton<SpeedrunSplits>.Instance;
        var prefab = (GameObject)_splitPrefabField.GetValue(splitsInstance);

        for (int i = 0; i < SplitNames.Length; i++)
        {
            var split = Object.Instantiate(prefab, splitsInstance.transform)
                .GetComponent<Split>();
            gameSplits.Add(split);
            _managedSplits.Add(split);

            _splitLabelField.SetValue(split, SplitNames[i]);
            HideSplitVisuals(split);

            if (i == 0)
            {
                _splitTickingField.SetValue(split, true);
                _splitTimeValueField.SetValue(split, 0f);
            }
            else
            {
                _splitTickingField.SetValue(split, false);
                _splitTimeValueField.SetValue(split, 0f);
            }
        }

        // Ghost recording + playback
        _ghostRecording = new List<GhostFrame>();
        _ghostSegmentStarts = new int[SplitNames.Length];
        _ghostPlaybackIndex = 0;

        if (_practiceMode)
        {
            // Practice: start gold ghost for first active split
            _ghostActivePlayback = null;
            if (_ghost != null) _ghost.SetActive(false);
            int firstSplit = _practiceSkipIndex < 0 ? 0 : _practiceSkipIndex + 1;
            if (_goldGhostFrames != null
                && firstSplit < _goldGhostFrames.Length
                && _goldGhostFrames[firstSplit] != null)
            {
                _ghostActivePlayback = _goldGhostFrames[firstSplit];
                _ghostPlaybackIndex = 0;
                if (_ghost != null) _ghost.SetActive(true);
            }
        }
        else
        {
            // Normal run: play PB ghost
            _ghostActivePlayback = _ghostPlaybackFrames;
            if (_ghost != null) _ghost.SetActive(_ghostPlaybackFrames != null);
        }

        Logger.LogInfo("TigerMoth: managed run started (" + SplitNames.Length + " splits)");
    }

    private void HideSplitVisuals(Split split)
    {
        // Disable the TMP text and background Image renderers.
        // The Split script still runs and tracks timeValue — we just draw
        // our own table in OnGUI instead.
        var text = _splitTimerTextField != null
            ? _splitTimerTextField.GetValue(split) as Behaviour : null;
        if (text != null)
            text.enabled = false;

        var bg = _timerBackgroundField != null
            ? _timerBackgroundField.GetValue(split) as Behaviour : null;
        if (bg != null)
            bg.enabled = false;
    }

    private void AdvanceSplit()
    {
        if (_currentSplitIndex >= _managedSplits.Count)
            return;

        // In practice mode, skip the split we loaded into — don't record a time
        if (_practiceMode && _currentSplitIndex == _practiceSkipIndex)
        {
            Logger.LogInfo(string.Format("TigerMoth: practice skip '{0}'",
                SplitNames[_currentSplitIndex]));
            _currentSplitIndex++;

            // Mark segment start for recording
            if (_ghostSegmentStarts != null && _currentSplitIndex < SplitNames.Length)
                _ghostSegmentStarts[_currentSplitIndex] = _ghostRecording != null ? _ghostRecording.Count : 0;

            if (_currentSplitIndex < _managedSplits.Count)
            {
                var next = _managedSplits[_currentSplitIndex];
                _splitTimeValueField.SetValue(next, 0f);
                _splitTickingField.SetValue(next, true);

                // Start gold ghost playback for this split
                if (_goldGhostFrames != null
                    && _currentSplitIndex < _goldGhostFrames.Length
                    && _goldGhostFrames[_currentSplitIndex] != null)
                {
                    _ghostActivePlayback = _goldGhostFrames[_currentSplitIndex];
                    _ghostPlaybackIndex = 0;
                    if (_ghost != null) _ghost.SetActive(true);
                }
            }
            return;
        }

        float totalTime = _managedSplits[_currentSplitIndex].Lock();
        float segmentTime;
        if (_practiceMode)
        {
            // In practice mode splits tick from 0, so totalTime IS the segment
            segmentTime = totalTime;
        }
        else
        {
            segmentTime = _currentSplitIndex == 0
                ? totalTime
                : totalTime - _runTotals[_currentSplitIndex - 1];
        }
        _runTotals[_currentSplitIndex] = totalTime;

        bool isGold = UpdateBestSegment(_currentSplitIndex, segmentTime);

        _displaySegTimes[_currentSplitIndex] = segmentTime;
        _displayTotalTimes[_currentSplitIndex] = totalTime;
        _splitLocked[_currentSplitIndex] = true;
        _splitIsGold[_currentSplitIndex] = isGold;

        // Save gold ghost segment
        if (isGold && _ghostRecording != null && _ghostSegmentStarts != null)
            SaveGoldSegment(_currentSplitIndex,
                _ghostSegmentStarts[_currentSplitIndex], _ghostRecording.Count);

        SavePB();

        Logger.LogInfo(string.Format("TigerMoth: split '{0}' seg={1} total={2}",
            SplitNames[_currentSplitIndex], FormatTime(segmentTime), FormatTime(totalTime)));

        _currentSplitIndex++;

        // Mark next segment start
        if (_ghostSegmentStarts != null && _currentSplitIndex < SplitNames.Length)
            _ghostSegmentStarts[_currentSplitIndex] = _ghostRecording != null ? _ghostRecording.Count : 0;

        if (_currentSplitIndex < _managedSplits.Count)
        {
            var next = _managedSplits[_currentSplitIndex];
            if (_practiceMode)
                _splitTimeValueField.SetValue(next, 0f);
            else
                _splitTimeValueField.SetValue(next, totalTime);
            _splitTickingField.SetValue(next, true);

            // Practice mode: start gold ghost playback for the new split
            if (_practiceMode && _goldGhostFrames != null
                && _currentSplitIndex < _goldGhostFrames.Length
                && _goldGhostFrames[_currentSplitIndex] != null)
            {
                _ghostActivePlayback = _goldGhostFrames[_currentSplitIndex];
                _ghostPlaybackIndex = 0;
                if (_ghost != null) _ghost.SetActive(true);
            }
        }
    }

    private void OnEndRun()
    {
        int lastIdx = SplitNames.Length - 1;
        if (_currentSplitIndex != lastIdx)
            return;
        if (lastIdx >= _managedSplits.Count || _managedSplits[lastIdx] == null)
            return;

        // In practice mode, if End is the skipped split, nothing to record
        if (_practiceMode && _currentSplitIndex == _practiceSkipIndex)
        {
            _currentSplitIndex = SplitNames.Length;
            return;
        }

        // Lock() was already called by the game's EndRun
        float totalTime = (float)_splitTimeValueField.GetValue(_managedSplits[lastIdx]);
        float segmentTime;
        if (_practiceMode)
            segmentTime = totalTime;
        else
            segmentTime = lastIdx == 0
                ? totalTime
                : totalTime - _runTotals[lastIdx - 1];
        _runTotals[lastIdx] = totalTime;

        bool isGold = UpdateBestSegment(lastIdx, segmentTime);

        _displaySegTimes[lastIdx] = segmentTime;
        _displayTotalTimes[lastIdx] = totalTime;
        _splitLocked[lastIdx] = true;
        _splitIsGold[lastIdx] = isGold;

        // Save gold ghost for last segment
        if (isGold && _ghostRecording != null && _ghostSegmentStarts != null)
            SaveGoldSegment(lastIdx, _ghostSegmentStarts[lastIdx], _ghostRecording.Count);

        // Check for PB only in normal mode (practice runs skip splits)
        if (!_practiceMode)
        {
            bool complete = true;
            for (int i = 0; i < SplitNames.Length; i++)
            {
                if (_runTotals[i] <= 0) { complete = false; break; }
            }
            if (complete && (_pbTotalTimes == null || totalTime < _pbTotalTimes[lastIdx]))
            {
                _pbTotalTimes = (float[])_runTotals.Clone();
                SaveGhost();
                Logger.LogInfo("TigerMoth: New PB! " + FormatTime(totalTime));
            }
        }

        SavePB();
        _currentSplitIndex = SplitNames.Length;
    }

    // ── Formatting helpers ────────────────────────────────

    private static string FormatTime(float t)
    {
        return t.ToString("F2");
    }

    // ── PB persistence ────────────────────────────────────

    private bool UpdateBestSegment(int idx, float segmentTime)
    {
        if (_bestSegments == null)
            _bestSegments = new float[SplitNames.Length];
        if (idx >= _bestSegments.Length)
            return false;

        if (_bestSegments[idx] <= 0 || segmentTime < _bestSegments[idx])
        {
            _bestSegments[idx] = segmentTime;
            return true;
        }
        return false;
    }

    private void LoadPB()
    {
        string path = Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_pb.txt");
        if (!File.Exists(path))
            return;

        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("pb:"))
                    _pbTotalTimes = ParseFloats(line.Substring(3));
                else if (line.StartsWith("gold:"))
                    _bestSegments = ParseFloats(line.Substring(5));
            }
            if (_pbTotalTimes != null)
                Logger.LogInfo("TigerMoth: PB loaded — " + FormatTime(_pbTotalTimes[_pbTotalTimes.Length - 1]));
            if (_bestSegments != null)
            {
                float sum = 0;
                for (int i = 0; i < _bestSegments.Length; i++) sum += _bestSegments[i];
                Logger.LogInfo("TigerMoth: Sum of best segments — " + FormatTime(sum));
            }
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to load PB: " + e.Message);
        }
    }

    private void SavePB()
    {
        string path = Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_pb.txt");
        try
        {
            var lines = new List<string>();
            if (_pbTotalTimes != null)
                lines.Add("pb:" + JoinFloats(_pbTotalTimes));
            if (_bestSegments != null)
                lines.Add("gold:" + JoinFloats(_bestSegments));
            if (lines.Count > 0)
                File.WriteAllLines(path, lines.ToArray());
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save PB: " + e.Message);
        }
    }

    private static float[] ParseFloats(string csv)
    {
        string[] parts = csv.Split(',');
        float[] result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]);
        return result;
    }

    private static string JoinFloats(float[] values)
    {
        var parts = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
            parts[i] = values[i].ToString("F4", CultureInfo.InvariantCulture);
        return string.Join(",", parts);
    }

    // ── Ghost persistence ─────────────────────────────────

    private static string GhostPath()
    {
        return Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_ghost.bin");
    }

    private static string GoldGhostPath(int splitIndex)
    {
        return Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_ghost_" + splitIndex + ".bin");
    }

    private static void WriteGhostFrames(string path, GhostFrame[] frames)
    {
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(frames.Length);
            for (int i = 0; i < frames.Length; i++)
            {
                w.Write(frames[i].x);
                w.Write(frames[i].y);
                w.Write(frames[i].animHash);
                w.Write(frames[i].animTime);
                w.Write((byte)(frames[i].flipX ? 1 : 0));
            }
        }
    }

    private static GhostFrame[] ReadGhostFrames(string path)
    {
        using (var fs = File.OpenRead(path))
        using (var r = new BinaryReader(fs))
        {
            int count = r.ReadInt32();
            var frames = new GhostFrame[count];
            for (int i = 0; i < count; i++)
            {
                frames[i].x = r.ReadSingle();
                frames[i].y = r.ReadSingle();
                frames[i].animHash = r.ReadInt32();
                frames[i].animTime = r.ReadSingle();
                frames[i].flipX = r.ReadByte() != 0;
            }
            return frames;
        }
    }

    private void LoadGhost()
    {
        try
        {
            string path = GhostPath();
            if (File.Exists(path))
            {
                _ghostPlaybackFrames = ReadGhostFrames(path);
                Logger.LogInfo("TigerMoth: PB ghost loaded (" + _ghostPlaybackFrames.Length + " frames)");
            }
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to load PB ghost: " + e.Message);
            _ghostPlaybackFrames = null;
        }

        _goldGhostFrames = new GhostFrame[SplitNames.Length][];
        for (int i = 0; i < SplitNames.Length; i++)
        {
            try
            {
                string path = GoldGhostPath(i);
                if (File.Exists(path))
                {
                    _goldGhostFrames[i] = ReadGhostFrames(path);
                    Logger.LogInfo("TigerMoth: gold ghost " + i + " loaded (" + _goldGhostFrames[i].Length + " frames)");
                }
            }
            catch (System.Exception e)
            {
                Logger.LogError("TigerMoth: failed to load gold ghost " + i + ": " + e.Message);
            }
        }
    }

    private void SaveGhost()
    {
        if (_ghostRecording == null || _ghostRecording.Count == 0)
            return;
        try
        {
            var frames = _ghostRecording.ToArray();
            WriteGhostFrames(GhostPath(), frames);
            _ghostPlaybackFrames = frames;
            Logger.LogInfo("TigerMoth: PB ghost saved (" + frames.Length + " frames)");
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save PB ghost: " + e.Message);
        }
    }

    private void SaveGoldSegment(int splitIndex, int startFrame, int endFrame)
    {
        if (_ghostRecording == null || startFrame >= endFrame)
            return;
        try
        {
            int count = endFrame - startFrame;
            var frames = new GhostFrame[count];
            _ghostRecording.CopyTo(startFrame, frames, 0, count);
            WriteGhostFrames(GoldGhostPath(splitIndex), frames);
            _goldGhostFrames[splitIndex] = frames;
            Logger.LogInfo("TigerMoth: gold ghost " + splitIndex + " saved (" + count + " frames)");
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save gold ghost " + splitIndex + ": " + e.Message);
        }
    }

    // ── Camera zoom ───────────────────────────────────────

    private void ApplyZoom()
    {
        float size = _defaultZoom + _zoomSteps * 2f;
        Singleton<AdvancedCamera>.Instance.targetSize = size;
        Camera.main.orthographicSize = size;
    }

    // ── Ghost moth helpers ──────────────────────────────────

    private static void CopySprites(Transform src, Transform dst)
    {
        var sr = src.GetComponent<SpriteRenderer>();

        // Only copy the moth body sprite, skip everything else
        if (src.name != "MothBodySprite" && sr != null) return;

        if (sr != null)
        {
            var ghostSr = dst.gameObject.AddComponent<SpriteRenderer>();
            ghostSr.sprite = sr.sprite;
            ghostSr.sortingLayerID = sr.sortingLayerID;
            ghostSr.sortingOrder = sr.sortingOrder;
            ghostSr.flipX = sr.flipX;
            ghostSr.flipY = sr.flipY;
            var c = sr.color;
            c.a = 0.4f;
            ghostSr.color = c;
        }

        foreach (Transform child in src)
        {
            var ghostChild = new GameObject(child.name);
            ghostChild.transform.SetParent(dst, false);
            ghostChild.transform.localPosition = child.localPosition;
            ghostChild.transform.localRotation = child.localRotation;
            ghostChild.transform.localScale = child.localScale;
            CopySprites(child, ghostChild.transform);
        }
    }

    // ── Hardcoded checkpoints ─────────────────────────────

    private static SavedState[] CreateCheckpoints()
    {
        return new[]
        {
            // 2: Before Church
            new SavedState
            {
                rbPosition = new Vector2(-13.344f, 8.546f),
                jumps = 1,
                facingDirection = -1,
                currentSplitIndex = 0,
                cameraTargetSize = 5.2f,
                cameraPosition = new Vector3(-14.333f, 11.806f, -10f),
                spiderPosition = new Vector3(-23.748f, 12.803f, 0f),
                spiderCanFollow = true,
                animStateHash = 618468143,
            },
            // 3: Before Gift
            new SavedState
            {
                rbPosition = new Vector2(-4.648f, 30.696f),
                jumps = 1,
                facingDirection = 1,
                currentSplitIndex = 1,
                cameraTargetSize = 5.2f,
                cameraPosition = new Vector3(-1.968f, 35.130f, -10f),
                spiderPosition = new Vector3(-26.367f, 28.627f, 0f),
                spiderCanFollow = true,
                animStateHash = 618468143,
            },
            // 4: Mid-Tower
            new SavedState
            {
                rbPosition = new Vector2(-27.123f, 98.755f),
                jumps = 2,
                facingDirection = 1,
                currentSplitIndex = 2,
                extraJumpUsed = true,
                cameraTargetSize = 6.2f,
                cameraPosition = new Vector3(-26.134f, 102.012f, -10f),
                spiderPosition = new Vector3(-21.193f, 11.682f, 0f),
                spiderCanFollow = true,
                animStateHash = 618468143,
            },
            // 5: Near End
            new SavedState
            {
                rbPosition = new Vector2(-6.102f, 153.932f),
                jumps = 2,
                facingDirection = -1,
                currentSplitIndex = 3,
                extraJumpUsed = true,
                cameraTargetSize = 8.5f,
                cameraPosition = new Vector3(-7.091f, 157.190f, -10f),
                spiderPosition = new Vector3(-26.468f, 26.954f, 0f),
                spiderCanFollow = true,
                animStateHash = 618468143,
            },
        };
    }

    // ── GUI ───────────────────────────────────────────────

    void OnGUI()
    {
        if (_moth == null)
            return;

        if (_headerStyle == null)
        {
            _headerStyle = new GUIStyle(GUI.skin.label);
            _headerStyle.fontSize = 34;
            _headerStyle.fontStyle = FontStyle.Bold;
            _headerStyle.normal.textColor = new Color(1f, 1f, 1f, 0.9f);

            _keycapTex = new Texture2D(1, 1);
            _keycapTex.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.15f));
            _keycapTex.Apply();
            _keycapStyle = new GUIStyle(GUI.skin.label);
            _keycapStyle.fontSize = 30;
            _keycapStyle.fontStyle = FontStyle.Bold;
            _keycapStyle.alignment = TextAnchor.MiddleCenter;
            _keycapStyle.normal.textColor = new Color(1f, 1f, 1f, 0.95f);
            _keycapStyle.normal.background = _keycapTex;

            _actionStyle = new GUIStyle(GUI.skin.label);
            _actionStyle.fontSize = 30;
            _actionStyle.alignment = TextAnchor.MiddleLeft;
            _actionStyle.normal.textColor = new Color(1f, 1f, 1f, 0.7f);

            _infoStyle = new GUIStyle(GUI.skin.label);
            _infoStyle.fontSize = 30;
            _infoStyle.alignment = TextAnchor.MiddleLeft;
            _infoStyle.normal.textColor = new Color(1f, 1f, 1f, 0.5f);

            _splitNameStyle = new GUIStyle(GUI.skin.label);
            _splitNameStyle.fontSize = 34;
            _splitNameStyle.alignment = TextAnchor.MiddleLeft;
            _splitNameStyle.normal.textColor = Color.white;

            _splitTimeStyle = new GUIStyle(GUI.skin.label);
            _splitTimeStyle.fontSize = 34;
            _splitTimeStyle.alignment = TextAnchor.MiddleRight;
            _splitTimeStyle.normal.textColor = Color.white;

            _timerStyle = new GUIStyle(GUI.skin.label);
            _timerStyle.fontSize = 48;
            _timerStyle.fontStyle = FontStyle.Bold;
            _timerStyle.alignment = TextAnchor.MiddleRight;
            _timerStyle.normal.textColor = Color.white;

            _panelBgTex = new Texture2D(1, 1);
            _panelBgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
            _panelBgTex.Apply();
            _panelBgStyle = new GUIStyle();
            _panelBgStyle.normal.background = _panelBgTex;

            _splitActiveTex = new Texture2D(1, 1);
            _splitActiveTex.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.2f));
            _splitActiveTex.Apply();
            _splitActiveStyle = new GUIStyle();
            _splitActiveStyle.normal.background = _splitActiveTex;
        }

        // ── TigerMoth overlay (top-left) ──
        DrawOverlay();

        // ── Split timer table (top-right) ──
        if (_runActive && _managedSplits.Count > 0)
            DrawSplitTable();
    }

    private void DrawOverlay()
    {
        const float pad = 10f;
        const float rowH = 44f;
        const float keycapW = 56f;
        const float keycapH = 44f;
        const float gap = 12f;
        const float actionW = 200f;
        const float padR = 16f;
        const float tableW = keycapW + gap + actionW + padR;

        string[][] bindings = new[] {
            new[] { "H", "Save" },
            new[] { "I", "Load" },
            new[] { "1-5", "Checkpoints" },
            new[] { "[", "Zoom in" },
            new[] { "]", "Zoom out" },
        };
        float tableH = rowH + bindings.Length * rowH + rowH; // header + rows + info

        float tableX = 10f;
        float tableY = 10f;

        // Background
        GUI.Box(new Rect(tableX, tableY, tableW + pad * 2, tableH + pad * 2),
            GUIContent.none, _panelBgStyle);

        float cx = tableX + pad;
        float cy = tableY + pad;

        // Header
        GUI.Label(new Rect(cx, cy, 400, rowH), "TigerMoth  v1.4.0", _headerStyle);
        cy += rowH;

        // Keybindings
        for (int i = 0; i < bindings.Length; i++)
        {
            float ky = cy + (rowH - keycapH) * 0.5f;
            GUI.Box(new Rect(cx, ky, keycapW, keycapH), bindings[i][0], _keycapStyle);
            GUI.Label(new Rect(cx + keycapW + gap, cy, actionW, rowH),
                bindings[i][1], _actionStyle);
            cy += rowH;
        }

        // Info line
        string zoomStr = _zoomSteps == 0 ? "0" : (_zoomSteps > 0 ? "+" + _zoomSteps : _zoomSteps.ToString());
        GUI.Label(new Rect(cx, cy, tableW, rowH), "Zoom: " + zoomStr, _infoStyle);
    }

    private static readonly Color ColorAhead = new Color(0.27f, 1f, 0.27f);
    private static readonly Color ColorBehind = new Color(1f, 0.27f, 0.27f);
    private static readonly Color ColorGold = new Color(1f, 0.84f, 0f);
    private static readonly Color ColorGray = new Color(0.5f, 0.5f, 0.5f);

    private void DrawSplitTable()
    {
        const float nameW = 140f;
        const float deltaWNormal = 140f;
        const float deltaWPractice = 160f;
        const float segW = 130f;
        const float totalW = 130f;
        const float rowH = 46f;
        const float timerH = 60f;
        const float timerGap = 8f;
        const float pad = 6f;

        float deltaW = _practiceMode ? deltaWPractice : deltaWNormal;
        float tableW = _practiceMode
            ? nameW + deltaW + segW
            : nameW + deltaW + segW + totalW;
        const float infoH = 36f;
        float headerH = _practiceMode ? rowH : 0f;
        bool hasGolds = _bestSegmentsSnapshot != null
            && _bestSegmentsSnapshot.Length >= SplitNames.Length;
        float tableH = headerH + SplitNames.Length * rowH + timerGap + timerH
            + (hasGolds ? infoH : 0f);

        float tableX = Screen.width - tableW - pad * 2 - 10f;
        float tableY = 10f;

        // Background
        GUI.Box(new Rect(tableX, tableY, tableW + pad * 2, tableH + pad * 2),
            GUIContent.none, _panelBgStyle);

        float cx = tableX + pad;
        float cy = tableY + pad;

        // Practice mode header
        if (_practiceMode)
        {
            GUI.Label(new Rect(cx, cy, tableW, rowH), "Practice Mode", _headerStyle);
            cy += rowH;
        }

        // ── Split rows (static — no ticking inline) ──
        for (int i = 0; i < SplitNames.Length; i++)
        {
            float rowY = cy + i * rowH;
            float colX = cx;
            bool hasGold = _bestSegmentsSnapshot != null && i < _bestSegmentsSnapshot.Length
                && _bestSegmentsSnapshot[i] > 0;
            bool hasPb = _pbTotalTimes != null && i < _pbTotalTimes.Length
                && _pbTotalTimes[i] > 0;
            bool locked = _splitLocked != null && i < _splitLocked.Length
                && _splitLocked[i];
            bool isSkipped = _practiceMode && i <= _practiceSkipIndex;

            // Highlight active row
            if (i == _currentSplitIndex && !locked)
                GUI.Box(new Rect(cx, rowY, tableW, rowH), GUIContent.none, _splitActiveStyle);

            // Name
            GUI.Label(new Rect(colX, rowY, nameW, rowH), SplitNames[i], _splitNameStyle);
            colX += nameW;

            if (isSkipped && !locked)
            {
                // Skipped/prior splits in practice mode — show gold times in gray
                colX += deltaW;
                var orig = GUI.color;
                GUI.color = ColorGray;
                GUI.Label(new Rect(colX, rowY, segW, rowH),
                    hasGold ? FormatTime(_bestSegmentsSnapshot[i]) : "--", _splitTimeStyle);
                GUI.color = orig;
            }
            else if (locked)
            {
                // Completed split — show actual delta + times
                if (_practiceMode)
                {
                    if (hasGold)
                        DrawDelta(colX, rowY, deltaW, rowH,
                            _displaySegTimes[i] - _bestSegmentsSnapshot[i],
                            _splitIsGold != null && _splitIsGold[i]);
                    colX += deltaW;
                    GUI.Label(new Rect(colX, rowY, segW, rowH),
                        FormatTime(_displaySegTimes[i]), _splitTimeStyle);
                }
                else
                {
                    if (hasPb)
                        DrawDelta(colX, rowY, deltaW, rowH,
                            _displayTotalTimes[i] - _pbTotalTimes[i],
                            _splitIsGold != null && _splitIsGold[i]);
                    colX += deltaW;
                    GUI.Label(new Rect(colX, rowY, segW, rowH),
                        FormatTime(_displaySegTimes[i]), _splitTimeStyle);
                    colX += segW;
                    GUI.Label(new Rect(colX, rowY, totalW, rowH),
                        FormatTime(_displayTotalTimes[i]), _splitTimeStyle);
                }
            }
            else
            {
                // Future/active split — show PB values in gray, or "--"
                // For the active split, show live delta when within 5s of PB
                bool isActive = i == _currentSplitIndex && i < _managedSplits.Count
                    && _managedSplits[i] != null;
                float liveTime = isActive
                    ? (float)_splitTimeValueField.GetValue(_managedSplits[i]) : 0f;

                if (_practiceMode)
                {
                    float goldSeg = hasGold ? _bestSegmentsSnapshot[i] : 0f;
                    if (isActive && hasGold && liveTime >= goldSeg - 5f)
                        DrawDelta(colX, rowY, deltaW, rowH, liveTime - goldSeg, false, true);
                    colX += deltaW;

                    var orig = GUI.color;
                    GUI.color = ColorGray;
                    GUI.Label(new Rect(colX, rowY, segW, rowH),
                        hasGold ? FormatTime(goldSeg) : "--", _splitTimeStyle);
                    GUI.color = orig;
                }
                else
                {
                    float pbTotal = hasPb ? _pbTotalTimes[i] : 0f;
                    if (isActive && hasPb && liveTime >= pbTotal - 5f)
                        DrawDelta(colX, rowY, deltaW, rowH, liveTime - pbTotal, false, true);
                    colX += deltaW;

                    float pbSeg = 0f;
                    if (hasPb)
                        pbSeg = i == 0 ? _pbTotalTimes[0]
                            : _pbTotalTimes[i] - _pbTotalTimes[i - 1];

                    var orig = GUI.color;
                    GUI.color = ColorGray;
                    GUI.Label(new Rect(colX, rowY, segW, rowH),
                        hasPb ? FormatTime(pbSeg) : "--", _splitTimeStyle);
                    colX += segW;
                    GUI.Label(new Rect(colX, rowY, totalW, rowH),
                        hasPb ? FormatTime(pbTotal) : "--", _splitTimeStyle);
                    GUI.color = orig;
                }
            }
        }

        // ── Bottom live timer ──
        float timerY = cy + SplitNames.Length * rowH + timerGap;
        if (_currentSplitIndex >= 0 && _currentSplitIndex < _managedSplits.Count
            && _managedSplits[_currentSplitIndex] != null)
        {
            float liveTime = (float)_splitTimeValueField.GetValue(
                _managedSplits[_currentSplitIndex]);
            GUI.Label(new Rect(cx, timerY, tableW, timerH),
                FormatTime(liveTime), _timerStyle);
        }
        else if (_currentSplitIndex >= SplitNames.Length && _displayTotalTimes != null)
        {
            // Run finished — show final time
            float finalTime = _practiceMode
                ? _displaySegTimes[SplitNames.Length - 1]
                : _displayTotalTimes[SplitNames.Length - 1];
            GUI.Label(new Rect(cx, timerY, tableW, timerH),
                FormatTime(finalTime), _timerStyle);
        }

        // ── Best Possible Time ──
        if (hasGolds)
        {
            float bpt = 0f;
            for (int i = 0; i < SplitNames.Length; i++)
                bpt += _bestSegmentsSnapshot[i];
            float bptY = timerY + timerH;
            var orig = GUI.color;
            GUI.color = ColorGray;
            GUI.Label(new Rect(cx, bptY, tableW, infoH), "Best Possible Time", _infoStyle);
            GUI.Label(new Rect(cx, bptY, tableW, infoH), FormatTime(bpt), _splitTimeStyle);
            GUI.color = orig;
        }
    }

    private void DrawDelta(float x, float y, float w, float h, float delta, bool isGold,
        bool live = false)
    {
        Color c = isGold ? ColorGold : (delta <= 0 ? ColorAhead : ColorBehind);
        string sign = delta >= 0 ? "+" : "\u2212";
        string secs = sign + Mathf.Abs(delta).ToString("F0");
        string decs = "." + Mathf.Abs(delta).ToString("F2").Substring(
            Mathf.Abs(delta).ToString("F2").IndexOf('.') + 1);
        float decW = _splitTimeStyle.CalcSize(new GUIContent(decs)).x;

        var orig = GUI.color;
        GUI.color = c;
        // Seconds right-aligned, leaving room for decimals on the right
        GUI.Label(new Rect(x, y, w - decW, h), secs, _splitTimeStyle);
        if (!live)
            GUI.Label(new Rect(x + w - decW, y, decW, h), decs, _splitTimeStyle);
        GUI.color = orig;
    }

    // ── Save / Load ───────────────────────────────────────

    private void SaveState()
    {
        if (_rb == null)
            return;

        _savedState = new SavedState
        {
            rbPosition = _rb.position,
            rbVelocity = _rb.velocity,
            jumps = (int)_jumpsField.GetValue(_moth),
            chargingTime = (float)_chargingTimeField.GetValue(_moth),
            facingDirection = (int)_facingDirectionField.GetValue(_moth),
            jumpCanceled = (bool)_jumpCanceledField.GetValue(_moth),
            hitstopActive = (bool)_hitstopField.GetValue(_moth),
            queuedJump = (bool)_queuedJumpField.GetValue(_moth),
            velocityBeforeHitstop = (Vector2)_velocityBeforeHitstopField.GetValue(_moth),
            currentSplitIndex = _currentSplitIndex,
            splitTimeValues = new float[_managedSplits.Count],
            extraJumpUsed = _extraJump != null && (bool)_extraJumpUsedField.GetValue(_extraJump),
            cameraTargetSize = Singleton<AdvancedCamera>.Instance.targetSize,
            cameraPosition = Singleton<AdvancedCamera>.Instance.transform.position,
            spiderPosition = Singleton<SpiderBrain>.Instance.transform.position,
            spiderCanFollow = (bool)_spiderCanFollowField.GetValue(Singleton<SpiderBrain>.Instance),
        };

        // Animator state
        var animator = (Animator)_animatorField.GetValue(_moth);
        if (animator != null)
        {
            var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
            _savedState.animStateHash = stateInfo.fullPathHash;
            _savedState.animNormalizedTime = stateInfo.normalizedTime;
        }

        for (int i = 0; i < _managedSplits.Count; i++)
            _savedState.splitTimeValues[i] = (float)_splitTimeValueField.GetValue(_managedSplits[i]);

        Logger.LogInfo(string.Format("TigerMoth saved state at ({0:F2}, {1:F2}) split={2}",
            _savedState.rbPosition.x, _savedState.rbPosition.y, _currentSplitIndex));
    }

    private void DumpStateJson(int slot)
    {
        if (_savedState == null)
            return;

        var s = _savedState;
        var ci = CultureInfo.InvariantCulture;
        string json = string.Format(ci,
@"{{
  ""rbPosition"": [{0}, {1}],
  ""rbVelocity"": [{2}, {3}],
  ""jumps"": {4},
  ""chargingTime"": {5},
  ""facingDirection"": {6},
  ""jumpCanceled"": {7},
  ""hitstopActive"": {8},
  ""queuedJump"": {9},
  ""velocityBeforeHitstop"": [{10}, {11}],
  ""currentSplitIndex"": {12},
  ""extraJumpUsed"": {13},
  ""cameraTargetSize"": {14},
  ""cameraPosition"": [{15}, {16}, {17}],
  ""spiderPosition"": [{18}, {19}, {20}],
  ""spiderCanFollow"": {21},
  ""animStateHash"": {22},
  ""animNormalizedTime"": {23}
}}",
            s.rbPosition.x, s.rbPosition.y,
            s.rbVelocity.x, s.rbVelocity.y,
            s.jumps, s.chargingTime, s.facingDirection,
            s.jumpCanceled.ToString().ToLower(),
            s.hitstopActive.ToString().ToLower(),
            s.queuedJump.ToString().ToLower(),
            s.velocityBeforeHitstop.x, s.velocityBeforeHitstop.y,
            s.currentSplitIndex,
            s.extraJumpUsed.ToString().ToLower(),
            s.cameraTargetSize,
            s.cameraPosition.x, s.cameraPosition.y, s.cameraPosition.z,
            s.spiderPosition.x, s.spiderPosition.y, s.spiderPosition.z,
            s.spiderCanFollow.ToString().ToLower(),
            s.animStateHash, s.animNormalizedTime);

        string dir = Path.Combine(BepInEx.Paths.PluginPath, "TigerMoth");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "checkpoint" + slot + ".json");
        File.WriteAllText(path, json);
        Logger.LogInfo("TigerMoth: dumped state to " + path);
    }

    private void ApplyState()
    {
        if (_savedState == null || _rb == null)
            return;

        // Physics body
        _rb.position = _savedState.rbPosition;
        _rb.velocity = _savedState.rbVelocity;
        _rb.angularVelocity = 0f;

        // Transform — z is always 0; rotation derived from facingDirection
        _moth.transform.position = new Vector3(_savedState.rbPosition.x, _savedState.rbPosition.y, 0f);
        _moth.transform.eulerAngles = new Vector3(0f, _savedState.facingDirection == 1 ? 0f : 180f, 0f);

        // MothController state
        _jumpsField.SetValue(_moth, _savedState.jumps);
        _maxJumpsField.SetValue(_moth, _savedState.extraJumpUsed ? 2 : 1);
        _chargingTimeField.SetValue(_moth, _savedState.chargingTime);
        _facingDirectionField.SetValue(_moth, _savedState.facingDirection);
        _jumpCanceledField.SetValue(_moth, _savedState.jumpCanceled);
        _hitstopField.SetValue(_moth, _savedState.hitstopActive);
        _queuedJumpField.SetValue(_moth, _savedState.queuedJump);
        _velocityBeforeHitstopField.SetValue(_moth, _savedState.velocityBeforeHitstop);

        // Restore splits
        var splitsInstance = Singleton<SpeedrunSplits>.Instance;
        _splitsRunningField.SetValue(splitsInstance, true);
        var splitsList = (List<Split>)_splitsListField.GetValue(splitsInstance);
        var splitPrefab = (GameObject)_splitPrefabField.GetValue(splitsInstance);

        foreach (var split in splitsList)
            Object.Destroy(split.gameObject);
        splitsList.Clear();
        _managedSplits.Clear();

        // Rebuild tracking arrays
        _runTotals = new float[SplitNames.Length];
        _displaySegTimes = new float[SplitNames.Length];
        _displayTotalTimes = new float[SplitNames.Length];
        _splitLocked = new bool[SplitNames.Length];
        _splitIsGold = new bool[SplitNames.Length];
        _bestSegmentsSnapshot = _bestSegments != null
            ? (float[])_bestSegments.Clone() : null;

        for (int i = 0; i < _savedState.currentSplitIndex; i++)
        {
            if (_savedState.splitTimeValues != null && i < _savedState.splitTimeValues.Length)
                _runTotals[i] = _savedState.splitTimeValues[i];
        }

        for (int i = 0; i < SplitNames.Length; i++)
        {
            float timeValue = (_savedState.splitTimeValues != null && i < _savedState.splitTimeValues.Length)
                ? _savedState.splitTimeValues[i]
                : 0f;
            bool ticking = (i == _savedState.currentSplitIndex) && !_practiceMode;

            var newSplit = Object.Instantiate(splitPrefab, splitsInstance.transform).GetComponent<Split>();
            splitsList.Add(newSplit);
            _managedSplits.Add(newSplit);

            _splitLabelField.SetValue(newSplit, SplitNames[i]);
            _splitTimeValueField.SetValue(newSplit, timeValue);
            _splitTickingField.SetValue(newSplit, ticking);
            HideSplitVisuals(newSplit);

            if (i < _savedState.currentSplitIndex && !_practiceMode)
            {
                newSplit.Lock();
                _splitTimeValueField.SetValue(newSplit, timeValue);

                float seg = i == 0 ? timeValue : timeValue - _runTotals[i - 1];
                _displaySegTimes[i] = seg;
                _displayTotalTimes[i] = timeValue;
                _splitLocked[i] = true;
                _splitIsGold[i] = _bestSegments != null && i < _bestSegments.Length
                    && _bestSegments[i] > 0 && seg <= _bestSegments[i];
            }
        }

        _currentSplitIndex = _savedState.currentSplitIndex;
        _runActive = _savedState.currentSplitIndex >= 0;

        // Restore ExtraJump powerup state
        if (_extraJump != null)
        {
            bool currentlyUsed = (bool)_extraJumpUsedField.GetValue(_extraJump);
            if (_savedState.extraJumpUsed && !currentlyUsed)
            {
                _extraJumpUsedField.SetValue(_extraJump, true);
                var child = (GameObject)_extraJumpFunctionalChildField.GetValue(_extraJump);
                if (child != null)
                    Object.Destroy(child);
            }
        }

        // Restore spider state
        var spider = Singleton<SpiderBrain>.Instance;
        if (spider != null)
        {
            spider.transform.position = _savedState.spiderPosition;
            _spiderCanFollowField.SetValue(spider, _savedState.spiderCanFollow);
        }

        // Restore animator state
        var animator = (Animator)_animatorField.GetValue(_moth);
        if (animator != null && _savedState.animStateHash != 0)
            animator.Play(_savedState.animStateHash, 0, _savedState.animNormalizedTime);

        // Restore camera position and zoom
        var cam = Singleton<AdvancedCamera>.Instance;
        float zoom = _savedState.cameraTargetSize + _zoomSteps * 2f;
        cam.targetSize = zoom;
        Camera.main.orthographicSize = zoom;
        cam.transform.position = _savedState.cameraPosition;
        if (_pureTransformField != null)
        {
            var pure = (Transform)_pureTransformField.GetValue(cam);
            if (pure != null)
                pure.position = _savedState.cameraPosition;
        }

        Logger.LogInfo(string.Format("TigerMoth: restored state at ({0:F2}, {1:F2}) split={2}",
            _savedState.rbPosition.x, _savedState.rbPosition.y, _currentSplitIndex));
    }
}
