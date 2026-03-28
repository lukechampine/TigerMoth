using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("com.speedrun.tigermoth", "TigerMoth", Version)]
public class TigerMothPlugin : BaseUnityPlugin
{
    const string Version = "1.3.0";

    // ── Split definitions (hardcoded order) ───────────────
    private static readonly string[] SplitNames = { "Church", "Gift", "Tower", "End" };

    // Area trigger names (must match LocationTitleArea game object names)
    private const string AreaChurch = "The Ruined Church";
    private const string AreaTower = "The Tower";

    private const float LiveDeltaLeadTime = 5f;
    private const int GhostRecordingInitialCapacity = 8192;

    private static TigerMothPlugin _instance;

    // ── Harmony: block game's NewSplit after we take over ─
    // ── Harmony: reset our state when game starts a new run ─
    [HarmonyPatch(typeof(SpeedrunSplits), "StartRun")]
    private class PatchStartRun
    {
        static void Prefix()
        {
            if (_instance == null) return;
            if (_instance._runActive && !_instance._practiceMode && !_instance._tasMode)
                _instance.SaveAttempt();
            _instance.StopTasPlayback();
            _instance._tasMode = _instance._tasArmedAfterReset;
            _instance._replayActive = false;
            _instance._replayFrames = null;
            _instance._runActive = false;
            _instance._managedSplits.Clear();
            _instance._currentSplitIndex = -1;
            _instance._ghostRecording = null;
            _instance.ClearTapState();
        }
    }

    [HarmonyPatch(typeof(SpeedrunSplits), "NewSplit")]
    private class PatchNewSplit
    {
        static bool Prefix()
        {
            return _instance == null || !_instance._runActive;
        }
    }


    // ── Harmony: skip moth update during replay ───────────
    [HarmonyPatch(typeof(MothController), "Update")]
    private class PatchMothUpdate
    {
        static bool Prefix()
        {
            if (_instance != null)
                _instance.PrepareTasFrame();
            return _instance == null || !_instance._replayActive;
        }

        static void Postfix()
        {
            if (_instance != null)
                _instance.TickTasPlayback();
        }
    }

    // ── Harmony: skip powerup pickup during replay ────────
    [HarmonyPatch(typeof(ExtraJump), "OnTriggerEnter2D")]
    private class PatchExtraJumpTrigger
    {
        static bool Prefix()
        {
            return _instance == null || !_instance._replayActive;
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

    [HarmonyPatch(typeof(Player), "GetAxis", new[] { typeof(string) })]
    private class PatchRewiredGetAxis
    {
        static bool Prefix(Player __instance, string actionName, ref float __result)
        {
            return _instance == null || !_instance.TryOverrideTasAxis(__instance, actionName, ref __result);
        }
    }

    [HarmonyPatch(typeof(Player), "GetAxisRaw", new[] { typeof(string) })]
    private class PatchRewiredGetAxisRaw
    {
        static bool Prefix(Player __instance, string actionName, ref float __result)
        {
            return _instance == null || !_instance.TryOverrideTasAxis(__instance, actionName, ref __result);
        }
    }

    [HarmonyPatch(typeof(Player), "GetButton", new[] { typeof(string) })]
    private class PatchRewiredGetButton
    {
        static bool Prefix(Player __instance, string actionName, ref bool __result)
        {
            return _instance == null || !_instance.TryOverrideTasButton(__instance, actionName, ref __result, 0);
        }
    }

    [HarmonyPatch(typeof(Player), "GetButtonDown", new[] { typeof(string) })]
    private class PatchRewiredGetButtonDown
    {
        static bool Prefix(Player __instance, string actionName, ref bool __result)
        {
            return _instance == null || !_instance.TryOverrideTasButton(__instance, actionName, ref __result, 1);
        }
    }

    [HarmonyPatch(typeof(Player), "GetButtonUp", new[] { typeof(string) })]
    private class PatchRewiredGetButtonUp
    {
        static bool Prefix(Player __instance, string actionName, ref bool __result)
        {
            return _instance == null || !_instance.TryOverrideTasButton(__instance, actionName, ref __result, 2);
        }
    }

    [HarmonyPatch(typeof(Input), "get_anyKeyDown")]
    private class PatchUnityInputAnyKeyDown
    {
        static bool Prefix(ref bool __result)
        {
            return _instance == null || !_instance.TryOverrideUnityAnyKeyDown(ref __result);
        }
    }

    private static readonly KeyCode[] CpKeys =
        { KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5 };


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

    private class TasPrefixCache
    {
        public int prefixFrames;
        public int prefixCommandIndex;
        public ulong prefixFingerprint;
        public SavedState state;
        public int playbackFrameCount;
        public int commandIndex;
        public int commandFrame;
        public TasJumpStage jumpStage;
        public int jumpHoldFramesLeft;
        public int jumpWaitFramesLeft;
        public bool prevJumpHeld;
        public bool currentJumpHeld;
        public float currentLookHorizontal;
        public float currentLookVertical;
        public bool stopAfterFrame;
    }

    private struct GhostFrame
    {
        public float x, y;
        public int animHash;
        public float animTime;
        public bool flipX;
        public byte keys; // bitmask: 1=left, 2=right, 4=up, 8=down
    }

    private enum GhostDisplayMode
    {
        HumanBest,
        TasBest,
        AttemptSwarm,
        Off,
    }


    private enum TasCommandType
    {
        Wait,
        Slide,
        Jump,
        ReverseJump,
    }

    private enum TasJumpStage
    {
        WaitingForJumps,
        HoldingJump,
        ReleaseFrame,
        WaitAfterRelease,
        ReverseFrame,
    }

    private struct TasCommand
    {
        public TasCommandType type;
        public int holdFrames;
        public int waitFrames;
    }

    private const byte KeyLeft = 1;
    private const byte KeyRight = 2;
    private const byte KeyUp = 4;
    private const byte KeyDown = 8;
    private const string TasScriptFileName = "ttf.tas";
    private const float IdleVelocitySqrThreshold = 1e-8f;
    private const int TasTargetFps = 60;
    private const int TasStartupPreRollFrames = 0;
    private const float FpsSampleInterval = 0.25f;
    private const ulong FnvOffset64 = 1469598103934665603UL;
    private const ulong FnvPrime64 = 1099511628211UL;

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
    private GhostFrame[] _tasGhostPlaybackFrames; // TAS ghost (full run)
    private GhostFrame[][] _tasSplitGhostFrames;  // TAS per-split ghosts (fallback to gold)
    private GhostFrame[] _ghostActivePlayback;   // current playback source
    private int _ghostPlaybackIndex;
    private GhostDisplayMode _ghostDisplayMode = GhostDisplayMode.HumanBest;
    private GhostFrame[] _tapGhostFrames;          // TAP best ghost
    private float _tapBestTime;

    // Attempt swarm ghost
    private List<GhostFrame[]> _attemptFrames;       // all loaded attempts
    private List<GameObject> _attemptGhosts;          // ghost sprites per attempt
    private bool _attemptSwarmActive;

    private bool _replayActive;
    private GhostFrame[] _replayFrames;
    private int _replayIndex;
    private readonly List<TasCommand> _tasCommands = new List<TasCommand>(256);
    private string _tasScriptPath;
    private int _tasScriptFrameCount;
    private bool _tasArmedAfterReset;
    private bool _tasPlaybackActive;
    private int _tasPlaybackFrameCount;
    private int _tasCommandIndex;
    private int _tasCommandFrame;
    private int _tasIdleSectionFrames;
    private bool _tasWasIdleLastFrame;
    private TasJumpStage _tasJumpStage;
    private int _tasJumpHoldFramesLeft;
    private int _tasJumpWaitFramesLeft;
    private bool _tasPrevJumpHeld;
    private bool _tasCurrentJumpHeld;
    private float _tasCurrentLookHorizontal;
    private float _tasCurrentLookVertical;
    private bool _tasStopAfterFrame;
    private int _lastIdleFramesDetected;
    private float _fpsSampleStartRealtime;
    private int _fpsSampleFrames;
    private float _measuredFps;
    private float _tasPrevTimeScale = 1f;
    private float _tasPrevFixedDeltaTime = 1f / 50f;
    private int _tasPrevTargetFrameRate = -1;
    private int _tasPrevVSyncCount;
    private int _tasPrevCaptureFrameRate;
    private bool _tasRuntimeSettingsApplied;
    private int _tasStartupPreRollRemaining;
    private int _tasPrefixFrames;
    private int _tasPrefixCommandIndex = -1;
    private ulong _tasPrefixFingerprint;
    private TasPrefixCache _tasPrefixCache;
    private bool _tasPrefixCaptureDoneThisRun;
    private int _tasManualPrefixFrames;
    private int _tasManualPrefixCommandIndex = -1;
    private bool _tasManualPrefixPending;
    private int _tasManualPrefixTargetCommandIndex = -1;
    private bool _tasPowerupDismissActive;
    private bool _tasPowerupDismissPulseOn;
    private bool _tasSimulatedAnyKeyDown;
    private SavedState _savedState;        // H/I quicksave
    private SavedState _pendingRestore;    // state to apply after scene reload
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
    private FieldInfo _extraJumpCanvasField;

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
    private int _attemptNumber;

    // Area triggers — colliders for position-based split detection
    private Dictionary<string, Collider2D> _areaColliders = new Dictionary<string, Collider2D>();

    // Practice mode — entered on any load, exited on game reset (R)
    private bool _practiceMode;
    private int _practiceSkipIndex;
    private bool _tasMode;

    // Input display — per-jump charge tracking
    private float _jumpCharge1;
    private float _jumpCharge2;
    private float _prevChargingTime;
    private int _prevJumpsValue = -1;
    private bool _jumpFiredStaleCharge;
    private bool _chargeDisplayCleared;
    private float _cachedChargingTime;
    private int _cachedJumps;
    private int _cachedMaxJumps;

    // Input display mode: false = arrows only, true = arrows + charge + vel/norm
    private bool _inputDetailMode;

    // Touch All Platforms mode
    private bool _tapMode;
    private bool _tapArmedAfterReset;
    private HashSet<Collider2D> _tapPlatforms;
    private HashSet<Collider2D> _tapTouchedPlatforms;
    private int _tapTotalCount;
    private float _tapTimer;
    private bool _tapTimerRunning;
    private bool _tapComplete;

    // Camera zoom (offset applied via Harmony patch on AdvancedCamera.Update)
    private int _zoomSteps;

    // Personal best / gold tracking
    private float[] _pbTotalTimes;
    private float[] _bestSegments;
    private float[] _tasBestSegments;
    private float[] _bestSegmentsSnapshot; // frozen at run start for display deltas
    private float[] _pbSnapshot;           // frozen at run start for display deltas
    private float[] _runTotals;

    // Display data (read by OnGUI)
    private float[] _displaySegTimes;
    private float[] _displayTotalTimes;
    private bool[] _splitLocked;
    private bool[] _splitIsGold;

    // Cached per-run: hasGolds and bestPossibleTime (only change on split advance)
    private bool _hasGolds;
    private float _bestPossibleTime;

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
    private GUIContent _reusableContent;

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
        TickMeasuredFps();

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

            if (!_pendingLoad)
            {
                if (_runActive && !_practiceMode && !_tasMode)
                    SaveAttempt();
                _zoomSteps = 0;
                _practiceMode = false;
                _tasMode = false;
                _replayActive = false;
                _replayFrames = null;
                _runActive = false;
                _managedSplits.Clear();
                _currentSplitIndex = -1;
                _ghostRecording = null;
                ClearTapState();
                DestroyAttemptGhosts();
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
            _extraJumpCanvasField = typeof(ExtraJump).GetField("doubleJumpCanvas", flags);
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
            var areaNames = new[] { AreaChurch, AreaTower };
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
                        }
                        else
                            Logger.LogWarning("TigerMoth: '" + areaName + "' has no Collider2D");
                    }
                }
            }

            // Create ghost moth — build from scratch, sprites only
            if (_ghost == null)
            {
                _ghost = new GameObject("GhostMoth");
                CopySprites(_moth.transform, _ghost.transform);
                _ghost.transform.position = _moth.transform.position;

                // Add Animator for animation playback
                var mothAnimator = GetMothAnimator();
                if (mothAnimator != null)
                {
                    _ghostAnimator = _ghost.AddComponent<Animator>();
                    _ghostAnimator.runtimeAnimatorController = mothAnimator.runtimeAnimatorController;
                    _ghostAnimator.avatar = mothAnimator.avatar;
                }

                // Hide until a run starts with playback data
                _ghost.SetActive(false);
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
            if (_pendingRestore != null)
                ApplyState();
            if (_tapArmedAfterReset)
                StartTapMode();
            return;
        }

        ManageSplits();

        // Track jump charge for input display
        if (_chargingTimeField != null && _jumpsField != null && _maxJumpsField != null)
        {
            float ct = (float)_chargingTimeField.GetValue(_moth);
            int curJumps = (int)_jumpsField.GetValue(_moth);
            int maxJumps = (int)_maxJumpsField.GetValue(_moth);

            // Clear stale flag from previous frame (check before set to avoid same-frame flicker)
            if (_jumpFiredStaleCharge && ct != _prevChargingTime)
                _jumpFiredStaleCharge = false;

            if (_prevJumpsValue >= 0 && curJumps < _prevJumpsValue)
            {
                // A jump was fired — which slot?
                int jumpNumber = maxJumps - curJumps;
                if (jumpNumber == 1)
                    _jumpCharge1 = _prevChargingTime;
                else
                    _jumpCharge2 = _prevChargingTime;
                _jumpFiredStaleCharge = true;
                _chargeDisplayCleared = false;
            }

            // Clear both slots when actively charging a new first jump
            if (!_jumpFiredStaleCharge && ct > 0f && curJumps == maxJumps && !_chargeDisplayCleared)
            {
                _jumpCharge1 = 0f;
                _jumpCharge2 = 0f;
                _chargeDisplayCleared = true;
            }

            _prevJumpsValue = curJumps;
            _prevChargingTime = ct;
            _cachedChargingTime = ct;
            _cachedJumps = curJumps;
            _cachedMaxJumps = maxJumps;
        }

        if (Input.GetKeyDown(KeyCode.T))
        {
            TriggerTasResetAndPlayback();
            return;
        }

        // Checkpoints (must be checked before replay block which returns early)
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            _pendingRestore = null;
            _practiceMode = true;
            _tasMode = false;
            _practiceSkipIndex = -1;
            ReloadAndRestore();
            return;
        }

        for (int i = 0; i < CpKeys.Length; i++)
        {
            if (Input.GetKeyDown(CpKeys[i]))
            {
                _pendingRestore = _checkpoints[i];
                _practiceMode = true;
                _tasMode = false;
                _practiceSkipIndex = _pendingRestore.currentSplitIndex;
                ReloadAndRestore();
                return;
            }
        }

        if (Input.GetKeyDown(KeyCode.P))
        {
            if (_tasMode || _tasPlaybackActive)
            {
                bool clearPrefix = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (clearPrefix)
                    ClearManualTasPrefix();
                else
                    SetTasPrefixFromCurrentFrame();
            }
            else
            {
                ToggleTapMode();
            }
        }

        // Replay mode: drive moth from recorded frames
        if (_replayActive)
        {
            // Any movement input ends replay
            var controls = InputManager.controls;
            float h = controls != null ? controls.GetAxis("LookHorizontal") : 0f;
            float v = controls != null ? controls.GetAxis("LookVertical") : 0f;
            if (h != 0f || v != 0f)
            {
                StopReplay();
            }
            else if (_replayIndex < _replayFrames.Length)
            {
                var f = _replayFrames[_replayIndex];
                _moth.transform.position = new Vector3(f.x, f.y, 0f);
                _moth.transform.eulerAngles = new Vector3(0f, f.flipX ? 180f : 0f, 0f);
                _rb.position = new Vector2(f.x, f.y);
                _rb.velocity = Vector2.zero;
                var animator = GetMothAnimator();
                if (animator != null && f.animHash != 0)
                    animator.Play(f.animHash, 0, f.animTime);
                _replayIndex++;
            }
            else if (IsSegmentMode())
            {
                // Practice/TAS mode: advance to next split's segment ghost
                _currentSplitIndex++;
                if (_currentSplitIndex < _managedSplits.Count)
                {
                    var next = _managedSplits[_currentSplitIndex];
                    _splitTimeValueField.SetValue(next, 0f);
                    _splitTickingField.SetValue(next, true);
                }
                ChainReplay();
            }
            else
            {
                // Normal mode: PB exhausted, done
                StopReplay();
            }
            _ghostPlaybackIndex++;
            TickAttemptSwarm();
            return;
        }

        // Touch All Platforms: tick
        TickTapMode();

        // Ghost: record frame
        if (_runActive && _ghostRecording != null)
        {
            var mothAnimator = GetMothAnimator();
            var info = mothAnimator != null
                ? mothAnimator.GetCurrentAnimatorStateInfo(0)
                : default(AnimatorStateInfo);
            var controls = InputManager.controls;
            float h = controls != null ? controls.GetAxis("LookHorizontal") : 0f;
            float v = controls != null ? controls.GetAxis("LookVertical") : 0f;
            byte keys = 0;
            if (h < 0f) keys |= KeyLeft;
            if (h > 0f) keys |= KeyRight;
            if (v > 0f) keys |= KeyUp;
            if (v < 0f) keys |= KeyDown;

            _ghostRecording.Add(new GhostFrame
            {
                x = _moth.transform.position.x,
                y = _moth.transform.position.y,
                animHash = info.fullPathHash,
                animTime = info.normalizedTime,
                flipX = _moth.transform.eulerAngles.y > 90f,
                keys = keys
            });
        }

        // Ghost: playback frame (always advance index to stay in sync)
        if (_ghostActivePlayback != null && _ghostPlaybackIndex < _ghostActivePlayback.Length)
        {
            if (_ghost != null && _ghostDisplayMode != GhostDisplayMode.Off)
            {
                var f = _ghostActivePlayback[_ghostPlaybackIndex];
                _ghost.transform.position = new Vector3(f.x, f.y, 0f);
                _ghost.transform.eulerAngles = new Vector3(0f, f.flipX ? 180f : 0f, 0f);
                if (_ghostAnimator != null && f.animHash != 0)
                    _ghostAnimator.Play(f.animHash, 0, f.animTime);
            }
            _ghostPlaybackIndex++;
        }
        else if (_ghost != null && _ghost.activeSelf && _ghostActivePlayback != null)
        {
            _ghost.SetActive(false);
        }

        // Attempt swarm: playback
        TickAttemptSwarm();

        if (Input.GetKeyDown(KeyCode.H))
            SaveState();

        if (Input.GetKeyDown(KeyCode.I))
        {
            if (_savedState == null)
                Logger.LogWarning("TigerMoth: no saved state to load");
            else
            {
                _pendingRestore = _savedState;
                _practiceMode = true;
                _tasMode = false;
                _practiceSkipIndex = _savedState.currentSplitIndex;
                ReloadAndRestore();
            }
        }



        if (Input.GetKeyDown(KeyCode.V))
            _inputDetailMode = !_inputDetailMode;

        if (Input.GetKeyDown(KeyCode.F))
            StartReplay();

        if (Input.GetKeyDown(KeyCode.G))
            CycleGhostDisplayMode();

        if (Input.GetKeyDown(KeyCode.LeftBracket))
            _zoomSteps--;

        if (Input.GetKeyDown(KeyCode.RightBracket))
            _zoomSteps++;
    }

    private void TickMeasuredFps()
    {
        float now = Time.realtimeSinceStartup;
        if (_fpsSampleStartRealtime <= 0f)
        {
            _fpsSampleStartRealtime = now;
            _fpsSampleFrames = 1;
            return;
        }

        _fpsSampleFrames++;
        float elapsed = now - _fpsSampleStartRealtime;
        if (elapsed < FpsSampleInterval)
            return;

        _measuredFps = _fpsSampleFrames / elapsed;
        _fpsSampleStartRealtime = now;
        _fpsSampleFrames = 0;
    }

    void LateUpdate()
    {
        if (_zoomSteps == 0 || Camera.main == null) return;
        var cam = Singleton<AdvancedCamera>.Instance;
        if (cam == null) return;

        Camera.main.orthographicSize = cam.targetSize + _zoomSteps * 2f;
    }

    // ── Replay ──────────────────────────────────────────

    private static string GhostDisplayModeLabel(GhostDisplayMode mode)
    {
        switch (mode)
        {
            case GhostDisplayMode.HumanBest:
                return "Human";
            case GhostDisplayMode.TasBest:
                return "TAS";
            case GhostDisplayMode.AttemptSwarm:
                return "Swarm";
            default:
                return "Off";
        }
    }

    private GhostFrame[] GetFullRunGhostFramesForMode()
    {
        switch (_ghostDisplayMode)
        {
            case GhostDisplayMode.HumanBest:
                return _ghostPlaybackFrames;
            case GhostDisplayMode.TasBest:
                return _tasGhostPlaybackFrames;
            case GhostDisplayMode.AttemptSwarm:
                return _ghostPlaybackFrames; // follow PB in swarm mode
            default:
                return null;
        }
    }

    private GhostFrame[] GetSegmentGhostFramesForMode(int splitIndex)
    {
        if (splitIndex < 0 || splitIndex >= SplitNames.Length)
            return null;

        GhostFrame[][] source = null;
        if (_ghostDisplayMode == GhostDisplayMode.HumanBest)
            source = _goldGhostFrames;
        else if (_ghostDisplayMode == GhostDisplayMode.TasBest)
            source = _tasSplitGhostFrames;
        else
            return null;

        if (source == null || splitIndex >= source.Length)
            return null;

        return source[splitIndex];
    }

    private void ApplyGhostVisibility()
    {
        if (_ghost == null)
            return;

        bool canShow = !_replayActive
            && _ghostDisplayMode != GhostDisplayMode.Off
            && _ghostDisplayMode != GhostDisplayMode.AttemptSwarm
            && _ghostActivePlayback != null
            && _ghostPlaybackIndex < _ghostActivePlayback.Length;
        _ghost.SetActive(canShow);
    }

    private void CycleGhostDisplayMode()
    {
        _ghostDisplayMode = (GhostDisplayMode)(((int)_ghostDisplayMode + 1) % 4);

        // Manage attempt swarm
        if (_ghostDisplayMode == GhostDisplayMode.AttemptSwarm)
        {
            if (_attemptFrames == null)
                LoadAllAttempts();
            if (_attemptFrames.Count == 0)
            {
                // Skip swarm mode if no attempts
                _ghostDisplayMode = GhostDisplayMode.Off;
            }
            else
            {
                _attemptSwarmActive = true;
                CreateAttemptGhosts();
                ShowAttemptGhosts(true);
            }
        }
        else
        {
            if (_attemptSwarmActive)
            {
                _attemptSwarmActive = false;
                ShowAttemptGhosts(false);
            }
        }

        if (_runActive && _ghostDisplayMode != GhostDisplayMode.AttemptSwarm)
        {
            int previousIndex = _ghostPlaybackIndex;
            _ghostActivePlayback = IsSegmentMode()
                ? GetSegmentGhostFramesForMode(_currentSplitIndex)
                : GetFullRunGhostFramesForMode();
            if (_ghostActivePlayback == null)
                _ghostPlaybackIndex = 0;
            else if (previousIndex > _ghostActivePlayback.Length)
                _ghostPlaybackIndex = _ghostActivePlayback.Length;
            else
                _ghostPlaybackIndex = previousIndex;
        }

        ApplyGhostVisibility();
    }

    private void StartReplay()
    {
        if (!_runActive || _currentSplitIndex < 0 || _currentSplitIndex >= SplitNames.Length)
            return;

        GhostFrame[] frames = null;

        if (IsSegmentMode())
        {
            // Practice/TAS mode: use per-split ghost
            frames = GetSegmentGhostFramesForMode(_currentSplitIndex);
        }
        else
        {
            // Normal mode: use selected full-run ghost source
            frames = GetFullRunGhostFramesForMode();
        }

        if (frames == null || frames.Length == 0)
            return;

        _replayFrames = frames;
        _replayIndex = 0;
        _replayActive = true;
        _ghostPlaybackIndex = 0;

        // Zero physics; Harmony prefix skips MothController.Update during replay
        _rb.velocity = Vector2.zero;
        if (_ghost != null) _ghost.SetActive(false);
        if (_attemptSwarmActive)
            ShowAttemptGhosts(true);

        // Reset the current split timer to 0 and ensure it ticks during replay
        if (_currentSplitIndex >= 0 && _currentSplitIndex < _managedSplits.Count
            && _managedSplits[_currentSplitIndex] != null)
        {
            _splitTimeValueField.SetValue(_managedSplits[_currentSplitIndex], 0f);
            _splitTickingField.SetValue(_managedSplits[_currentSplitIndex], true);
        }

    }

    private void StopReplay()
    {
        if (!_replayActive) return;
        _replayActive = false;

        // Sync moth state to last replay frame so controls feel correct
        if (_replayIndex > 0 && _replayIndex <= _replayFrames.Length)
        {
            var f = _replayFrames[_replayIndex - 1];
            _rb.position = new Vector2(f.x, f.y);
            _rb.velocity = Vector2.zero;
            int facing = f.flipX ? -1 : 1;
            _facingDirectionField.SetValue(_moth, facing);
            _jumpCanceledField.SetValue(_moth, false);
            _chargingTimeField.SetValue(_moth, 0f);
            _hitstopField.SetValue(_moth, false);
            _queuedJumpField.SetValue(_moth, false);
        }

        _replayFrames = null;
    }

    private void TriggerTasResetAndPlayback()
    {
        string error;
        if (!LoadTasScript(out error))
        {
            _tasArmedAfterReset = false;
            _tasMode = false;
            StopTasPlayback();
            Logger.LogWarning("TigerMoth: TAS script load failed: " + error);
            return;
        }

        _tasArmedAfterReset = true;
        _lastIdleFramesDetected = 0;
        _tasManualPrefixPending = false;
        _tasManualPrefixTargetCommandIndex = -1;
        _tasMode = true;
        StopTasPlayback();
        StopReplay();
        _pendingRestore = null;
        _practiceMode = false;
        _practiceSkipIndex = -1;
        ReloadAndRestore(keepTasArmed: true);
        Logger.LogInfo("TigerMoth: TAS reset queued (" + _tasCommands.Count
            + " commands, " + _tasScriptFrameCount + " scripted frames)");
    }

    private void StartTasPlayback()
    {
        if (_tasCommands.Count == 0)
        {
            _tasArmedAfterReset = false;
            Logger.LogWarning("TigerMoth: TAS playback skipped (no commands loaded)");
            return;
        }

        _tasArmedAfterReset = false;
        _tasPlaybackActive = true;
        _tasPlaybackFrameCount = 0;
        _tasIdleSectionFrames = 0;
        _tasWasIdleLastFrame = false;
        _tasPowerupDismissActive = false;
        _tasPowerupDismissPulseOn = false;
        _tasSimulatedAnyKeyDown = false;
        _tasPrefixCaptureDoneThisRun = false;
        _tasStartupPreRollRemaining = TasStartupPreRollFrames;
        ResetTasCommandState();
        ApplyTasRuntimeSettings();
        bool resumedFromPrefixCache = TryApplyTasPrefixCache();
        Logger.LogInfo("TigerMoth: TAS playback started (" + _tasCommands.Count
            + " commands, " + _tasScriptFrameCount + " scripted frames, "
            + "fps=" + TasTargetFps + ", preroll=" + TasStartupPreRollFrames
            + ", prefix=" + (_tasPrefixCommandIndex >= 0
                ? _tasPrefixFrames + "f/cmd" + (_tasPrefixCommandIndex + 1)
                : "off")
            + (resumedFromPrefixCache ? ", resumed=cache" : "") + ")");
    }

    private void StopTasPlayback()
    {
        if (!_tasPlaybackActive)
            return;

        _tasPlaybackActive = false;
        _tasPlaybackFrameCount = 0;
        _tasIdleSectionFrames = 0;
        _tasWasIdleLastFrame = false;
        _tasPowerupDismissActive = false;
        _tasPowerupDismissPulseOn = false;
        _tasSimulatedAnyKeyDown = false;
        _tasPrefixCaptureDoneThisRun = false;
        _tasStartupPreRollRemaining = 0;
        _tasManualPrefixPending = false;
        _tasManualPrefixTargetCommandIndex = -1;
        ResetTasCommandState();
        RestoreTasRuntimeSettings();
        Logger.LogInfo("TigerMoth: TAS playback finished");
    }

    private void PrepareTasFrame()
    {
        if (!_tasPlaybackActive)
            return;

        _tasCurrentJumpHeld = false;
        _tasCurrentLookHorizontal = 0f;
        _tasCurrentLookVertical = 0f;
        _tasSimulatedAnyKeyDown = false;

        if (_tasStartupPreRollRemaining > 0)
        {
            _tasStartupPreRollRemaining--;
            return;
        }

        if (_tasCommandIndex >= _tasCommands.Count)
        {
            _tasStopAfterFrame = true;
            return;
        }

        MaybeCaptureManualTasPrefixAtCommandBoundary();
        MaybeCaptureTasPrefixCache();
        if (HandleTasPowerupDismissInput())
            return;
        PrepareCurrentTasCommandInput();
    }

    private void TickTasPlayback()
    {
        if (!_tasPlaybackActive)
            return;

        bool isIdle = _rb != null && _rb.velocity.sqrMagnitude <= IdleVelocitySqrThreshold;
        if (isIdle)
        {
            _tasIdleSectionFrames = _tasWasIdleLastFrame ? _tasIdleSectionFrames + 1 : 1;
            _lastIdleFramesDetected = _tasIdleSectionFrames;
            _tasWasIdleLastFrame = true;
        }
        else
        {
            _tasIdleSectionFrames = 0;
            _tasWasIdleLastFrame = false;
        }

        _tasPlaybackFrameCount++;
        _tasPrevJumpHeld = _tasCurrentJumpHeld;
        if (_tasStopAfterFrame)
            StopTasPlayback();
    }

    private void ApplyTasRuntimeSettings()
    {
        if (_tasRuntimeSettingsApplied)
            return;

        _tasPrevTimeScale = Time.timeScale;
        _tasPrevFixedDeltaTime = Time.fixedDeltaTime;
        _tasPrevTargetFrameRate = Application.targetFrameRate;
        _tasPrevVSyncCount = QualitySettings.vSyncCount;
        _tasPrevCaptureFrameRate = Time.captureFramerate;
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 1f / TasTargetFps;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = TasTargetFps;
        Time.captureFramerate = TasTargetFps;
        _tasRuntimeSettingsApplied = true;
    }

    private void RestoreTasRuntimeSettings()
    {
        if (!_tasRuntimeSettingsApplied)
            return;

        Time.timeScale = _tasPrevTimeScale;
        Time.fixedDeltaTime = _tasPrevFixedDeltaTime;
        Application.targetFrameRate = _tasPrevTargetFrameRate;
        QualitySettings.vSyncCount = _tasPrevVSyncCount;
        Time.captureFramerate = _tasPrevCaptureFrameRate;
        _tasRuntimeSettingsApplied = false;
    }

    private bool TryApplyTasPrefixCache()
    {
        if (_tasPrefixCommandIndex < 0 || _tasPrefixCache == null)
            return false;

        if (_tasPrefixCache.prefixCommandIndex != _tasPrefixCommandIndex
            || _tasPrefixCache.prefixFingerprint != _tasPrefixFingerprint
            || _tasPrefixCache.state == null)
        {
            _tasPrefixCache = null;
            return false;
        }

        _pendingRestore = _tasPrefixCache.state;
        ApplyState();

        _tasPlaybackFrameCount = _tasPrefixCache.playbackFrameCount;
        _tasCommandIndex = _tasPrefixCache.commandIndex;
        _tasCommandFrame = _tasPrefixCache.commandFrame;
        _tasJumpStage = _tasPrefixCache.jumpStage;
        _tasJumpHoldFramesLeft = _tasPrefixCache.jumpHoldFramesLeft;
        _tasJumpWaitFramesLeft = _tasPrefixCache.jumpWaitFramesLeft;
        _tasPrevJumpHeld = _tasPrefixCache.prevJumpHeld;
        _tasCurrentJumpHeld = _tasPrefixCache.currentJumpHeld;
        _tasCurrentLookHorizontal = _tasPrefixCache.currentLookHorizontal;
        _tasCurrentLookVertical = _tasPrefixCache.currentLookVertical;
        _tasStopAfterFrame = _tasPrefixCache.stopAfterFrame;
        _tasStartupPreRollRemaining = 0;
        _tasPrefixCaptureDoneThisRun = true;
        return true;
    }

    private void SetTasPrefixFromCurrentFrame()
    {
        if (!_tasPlaybackActive)
        {
            Logger.LogInfo("TigerMoth: prefix hotkey ignored (TAS playback not active)");
            return;
        }

        if (_tasPlaybackFrameCount <= 0)
        {
            Logger.LogWarning("TigerMoth: prefix hotkey ignored at frame "
                + _tasPlaybackFrameCount + " (must be >= 1)");
            return;
        }

        int targetCommandIndex = GetNextTasCommandIndexForManualPrefix();
        if (targetCommandIndex < 0)
        {
            Logger.LogWarning("TigerMoth: prefix hotkey ignored (no next TAS command)");
            return;
        }

        _tasManualPrefixPending = true;
        _tasManualPrefixTargetCommandIndex = targetCommandIndex;
        Logger.LogInfo("TigerMoth: manual TAS prefix armed for next command (#"
            + (targetCommandIndex + 1) + ")");
    }

    private int GetNextTasCommandIndexForManualPrefix()
    {
        for (int next = _tasCommandIndex + 1; next < _tasCommands.Count; next++)
        {
            if (GetTasCommandDurationFrames(_tasCommands[next]) > 0)
                return next;
        }

        return -1;
    }

    private void MaybeCaptureManualTasPrefixAtCommandBoundary()
    {
        if (!_tasManualPrefixPending)
            return;

        if (_tasManualPrefixTargetCommandIndex < 0 || _tasManualPrefixTargetCommandIndex >= _tasCommands.Count)
        {
            _tasManualPrefixPending = false;
            _tasManualPrefixTargetCommandIndex = -1;
            return;
        }

        if (_tasCommandIndex < _tasManualPrefixTargetCommandIndex)
            return;
        if (_tasCommandIndex > _tasManualPrefixTargetCommandIndex)
        {
            Logger.LogWarning("TigerMoth: manual prefix target command was passed before capture; clearing pending prefix");
            _tasManualPrefixPending = false;
            _tasManualPrefixTargetCommandIndex = -1;
            return;
        }

        if (_tasCommandFrame != 0
            || _tasJumpStage != TasJumpStage.WaitingForJumps
            || _tasJumpHoldFramesLeft != 0
            || _tasJumpWaitFramesLeft != 0)
        {
            return;
        }

        _tasManualPrefixPending = false;
        _tasManualPrefixTargetCommandIndex = -1;
        _tasManualPrefixFrames = _tasPlaybackFrameCount;
        _tasManualPrefixCommandIndex = _tasCommandIndex;
        _tasPrefixFrames = _tasManualPrefixFrames;
        _tasPrefixCommandIndex = _tasManualPrefixCommandIndex;
        _tasPrefixFingerprint = ComputeTasPrefixFingerprint(_tasPrefixCommandIndex);
        if (!CaptureTasPrefixCacheFromCurrentState())
            return;

        _tasPrefixCaptureDoneThisRun = true;
        Logger.LogInfo("TigerMoth: manual TAS prefix set to " + _tasManualPrefixFrames
            + "f at command #" + (_tasManualPrefixCommandIndex + 1)
            + " (press T to restart from cached prefix)");
    }

    private void ClearManualTasPrefix()
    {
        _tasManualPrefixFrames = 0;
        _tasManualPrefixCommandIndex = -1;
        _tasManualPrefixPending = false;
        _tasManualPrefixTargetCommandIndex = -1;
        _tasPrefixFrames = 0;
        _tasPrefixCommandIndex = -1;
        _tasPrefixFingerprint = 0;
        _tasPrefixCache = null;
        _tasPrefixCaptureDoneThisRun = false;
        Logger.LogInfo("TigerMoth: manual TAS prefix cleared");
    }

    private bool CaptureTasPrefixCacheFromCurrentState()
    {
        SavedState state = CaptureStateSnapshot();
        if (state == null)
            return false;

        _tasPrefixCache = new TasPrefixCache
        {
            prefixFrames = _tasPrefixFrames,
            prefixCommandIndex = _tasPrefixCommandIndex,
            prefixFingerprint = _tasPrefixFingerprint,
            state = state,
            playbackFrameCount = _tasPlaybackFrameCount,
            commandIndex = _tasCommandIndex,
            commandFrame = _tasCommandFrame,
            jumpStage = _tasJumpStage,
            jumpHoldFramesLeft = _tasJumpHoldFramesLeft,
            jumpWaitFramesLeft = _tasJumpWaitFramesLeft,
            prevJumpHeld = _tasPrevJumpHeld,
            currentJumpHeld = _tasCurrentJumpHeld,
            currentLookHorizontal = _tasCurrentLookHorizontal,
            currentLookVertical = _tasCurrentLookVertical,
            stopAfterFrame = _tasStopAfterFrame,
        };

        return true;
    }

    private void MaybeCaptureTasPrefixCache()
    {
        if (_tasPrefixCaptureDoneThisRun || _tasPrefixCommandIndex < 0)
            return;

        if (_tasCommandIndex < _tasPrefixCommandIndex)
            return;
        if (_tasCommandIndex > _tasPrefixCommandIndex)
        {
            _tasPrefixCaptureDoneThisRun = true;
            Logger.LogWarning("TigerMoth: skipped automatic prefix cache capture (passed target command)");
            return;
        }

        if (_tasCommandFrame != 0
            || _tasJumpStage != TasJumpStage.WaitingForJumps
            || _tasJumpHoldFramesLeft != 0
            || _tasJumpWaitFramesLeft != 0)
            return;

        _tasPrefixFrames = _tasPlaybackFrameCount;
        _tasManualPrefixFrames = _tasPrefixFrames;
        _tasManualPrefixCommandIndex = _tasPrefixCommandIndex;

        if (!CaptureTasPrefixCacheFromCurrentState())
            return;

        _tasPrefixCaptureDoneThisRun = true;
        Logger.LogInfo("TigerMoth: TAS prefix cache captured at frame "
            + _tasPlaybackFrameCount + " (prefix=" + _tasPrefixFrames
            + "f/cmd" + (_tasPrefixCommandIndex + 1) + ")");
    }

    // mode: 0 = hold, 1 = down, 2 = up
    private bool TryOverrideTasButton(Player player, string actionName, ref bool value, int mode)
    {
        if (!_tasPlaybackActive || !ReferenceEquals(player, InputManager.controls))
            return false;

        if (actionName == "Jump")
        {
            if (mode == 0)
                value = _tasCurrentJumpHeld;
            else if (mode == 1)
                value = !_tasPrevJumpHeld && _tasCurrentJumpHeld;
            else
                value = _tasPrevJumpHeld && !_tasCurrentJumpHeld;
            return true;
        }

        if (actionName == "CancelJump")
        {
            value = false;
            return true;
        }

        return false;
    }

    private bool TryOverrideTasAxis(Player player, string actionName, ref float value)
    {
        if (!_tasPlaybackActive || !ReferenceEquals(player, InputManager.controls))
            return false;

        if (actionName == "LookVertical")
        {
            value = _tasCurrentLookVertical;
            return true;
        }

        if (actionName == "LookHorizontal")
        {
            value = _tasCurrentLookHorizontal;
            return true;
        }

        return false;
    }

    private bool TryOverrideUnityAnyKeyDown(ref bool value)
    {
        if (!_tasPlaybackActive || !_tasPowerupDismissActive)
            return false;

        value = _tasSimulatedAnyKeyDown;
        return true;
    }

    private bool HandleTasPowerupDismissInput()
    {
        if (!_tasPlaybackActive)
            return false;

        bool active = IsExtraJumpPowerupScreenActive();
        if (!active)
        {
            if (_tasPowerupDismissActive)
            {
                _tasPowerupDismissActive = false;
                _tasPowerupDismissPulseOn = false;
                _tasSimulatedAnyKeyDown = false;
                Logger.LogInfo("TigerMoth: TAS powerup auto-dismiss complete");
            }

            return false;
        }

        if (!_tasPowerupDismissActive)
        {
            _tasPowerupDismissActive = true;
            _tasPowerupDismissPulseOn = false;
            Logger.LogInfo("TigerMoth: TAS powerup screen detected; auto-dismissing");
        }

        // 1 frame left press, 1 frame release.
        _tasPowerupDismissPulseOn = !_tasPowerupDismissPulseOn;
        if (_tasPowerupDismissPulseOn)
        {
            _tasCurrentLookHorizontal = -1f;
            _tasSimulatedAnyKeyDown = true;
        }
        else
        {
            _tasCurrentLookHorizontal = 0f;
            _tasSimulatedAnyKeyDown = false;
        }

        _tasCurrentLookVertical = 0f;
        _tasCurrentJumpHeld = false;
        return true;
    }

    private bool IsExtraJumpPowerupScreenActive()
    {
        if (_extraJump == null || _extraJumpCanvasField == null)
            return false;

        var gameManager = Singleton<GameManager>.Instance;
        if (gameManager == null || !gameManager.Paused)
            return false;

        var canvas = _extraJumpCanvasField.GetValue(_extraJump) as GameObject;
        return canvas != null && canvas.activeInHierarchy;
    }

    private bool LoadTasScript(out string error)
    {
        _tasCommands.Clear();
        _tasScriptFrameCount = 0;
        _tasPrefixFrames = 0;
        _tasPrefixCommandIndex = -1;
        _tasPrefixFingerprint = 0;
        _tasScriptPath = ResolveTasScriptPath();

        if (!File.Exists(_tasScriptPath))
        {
            error = "missing " + TasScriptFileName + " (looked in game root and config)";
            return false;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(_tasScriptPath);
        }
        catch (System.Exception e)
        {
            error = "failed to read '" + _tasScriptPath + "': " + e.Message;
            return false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0
                || line.StartsWith("--")
                || line.StartsWith("//")
                || line.StartsWith("#"))
                continue;

            string[] parts = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            string cmd = parts[0].ToLowerInvariant();
            int frames;

            if (cmd == "j" || cmd == "jump")
            {
                if (!TryParseTasFrames(parts, i + 1, cmd, minFrames: 1, out frames, out error))
                    return false;
                AppendTasJump(frames);
                continue;
            }

            if (cmd == "rj")
            {
                int holdFrames;
                int reverseDelayFrames;
                if (!TryParseTasReverseJump(parts, i + 1, out holdFrames, out reverseDelayFrames, out error))
                    return false;
                AppendTasReverseJump(holdFrames, reverseDelayFrames);
                continue;
            }

            if (cmd == "w" || cmd == "wait")
            {
                if (!TryParseTasFrames(parts, i + 1, cmd, minFrames: 0, out frames, out error))
                    return false;
                AppendTasWait(frames);
                continue;
            }

            if (cmd == "s" || cmd == "slide")
            {
                if (!TryParseTasFrames(parts, i + 1, cmd, minFrames: 0, out frames, out error))
                    return false;
                AppendTasSlide(frames);
                continue;
            }

            error = "line " + (i + 1) + ": unknown command '" + parts[0] + "'";
            return false;
        }

        if (_tasCommands.Count == 0)
        {
            error = "no TAS commands found in '" + _tasScriptPath + "'";
            return false;
        }

        _tasPrefixFrames = _tasManualPrefixFrames;
        _tasPrefixCommandIndex = _tasManualPrefixCommandIndex;

        if (_tasPrefixCommandIndex >= _tasCommands.Count)
        {
            Logger.LogWarning("TigerMoth: clearing manual prefix (target command no longer exists in updated TAS)");
            _tasManualPrefixFrames = 0;
            _tasManualPrefixCommandIndex = -1;
            _tasPrefixFrames = 0;
            _tasPrefixCommandIndex = -1;
            _tasPrefixCache = null;
        }

        if (_tasPrefixCommandIndex >= 0)
            _tasPrefixFingerprint = ComputeTasPrefixFingerprint(_tasPrefixCommandIndex);

        if (_tasPrefixCache != null
            && (_tasPrefixCache.prefixCommandIndex != _tasPrefixCommandIndex
                || _tasPrefixCache.prefixFingerprint != _tasPrefixFingerprint))
            _tasPrefixCache = null;

        error = null;
        Logger.LogInfo("TigerMoth: TAS script loaded from " + _tasScriptPath
            + " (" + _tasScriptFrameCount + "f, prefix="
            + (_tasPrefixCommandIndex >= 0
                ? _tasPrefixFrames + "f/cmd" + (_tasPrefixCommandIndex + 1)
                : "off") + ")");
        return true;
    }

    private static bool TryParseTasFrames(string[] parts, int lineNumber, string command,
        int minFrames, out int frames, out string error)
    {
        frames = 0;
        if (parts.Length != 2)
        {
            error = "line " + lineNumber + ": expected '" + command + " <frames>'";
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out frames))
        {
            error = "line " + lineNumber + ": invalid frame count '" + parts[1] + "'";
            return false;
        }

        if (frames < minFrames)
        {
            error = "line " + lineNumber + ": frame count must be >= " + minFrames;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseTasReverseJump(string[] parts, int lineNumber,
        out int holdFrames, out int reverseDelayFrames, out string error)
    {
        holdFrames = 0;
        reverseDelayFrames = 0;
        if (parts.Length != 2 && parts.Length != 3)
        {
            error = "line " + lineNumber + ": expected 'rj <jumpFrames> [waitFrames]'";
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out holdFrames)
            || holdFrames < 1)
        {
            error = "line " + lineNumber + ": jumpFrames must be >= 1";
            return false;
        }

        if (parts.Length == 3
            && (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out reverseDelayFrames) || reverseDelayFrames < 0))
        {
            error = "line " + lineNumber + ": waitFrames must be >= 0";
            return false;
        }

        error = null;
        return true;
    }

    private void AppendTasJump(int holdFrames)
    {
        _tasCommands.Add(new TasCommand
        {
            type = TasCommandType.Jump,
            holdFrames = holdFrames,
            waitFrames = 0,
        });
        _tasScriptFrameCount += holdFrames + 1; // includes release frame
    }

    private void AppendTasReverseJump(int holdFrames, int reverseDelayFrames)
    {
        _tasCommands.Add(new TasCommand
        {
            type = TasCommandType.ReverseJump,
            holdFrames = holdFrames,
            waitFrames = reverseDelayFrames,
        });
        _tasScriptFrameCount += holdFrames + 1 + reverseDelayFrames + 1;
    }

    private void AppendTasWait(int frames)
    {
        _tasCommands.Add(new TasCommand
        {
            type = TasCommandType.Wait,
            holdFrames = frames,
            waitFrames = 0,
        });
        _tasScriptFrameCount += frames;
    }

    private void AppendTasSlide(int frames)
    {
        _tasCommands.Add(new TasCommand
        {
            type = TasCommandType.Slide,
            holdFrames = frames,
            waitFrames = 0,
        });
        _tasScriptFrameCount += frames;
    }

    private static int GetTasCommandDurationFrames(TasCommand command)
    {
        switch (command.type)
        {
            case TasCommandType.Jump:
                return command.holdFrames + 1;
            case TasCommandType.ReverseJump:
                return command.holdFrames + 1 + command.waitFrames + 1;
            case TasCommandType.Wait:
            case TasCommandType.Slide:
            default:
                return command.holdFrames;
        }
    }

    private static ulong HashInt64(ulong hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= FnvPrime64;
            return hash;
        }
    }

    private ulong ComputeTasPrefixFingerprint(int prefixCommandIndex)
    {
        if (prefixCommandIndex < 0)
            return 0;

        ulong hash = FnvOffset64;
        int commandCount = Mathf.Min(prefixCommandIndex, _tasCommands.Count);
        for (int i = 0; i < commandCount; i++)
        {
            var command = _tasCommands[i];
            hash = HashInt64(hash, (int)command.type);
            hash = HashInt64(hash, command.holdFrames);
            hash = HashInt64(hash, command.waitFrames);
        }

        hash = HashInt64(hash, prefixCommandIndex);
        return hash;
    }

    private static string ResolveTasScriptPath()
    {
        string gameRootPath = Path.Combine(BepInEx.Paths.GameRootPath, TasScriptFileName);
        if (File.Exists(gameRootPath))
            return gameRootPath;
        return Path.Combine(BepInEx.Paths.ConfigPath, TasScriptFileName);
    }

    private void ResetTasCommandState()
    {
        _tasCommandIndex = 0;
        _tasCommandFrame = 0;
        _tasJumpStage = TasJumpStage.WaitingForJumps;
        _tasJumpHoldFramesLeft = 0;
        _tasJumpWaitFramesLeft = 0;
        _tasPrevJumpHeld = false;
        _tasCurrentJumpHeld = false;
        _tasCurrentLookHorizontal = 0f;
        _tasCurrentLookVertical = 0f;
        _tasStopAfterFrame = false;
    }

    private void PrepareCurrentTasCommandInput()
    {
        while (_tasCommandIndex < _tasCommands.Count)
        {
            var command = _tasCommands[_tasCommandIndex];

            if (command.type == TasCommandType.Wait)
            {
                if (command.holdFrames <= 0)
                {
                    AdvanceTasCommand();
                    continue;
                }

                _tasCommandFrame++;
                if (_tasCommandFrame >= command.holdFrames)
                    AdvanceTasCommand();
                return;
            }

            if (command.type == TasCommandType.Slide)
            {
                if (command.holdFrames <= 0)
                {
                    AdvanceTasCommand();
                    continue;
                }

                // Slide timer only starts once moth is grounded (jumps == maxJumps).
                if (_tasCommandFrame == 0 && !IsMothGroundedForSlide())
                    return;

                _tasCommandFrame++;
                if (_tasCommandFrame >= command.holdFrames)
                    AdvanceTasCommand();
                return;
            }

            if (_tasJumpStage == TasJumpStage.WaitingForJumps)
            {
                if (GetCurrentMothJumps() <= 0)
                    return;

                _tasJumpStage = TasJumpStage.HoldingJump;
                _tasJumpHoldFramesLeft = command.holdFrames;
                _tasJumpWaitFramesLeft = command.waitFrames;
            }

            if (_tasJumpStage == TasJumpStage.HoldingJump)
            {
                _tasCurrentJumpHeld = true;
                _tasCurrentLookVertical = 1f;
                _tasJumpHoldFramesLeft--;
                if (_tasJumpHoldFramesLeft <= 0)
                    _tasJumpStage = TasJumpStage.ReleaseFrame;
                return;
            }

            if (_tasJumpStage == TasJumpStage.ReleaseFrame)
            {
                if (command.type == TasCommandType.ReverseJump)
                {
                    _tasJumpStage = _tasJumpWaitFramesLeft > 0
                        ? TasJumpStage.WaitAfterRelease
                        : TasJumpStage.ReverseFrame;
                }
                else
                {
                    AdvanceTasCommand();
                }
                return;
            }

            if (_tasJumpStage == TasJumpStage.WaitAfterRelease)
            {
                _tasJumpWaitFramesLeft--;
                if (_tasJumpWaitFramesLeft <= 0)
                    _tasJumpStage = TasJumpStage.ReverseFrame;
                return;
            }

            if (_tasJumpStage == TasJumpStage.ReverseFrame)
            {
                _tasCurrentLookHorizontal = GetReverseInputDirection();
                AdvanceTasCommand();
                return;
            }
        }

        _tasStopAfterFrame = true;
    }

    private void AdvanceTasCommand()
    {
        _tasCommandIndex++;
        _tasCommandFrame = 0;
        _tasJumpStage = TasJumpStage.WaitingForJumps;
        _tasJumpHoldFramesLeft = 0;
        _tasJumpWaitFramesLeft = 0;
        if (_tasCommandIndex >= _tasCommands.Count)
            _tasStopAfterFrame = true;
    }

    private int GetCurrentMothJumps()
    {
        if (_moth == null || _jumpsField == null)
            return 0;

        object value = _jumpsField.GetValue(_moth);
        if (value is int)
            return (int)value;
        return 0;
    }

    private bool IsMothGroundedForSlide()
    {
        if (_moth == null || _jumpsField == null || _maxJumpsField == null)
            return false;

        object jumpsValue = _jumpsField.GetValue(_moth);
        object maxJumpsValue = _maxJumpsField.GetValue(_moth);
        if (!(jumpsValue is int) || !(maxJumpsValue is int))
            return false;

        return (int)jumpsValue == (int)maxJumpsValue;
    }

    private float GetReverseInputDirection()
    {
        int facing = 1;
        if (_moth != null && _facingDirectionField != null)
        {
            object value = _facingDirectionField.GetValue(_moth);
            if (value is int)
                facing = (int)value;
        }
        return facing < 0 ? 1f : -1f;
    }

    private void ChainReplay()
    {
        if (!_replayActive) return;

        if (_currentSplitIndex < SplitNames.Length && IsSegmentMode())
        {
            var frames = GetSegmentGhostFramesForMode(_currentSplitIndex);
            if (frames != null && frames.Length > 0)
            {
                _replayFrames = frames;
                _replayIndex = 0;
                Logger.LogInfo("TigerMoth: replay chained to split " + _currentSplitIndex
                    + " (" + _replayFrames.Length + " frames)");
            }
            else
            {
                StopReplay();
            }
        }
        else
        {
            StopReplay();
        }
    }

    // ── Helpers ─────────────────────────────────────────

    private Animator GetMothAnimator()
    {
        return (Animator)_animatorField.GetValue(_moth);
    }

    private bool IsSegmentMode()
    {
        return _practiceMode || _tasMode;
    }

    private float ComputeSegment(int idx, float totalTime)
    {
        if (IsSegmentMode())
            return totalTime;
        return idx == 0 ? totalTime : totalTime - _runTotals[idx - 1];
    }

    private void ResetTrackingArrays()
    {
        _runTotals = new float[SplitNames.Length];
        _displaySegTimes = new float[SplitNames.Length];
        _displayTotalTimes = new float[SplitNames.Length];
        _splitLocked = new bool[SplitNames.Length];
        _splitIsGold = new bool[SplitNames.Length];
        float[] activeBestSegments = _tasMode ? _tasBestSegments : _bestSegments;
        _bestSegmentsSnapshot = activeBestSegments != null
            ? (float[])activeBestSegments.Clone() : null;
        _pbSnapshot = _pbTotalTimes != null
            ? (float[])_pbTotalTimes.Clone() : null;
        UpdateCachedGolds();
    }

    private void UpdateCachedGolds()
    {
        _hasGolds = _bestSegmentsSnapshot != null
            && _bestSegmentsSnapshot.Length >= SplitNames.Length
            && System.Array.TrueForAll(_bestSegmentsSnapshot, s => s > 0f);
        if (_hasGolds)
        {
            _bestPossibleTime = 0f;
            for (int i = 0; i < SplitNames.Length; i++)
            {
                if (!IsSegmentMode() && _splitLocked != null && i < _splitLocked.Length && _splitLocked[i])
                    _bestPossibleTime += _displaySegTimes[i];
                else
                    _bestPossibleTime += _bestSegmentsSnapshot[i];
            }
        }
    }

    private void MarkSegmentStart()
    {
        if (_ghostSegmentStarts != null && _currentSplitIndex < SplitNames.Length)
            _ghostSegmentStarts[_currentSplitIndex] = _ghostRecording != null ? _ghostRecording.Count : 0;
    }

    private void StartSegmentGhostPlayback(int splitIndex)
    {
        _ghostActivePlayback = GetSegmentGhostFramesForMode(splitIndex);
        _ghostPlaybackIndex = 0;
        ApplyGhostVisibility();
    }

    private void RecordSplitCompletion(int idx, float totalTime)
    {
        float segmentTime = ComputeSegment(idx, totalTime);
        _runTotals[idx] = totalTime;

        bool isGold = UpdateBestSegment(idx, segmentTime, _tasMode);

        _displaySegTimes[idx] = segmentTime;
        _displayTotalTimes[idx] = totalTime;
        _splitLocked[idx] = true;
        _splitIsGold[idx] = isGold;

        if (_tasMode)
        {
            if (_ghostRecording != null && _ghostSegmentStarts != null)
                SaveTasSegment(idx, _ghostSegmentStarts[idx], _ghostRecording.Count);
        }
        else
        {
            // Save gold ghost segment
            if (isGold && _ghostRecording != null && _ghostSegmentStarts != null)
                SaveGoldSegment(idx, _ghostSegmentStarts[idx], _ghostRecording.Count);
        }

        if (!_tasMode || isGold)
            SavePB();
        UpdateCachedGolds();

        Logger.LogInfo(string.Format("TigerMoth: split '{0}' seg={1} total={2}",
            SplitNames[idx], FormatTime(segmentTime), FormatTime(totalTime)));
    }

    private static Texture2D MakeSolidTexture(Color color)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }

    // ── Load state via scene reload ───────────────────────

    private void ReloadAndRestore(bool keepTasArmed = false)
    {
        _replayActive = false;
        _replayFrames = null;
        StopTasPlayback();
        ClearTapState();
        if (!keepTasArmed)
        {
            _tasArmedAfterReset = false;
            _tasMode = false;
        }
        _pendingLoad = true;
        _moth = null;
        _rb = null;
        _ghost = null;
        _ghostAnimator = null;
        DestroyAttemptGhosts();
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
            if (!_practiceMode && !_tasMode)
                SaveAttempt();
            StopReplay();
            StopTasPlayback();
            _runActive = false;
            _tasMode = false;
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
                return AreaOverlap(AreaChurch);
            case 1: // "Gift" — ExtraJump collected
                return _extraJump != null && (bool)_extraJumpUsedField.GetValue(_extraJump);
            case 2: // "Tower" — moth enters The Tower
                return AreaOverlap(AreaTower);
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
        ResetTrackingArrays();

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

            _splitTickingField.SetValue(split, i == 0);
            _splitTimeValueField.SetValue(split, 0f);
        }

        // Ghost recording + playback
        _ghostRecording = new List<GhostFrame>(GhostRecordingInitialCapacity);
        _ghostSegmentStarts = new int[SplitNames.Length];
        _ghostPlaybackIndex = 0;
        if (_attemptSwarmActive)
            ShowAttemptGhosts(true);

        if (IsSegmentMode())
        {
            // Practice/TAS: start per-split ghost
            _ghostActivePlayback = null;
            if (_ghost != null) _ghost.SetActive(false);
            int firstSplit = _practiceMode
                ? (_practiceSkipIndex < 0 ? 0 : _practiceSkipIndex + 1)
                : 0;
            StartSegmentGhostPlayback(firstSplit);
        }
        else if (_tapMode)
        {
            // TAP mode: play TAP best ghost
            _ghostActivePlayback = _tapGhostFrames;
            ApplyGhostVisibility();
        }
        else
        {
            // Normal run: play selected full-run ghost source
            _ghostActivePlayback = GetFullRunGhostFramesForMode();
            ApplyGhostVisibility();
        }

        if (_tasArmedAfterReset)
            StartTasPlayback();

        Logger.LogInfo("TigerMoth: managed run started (" + SplitNames.Length + " splits)");
    }

    private void HideSplitVisuals(Split split)
    {
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

            MarkSegmentStart();

            if (_currentSplitIndex < _managedSplits.Count)
            {
                var next = _managedSplits[_currentSplitIndex];
                _splitTimeValueField.SetValue(next, 0f);
                _splitTickingField.SetValue(next, true);

                if (IsSegmentMode())
                    StartSegmentGhostPlayback(_currentSplitIndex);
            }

            if (_replayActive && IsSegmentMode())
                ChainReplay();
            return;
        }

        float totalTime = _managedSplits[_currentSplitIndex].Lock();
        RecordSplitCompletion(_currentSplitIndex, totalTime);

        _currentSplitIndex++;

        MarkSegmentStart();

        if (_currentSplitIndex < _managedSplits.Count)
        {
            var next = _managedSplits[_currentSplitIndex];
            if (IsSegmentMode())
                _splitTimeValueField.SetValue(next, 0f);
            else
                _splitTimeValueField.SetValue(next, totalTime);
            _splitTickingField.SetValue(next, true);

            if (IsSegmentMode())
                StartSegmentGhostPlayback(_currentSplitIndex);
        }

        if (_replayActive && IsSegmentMode())
            ChainReplay();
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
        RecordSplitCompletion(lastIdx, totalTime);

        // Check for PB only in normal mode (practice runs skip splits)
        if (!IsSegmentMode())
        {
            SaveAttempt();
            if (System.Array.TrueForAll(_runTotals, t => t > 0)
                && (_pbTotalTimes == null || totalTime < _pbTotalTimes[lastIdx]))
            {
                float oldPbTotal = (_pbTotalTimes != null && _pbTotalTimes.Length > lastIdx)
                    ? _pbTotalTimes[lastIdx]
                    : -1f;
                _pbTotalTimes = (float[])_runTotals.Clone();
                SaveGhost(oldPbTotal > 0f ? (float?)oldPbTotal : null);
                Logger.LogInfo("TigerMoth: New PB! " + FormatTime(totalTime));
            }
        }
        else if (_tasMode)
        {
            SaveTasGhost();
        }

        _currentSplitIndex = SplitNames.Length;

        if (_replayActive)
            StopReplay();
        StopTasPlayback();
    }

    // ── Formatting helpers ────────────────────────────────

    private static string FormatTime(float t)
    {
        return t.ToString("F2");
    }

    // ── PB persistence ────────────────────────────────────

    private bool UpdateBestSegment(int idx, float segmentTime, bool tasMode)
    {
        float[] segments = tasMode ? _tasBestSegments : _bestSegments;
        if (segments == null || segments.Length != SplitNames.Length)
            segments = new float[SplitNames.Length];
        if (idx >= segments.Length)
            return false;

        if (segments[idx] <= 0 || segmentTime < segments[idx])
        {
            segments[idx] = segmentTime;
            if (tasMode)
                _tasBestSegments = segments;
            else
                _bestSegments = segments;
            return true;
        }

        if (tasMode)
            _tasBestSegments = segments;
        else
            _bestSegments = segments;
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
                else if (line.StartsWith("tas:"))
                    _tasBestSegments = ParseFloats(line.Substring(4));
            }
            if (_pbTotalTimes != null)
                Logger.LogInfo("TigerMoth: PB loaded — " + FormatTime(_pbTotalTimes[_pbTotalTimes.Length - 1]));
            if (_bestSegments != null)
            {
                float sum = 0;
                for (int i = 0; i < _bestSegments.Length; i++) sum += _bestSegments[i];
                Logger.LogInfo("TigerMoth: Sum of best segments — " + FormatTime(sum));
            }
            if (_tasBestSegments != null)
            {
                float tasSum = 0;
                for (int i = 0; i < _tasBestSegments.Length; i++) tasSum += _tasBestSegments[i];
                Logger.LogInfo("TigerMoth: TAS sum of best segments — " + FormatTime(tasSum));
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
            if (_tasBestSegments != null)
                lines.Add("tas:" + JoinFloats(_tasBestSegments));
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

    private static string TasGhostPath()
    {
        return Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_tas.bin");
    }

    private static string TasSplitGhostPath(int splitIndex)
    {
        return Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_tas_" + splitIndex + ".bin");
    }

    private static string TapGhostPath()
    {
        return Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_tap_ghost.bin");
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
                w.Write(frames[i].keys);
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
                frames[i].keys = r.ReadByte();
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

        try
        {
            string path = TasGhostPath();
            if (File.Exists(path))
            {
                _tasGhostPlaybackFrames = ReadGhostFrames(path);
                Logger.LogInfo("TigerMoth: TAS ghost loaded (" + _tasGhostPlaybackFrames.Length + " frames)");
            }
            else
            {
                _tasGhostPlaybackFrames = null;
            }
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to load TAS ghost: " + e.Message);
            _tasGhostPlaybackFrames = null;
        }

        _goldGhostFrames = new GhostFrame[SplitNames.Length][];
        _tasSplitGhostFrames = new GhostFrame[SplitNames.Length][];
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

            try
            {
                string tasPath = TasSplitGhostPath(i);
                if (File.Exists(tasPath))
                {
                    _tasSplitGhostFrames[i] = ReadGhostFrames(tasPath);
                    Logger.LogInfo("TigerMoth: TAS split ghost " + i + " loaded (" + _tasSplitGhostFrames[i].Length + " frames)");
                }
                else
                {
                    _tasSplitGhostFrames[i] = null;
                }
            }
            catch (System.Exception e)
            {
                Logger.LogError("TigerMoth: failed to load TAS split ghost " + i + ": " + e.Message);
                _tasSplitGhostFrames[i] = null;
            }
        }

        // TAP ghost
        try
        {
            string tapPath = TapGhostPath();
            if (File.Exists(tapPath))
            {
                _tapGhostFrames = ReadGhostFrames(tapPath);
                Logger.LogInfo("TigerMoth: TAP ghost loaded (" + _tapGhostFrames.Length + " frames)");
            }
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to load TAP ghost: " + e.Message);
            _tapGhostFrames = null;
        }

        // TAP best time
        try
        {
            string timePath = Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_tap_best.txt");
            if (File.Exists(timePath))
            {
                float.TryParse(File.ReadAllText(timePath).Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out _tapBestTime);
                if (_tapBestTime > 0f)
                    Logger.LogInfo("TigerMoth: TAP best time: " + FormatTime(_tapBestTime));
            }
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to load TAP best time: " + e.Message);
        }
    }


    private static string GhostArchivePath(float pbTime, int duplicateIndex)
    {
        string baseName = "TigerMoth_ghost_" + pbTime.ToString("F2", CultureInfo.InvariantCulture);
        if (duplicateIndex > 0)
            baseName += "_" + duplicateIndex;
        return Path.Combine(BepInEx.Paths.ConfigPath, baseName + ".bin");
    }

    private void SaveGhost(float? previousPbTotal)
    {
        if (_ghostRecording == null || _ghostRecording.Count == 0)
            return;
        try
        {
            var frames = _ghostRecording.ToArray();
            string currentPath = GhostPath();
            if (previousPbTotal.HasValue && File.Exists(currentPath))
            {
                int duplicateIndex = 0;
                string archivePath = GhostArchivePath(previousPbTotal.Value, duplicateIndex);
                while (File.Exists(archivePath))
                {
                    duplicateIndex++;
                    archivePath = GhostArchivePath(previousPbTotal.Value, duplicateIndex);
                }

                File.Move(currentPath, archivePath);
                Logger.LogInfo("TigerMoth: archived previous PB ghost to " + archivePath);
            }

            WriteGhostFrames(currentPath, frames);
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

    private void SaveTasGhost()
    {
        if (_ghostRecording == null || _ghostRecording.Count == 0)
            return;
        try
        {
            var frames = _ghostRecording.ToArray();
            WriteGhostFrames(TasGhostPath(), frames);
            _tasGhostPlaybackFrames = frames;
            Logger.LogInfo("TigerMoth: TAS ghost saved (" + frames.Length + " frames)");
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save TAS ghost: " + e.Message);
        }
    }

    private void SaveTasSegment(int splitIndex, int startFrame, int endFrame)
    {
        if (_ghostRecording == null || startFrame >= endFrame)
            return;
        try
        {
            int count = endFrame - startFrame;
            var frames = new GhostFrame[count];
            _ghostRecording.CopyTo(startFrame, frames, 0, count);
            WriteGhostFrames(TasSplitGhostPath(splitIndex), frames);
            if (_tasSplitGhostFrames == null || _tasSplitGhostFrames.Length != SplitNames.Length)
                _tasSplitGhostFrames = new GhostFrame[SplitNames.Length][];
            _tasSplitGhostFrames[splitIndex] = frames;
            Logger.LogInfo("TigerMoth: TAS split ghost " + splitIndex + " saved (" + count + " frames)");
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save TAS split ghost " + splitIndex + ": " + e.Message);
        }
    }

    // ── Attempt recording ─────────────────────────────────

    private static string AttemptsDir()
    {
        return Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_attempts");
    }

    private void SaveAttempt()
    {
        if (_ghostRecording == null || _ghostRecording.Count < 30)
            return; // skip trivially short attempts (< 0.5 seconds)
        try
        {
            string dir = AttemptsDir();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (_attemptNumber == 0)
            {
                // Resume numbering from existing files
                foreach (string file in Directory.GetFiles(dir, "attempt_*.bin"))
                {
                    string[] parts = Path.GetFileNameWithoutExtension(file).Split('_');
                    int num;
                    if (parts.Length >= 2 && int.TryParse(parts[1], out num) && num > _attemptNumber)
                        _attemptNumber = num;
                }
            }
            _attemptNumber++;

            string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string filename = "attempt_" + _attemptNumber.ToString("D4") + "_" + timestamp + ".bin";
            var frames = _ghostRecording.ToArray();
            WriteGhostFrames(Path.Combine(dir, filename), frames);
            Logger.LogInfo("TigerMoth: attempt " + _attemptNumber + " saved (" + frames.Length + " frames)");
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save attempt: " + e.Message);
        }
    }

    // ── Touch All Platforms ─────────────────────────────────

    private void ToggleTapMode()
    {
        _tapArmedAfterReset = true;
        _practiceMode = false;
        _tasMode = false;
        _pendingRestore = null;
        ReloadAndRestore();
    }

    private void StartTapMode()
    {
        InitTapPlatforms();
        _tapTouchedPlatforms = new HashSet<Collider2D>();
        _tapTimer = 0f;
        _tapTimerRunning = true;
        _tapComplete = false;
        _tapMode = true;
        _tapArmedAfterReset = false;
        Logger.LogInfo("TigerMoth: TAP mode ON (" + _tapTotalCount + " platforms)");
    }

    private static string ColliderKey(Collider2D col)
    {
        var t = col.transform;
        var sb = new System.Text.StringBuilder();
        var current = t;
        while (current != null)
        {
            if (sb.Length > 0) sb.Insert(0, '/');
            sb.Insert(0, current.name);
            current = current.parent;
        }
        var c = col.bounds.center;
        sb.Append('|');
        sb.Append(c.x.ToString("F3", CultureInfo.InvariantCulture));
        sb.Append(',');
        sb.Append(c.y.ToString("F3", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static readonly Vector2[] TapPlatformPositions =
    {
        new Vector2(-17.120f, 20.100f),
        new Vector2(-21.730f, 17.520f),
        new Vector2(-29.490f, -10.850f),
        new Vector2(-9.606f, -4.655f),
        new Vector2(-32.712f, -6.925f),
        new Vector2(2.336f, 4.688f),
        new Vector2(-36.890f, 0.290f),
        new Vector2(-11.535f, 11.311f),
        new Vector2(-30.640f, -0.650f),
        new Vector2(-13.932f, 20.680f),
        new Vector2(-11.302f, -20.700f),
        new Vector2(-31.780f, 80.863f),
        new Vector2(-22.230f, 97.055f),
        new Vector2(-55.939f, -10.124f),
        new Vector2(-40.731f, 40.410f),
        new Vector2(-35.353f, 49.152f),
        new Vector2(-22.230f, 50.613f),
        new Vector2(5.934f, 27.552f),
        new Vector2(-23.692f, -5.750f),
        new Vector2(-30.089f, 52.400f),
        new Vector2(-8.912f, -18.500f),
        new Vector2(-39.777f, 55.238f),
        new Vector2(-17.990f, 65.600f),
        new Vector2(-32.040f, 85.201f),
        new Vector2(-31.476f, 88.341f),
        new Vector2(-25.988f, 91.253f),
        new Vector2(-35.974f, 106.141f),
        new Vector2(-15.439f, 60.800f),
        new Vector2(0.212f, -16.566f),
        new Vector2(-36.688f, 48.291f),
        new Vector2(1.815f, 109.353f),
        new Vector2(-23.002f, 68.690f),
        new Vector2(-26.899f, 77.073f),
        new Vector2(-31.060f, 15.013f),
        new Vector2(-48.777f, 61.031f),
        new Vector2(9.307f, 40.321f),
        new Vector2(-35.943f, -10.410f),
        new Vector2(-44.630f, -13.395f),
        new Vector2(-32.165f, -7.374f),
        new Vector2(-20.853f, 6.520f),
        new Vector2(-15.477f, 14.670f),
        new Vector2(-22.810f, 19.430f),
        new Vector2(-41.860f, 0.990f),
        new Vector2(-22.199f, 38.850f),
        new Vector2(-14.775f, -3.776f),
        new Vector2(-43.124f, 30.959f),
        new Vector2(0.330f, 25.119f),
        new Vector2(-29.830f, 23.666f),
        new Vector2(-29.830f, 35.070f),
        new Vector2(-43.185f, 19.356f),
        new Vector2(-6.300f, 30.470f),
        new Vector2(-17.900f, 32.610f),
        new Vector2(-28.870f, 9.720f),
        new Vector2(-57.380f, -8.467f),
        new Vector2(-59.943f, -5.463f),
        new Vector2(-22.810f, 29.310f),
        new Vector2(-46.855f, 10.550f),
        new Vector2(-9.648f, 40.093f),
        new Vector2(-28.791f, 113.740f),
        new Vector2(-15.211f, 128.183f),
        new Vector2(-10.299f, 120.733f),
        new Vector2(-27.820f, 139.120f),
        new Vector2(-5.150f, 153.540f),
        new Vector2(-40.700f, 11.500f),
        new Vector2(-31.059f, 63.360f),
        new Vector2(-22.160f, 135.040f),
        new Vector2(-22.160f, 148.910f),
        new Vector2(-11.485f, 14.768f),
        new Vector2(-22.860f, 146.300f),
        new Vector2(-33.996f, 12.170f),
        new Vector2(-17.130f, 159.550f),
        new Vector2(-35.040f, 68.828f),
        new Vector2(-32.326f, 40.619f),
        new Vector2(-15.012f, 14.502f),
        new Vector2(-22.160f, 168.050f),
    };

    private void InitTapPlatforms()
    {
        _tapPlatforms = new HashSet<Collider2D>();
        if (_moth == null)
        {
            _tapTotalCount = 0;
            return;
        }

        const float tolerance = 0.01f;
        foreach (var col in FindObjectsOfType<Collider2D>())
        {
            if (!col.enabled || !col.gameObject.activeInHierarchy)
                continue;
            if (col.isTrigger)
                continue;
            var c = col.bounds.center;
            for (int i = 0; i < TapPlatformPositions.Length; i++)
            {
                if (Mathf.Abs(c.x - TapPlatformPositions[i].x) < tolerance
                    && Mathf.Abs(c.y - TapPlatformPositions[i].y) < tolerance)
                {
                    _tapPlatforms.Add(col);
                    break;
                }
            }
        }
        _tapTotalCount = _tapPlatforms.Count;
    }

    private void TickTapMode()
    {
        if (!_tapMode || _tapComplete || _moth == null || _rb == null)
            return;

        if (_tapTimerRunning)
            _tapTimer += Time.deltaTime;

        // Check contacts every frame — register any whitelisted platform
        // the moth is touching with an upward normal
        {
            var contacts = new List<ContactPoint2D>();
            _rb.GetContacts(contacts);
            foreach (var contact in contacts)
            {
                if (contact.normal.y <= 0.9f)
                    continue;
                var plat = contact.collider;
                if (plat == null || !_tapPlatforms.Contains(plat) || _tapTouchedPlatforms.Contains(plat))
                    continue;
                _tapTouchedPlatforms.Add(plat);
            }

            if (_tapTouchedPlatforms.Count >= _tapTotalCount)
            {
                _tapTimerRunning = false;
                _tapComplete = true;
                Logger.LogInfo("TigerMoth: All platforms touched! Time: " + FormatTime(_tapTimer));
                SaveTapGhost();
            }
        }
    }

    private void SaveTapDiscovery()
    {
        if (_tapTouchedPlatforms == null || _tapTouchedPlatforms.Count == 0)
            return;
        try
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_tap_discovery.txt");
            var lines = new List<string>();
            foreach (var col in _tapTouchedPlatforms)
            {
                if (col == null) continue;
                lines.Add(ColliderKey(col));
            }
            lines.Sort();
            File.WriteAllLines(path, lines.ToArray());
            Logger.LogInfo("TigerMoth: TAP discovery saved (" + lines.Count + " platforms) to " + path);
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save TAP discovery: " + e.Message);
        }
    }

    private void SaveTapGhost()
    {
        if (_ghostRecording == null || _ghostRecording.Count == 0)
            return;
        if (_tapBestTime > 0f && _tapTimer >= _tapBestTime)
        {
            Logger.LogInfo("TigerMoth: TAP time " + FormatTime(_tapTimer)
                + " did not beat best " + FormatTime(_tapBestTime));
            return;
        }
        try
        {
            var frames = _ghostRecording.ToArray();
            WriteGhostFrames(TapGhostPath(), frames);
            _tapGhostFrames = frames;
            _tapBestTime = _tapTimer;
            File.WriteAllText(
                Path.Combine(BepInEx.Paths.ConfigPath, "TigerMoth_tap_best.txt"),
                _tapBestTime.ToString("F4", CultureInfo.InvariantCulture));
            Logger.LogInfo("TigerMoth: New TAP best! " + FormatTime(_tapBestTime)
                + " (" + frames.Length + " frames)");
        }
        catch (System.Exception e)
        {
            Logger.LogError("TigerMoth: failed to save TAP ghost: " + e.Message);
        }
    }

    private void DrawTapColliders()
    {
        if (!_tapMode || _moth == null || Camera.main == null)
            return;
        if (Event.current.type != EventType.Repaint)
            return;

        var cam = Camera.main;
        Color untouchedColor = new Color(1f, 0.3f, 0.3f, 0.7f);
        Color touchedColor = new Color(0.3f, 1f, 0.3f, 0.7f);

        foreach (var col in _tapPlatforms)
        {
            if (col == null || !col.enabled || !col.gameObject.activeInHierarchy)
                continue;
            Color color = _tapTouchedPlatforms.Contains(col) ? touchedColor : untouchedColor;
            DrawCollider2DScreen(cam, col, color);
        }
    }

    private void ClearTapState()
    {
        _tapMode = false;
        _tapPlatforms = null;
        _tapTouchedPlatforms = null;
        _tapTimerRunning = false;
        _tapComplete = false;
    }

    // ── Camera zoom ───────────────────────────────────────


    // ── Attempt swarm ──────────────────────────────────────

    private void LoadAllAttempts()
    {
        _attemptFrames = new List<GhostFrame[]>();
        string dir = AttemptsDir();
        if (!Directory.Exists(dir))
            return;
        var files = Directory.GetFiles(dir, "attempt_*.bin");
        System.Array.Sort(files);
        foreach (string file in files)
        {
            try
            {
                var frames = ReadGhostFrames(file);
                if (frames != null && frames.Length > 0)
                    _attemptFrames.Add(frames);
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("TigerMoth: failed to load attempt " + file + ": " + e.Message);
            }
        }
        Logger.LogInfo("TigerMoth: loaded " + _attemptFrames.Count + " attempts");
    }

    private void CreateAttemptGhosts()
    {
        if (_attemptGhosts != null && _attemptGhosts.Count == _attemptFrames.Count)
            return; // already created
        DestroyAttemptGhosts();
        _attemptGhosts = new List<GameObject>(_attemptFrames.Count);
        if (_moth == null) return;

        var mothSr = _moth.GetComponentInChildren<SpriteRenderer>();
        if (mothSr == null) return;

        for (int i = 0; i < _attemptFrames.Count; i++)
        {
            var go = new GameObject("AttemptGhost_" + i);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = mothSr.sprite;
            sr.sortingLayerID = mothSr.sortingLayerID;
            sr.sortingOrder = mothSr.sortingOrder - 1;
            sr.color = new Color(1f, 1f, 1f, 0.08f);
            go.SetActive(false);
            _attemptGhosts.Add(go);
        }
    }

    private void DestroyAttemptGhosts()
    {
        if (_attemptGhosts == null) return;
        foreach (var go in _attemptGhosts)
        {
            if (go != null)
                Object.Destroy(go);
        }
        _attemptGhosts = null;
    }

    private void ShowAttemptGhosts(bool show)
    {
        if (_attemptGhosts == null) return;
        foreach (var go in _attemptGhosts)
        {
            if (go != null)
                go.SetActive(show);
        }
    }

    private void TickAttemptSwarm()
    {
        if (!_attemptSwarmActive || _attemptGhosts == null || !_runActive)
            return;

        // Sync with main ghost playback index
        int idx = _ghostPlaybackIndex;

        for (int i = 0; i < _attemptGhosts.Count; i++)
        {
            var go = _attemptGhosts[i];
            if (go == null) continue;
            var frames = _attemptFrames[i];

            if (idx < frames.Length)
            {
                if (!go.activeSelf) go.SetActive(true);
                var f = frames[idx];
                go.transform.position = new Vector3(f.x, f.y, 0f);
                go.transform.eulerAngles = new Vector3(0f, f.flipX ? 180f : 0f, 0f);
            }
            else if (go.activeSelf)
            {
                go.SetActive(false);
            }
        }
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

    // ── Collider visualization ──────────────────────────────

    private void DrawCollider2DScreen(Camera cam, Collider2D col, Color color)
    {
        if (col is BoxCollider2D box)
        {
            var t = box.transform;
            Vector2 half = box.size * 0.5f;
            Vector2 o = box.offset;
            Vector2 tl = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x - half.x, o.y + half.y)));
            Vector2 tr = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x + half.x, o.y + half.y)));
            Vector2 br = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x + half.x, o.y - half.y)));
            Vector2 bl = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x - half.x, o.y - half.y)));
            DrawGUILine(tl, tr, color); DrawGUILine(tr, br, color);
            DrawGUILine(br, bl, color); DrawGUILine(bl, tl, color);
        }
        else if (col is CircleCollider2D circle)
        {
            var t = circle.transform;
            Vector3 center = t.TransformPoint(circle.offset);
            float radius = circle.radius * Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y));
            const int segments = 32;
            float step = 2f * Mathf.PI / segments;
            Vector2 prev = WorldToGUI(cam, center + new Vector3(radius, 0f, 0f));
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * step;
                Vector2 next = WorldToGUI(cam, center + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f));
                DrawGUILine(prev, next, color);
                prev = next;
            }
        }
        else if (col is PolygonCollider2D poly)
        {
            var t = poly.transform;
            for (int p = 0; p < poly.pathCount; p++)
            {
                var points = poly.GetPath(p);
                for (int i = 0; i < points.Length; i++)
                {
                    Vector2 a = WorldToGUI(cam, t.TransformPoint(points[i]));
                    Vector2 b = WorldToGUI(cam, t.TransformPoint(points[(i + 1) % points.Length]));
                    DrawGUILine(a, b, color);
                }
            }
        }
        else if (col is EdgeCollider2D edge)
        {
            var t = edge.transform;
            var points = edge.points;
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector2 a = WorldToGUI(cam, t.TransformPoint(points[i]));
                Vector2 b = WorldToGUI(cam, t.TransformPoint(points[i + 1]));
                DrawGUILine(a, b, color);
            }
        }
        else if (col is CapsuleCollider2D capsule)
        {
            var t = capsule.transform;
            Vector2 o = capsule.offset;
            Vector2 sz = capsule.size;
            bool vertical = capsule.direction == CapsuleDirection2D.Vertical;
            float halfW = sz.x * 0.5f;
            float halfH = sz.y * 0.5f;
            float radius = vertical ? halfW : halfH;
            float straight = vertical ? Mathf.Max(0f, halfH - radius) : Mathf.Max(0f, halfW - radius);
            const int capSegs = 16;
            if (vertical)
            {
                Vector2 tlS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x - radius, o.y + straight)));
                Vector2 blS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x - radius, o.y - straight)));
                Vector2 trS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x + radius, o.y + straight)));
                Vector2 brS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x + radius, o.y - straight)));
                DrawGUILine(tlS, blS, color); DrawGUILine(trS, brS, color);
                DrawArcScreen(cam, t, o + new Vector2(0f, straight), radius, 0f, Mathf.PI, capSegs, color);
                DrawArcScreen(cam, t, o + new Vector2(0f, -straight), radius, Mathf.PI, 2f * Mathf.PI, capSegs, color);
            }
            else
            {
                Vector2 tlS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x - straight, o.y + radius)));
                Vector2 trS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x + straight, o.y + radius)));
                Vector2 blS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x - straight, o.y - radius)));
                Vector2 brS = WorldToGUI(cam, t.TransformPoint(new Vector2(o.x + straight, o.y - radius)));
                DrawGUILine(tlS, trS, color); DrawGUILine(blS, brS, color);
                DrawArcScreen(cam, t, o + new Vector2(straight, 0f), radius, -Mathf.PI * 0.5f, Mathf.PI * 0.5f, capSegs, color);
                DrawArcScreen(cam, t, o + new Vector2(-straight, 0f), radius, Mathf.PI * 0.5f, Mathf.PI * 1.5f, capSegs, color);
            }
        }
        else if (col is CompositeCollider2D composite)
        {
            var t = composite.transform;
            for (int p = 0; p < composite.pathCount; p++)
            {
                int count = composite.GetPathPointCount(p);
                var points = new Vector2[count];
                composite.GetPath(p, points);
                for (int i = 0; i < count; i++)
                {
                    Vector2 a = WorldToGUI(cam, t.TransformPoint(points[i]));
                    Vector2 b = WorldToGUI(cam, t.TransformPoint(points[(i + 1) % count]));
                    DrawGUILine(a, b, color);
                }
            }
        }
    }

    private void DrawArcScreen(Camera cam, Transform t, Vector2 center, float radius, float startAngle, float endAngle, int segments, Color color)
    {
        float step = (endAngle - startAngle) / segments;
        Vector2 prev = WorldToGUI(cam, t.TransformPoint(center + new Vector2(Mathf.Cos(startAngle) * radius, Mathf.Sin(startAngle) * radius)));
        for (int i = 1; i <= segments; i++)
        {
            float angle = startAngle + i * step;
            Vector2 next = WorldToGUI(cam, t.TransformPoint(center + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius)));
            DrawGUILine(prev, next, color);
            prev = next;
        }
    }

    private static Vector2 WorldToGUI(Camera cam, Vector3 world)
    {
        Vector3 screen = cam.WorldToScreenPoint(world);
        return new Vector2(screen.x, Screen.height - screen.y);
    }

    private static Texture2D _lineTex;

    private static void DrawGUILine(Vector2 a, Vector2 b, Color color)
    {
        if (_lineTex == null)
        {
            _lineTex = new Texture2D(1, 1);
            _lineTex.SetPixel(0, 0, Color.white);
            _lineTex.Apply();
        }

        var savedColor = GUI.color;
        GUI.color = color;

        float dx = b.x - a.x;
        float dy = b.y - a.y;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) { GUI.color = savedColor; return; }

        float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;

        var pivot = a;
        var savedMatrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, pivot);
        GUI.DrawTexture(new Rect(a.x, a.y - 0.5f, len, 2f), _lineTex);
        GUI.matrix = savedMatrix;

        GUI.color = savedColor;
    }

    // ── GUI ───────────────────────────────────────────────

    private static readonly string[][] OverlayBindings = {
        new[] { "H", "Save" },
        new[] { "I", "Load" },
        new[] { "1-5", "Checkpoints" },
        new[] { "F", "Follow ghost" },
        new[] { "V", "Detail view" },
        new[] { "P", "TAP mode" },
        new[] { "G", "Cycle ghost" },
        new[] { "[", "Zoom in" },
        new[] { "]", "Zoom out" },
    };

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

            _keycapTex = MakeSolidTexture(new Color(1f, 1f, 1f, 0.15f));
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
            _infoStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);

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

            _panelBgTex = MakeSolidTexture(new Color(0f, 0f, 0f, 0.75f));
            _panelBgStyle = new GUIStyle();
            _panelBgStyle.normal.background = _panelBgTex;

            _splitActiveTex = MakeSolidTexture(new Color(1f, 1f, 1f, 0.2f));
            _splitActiveStyle = new GUIStyle();
            _splitActiveStyle.normal.background = _splitActiveTex;

            _reusableContent = new GUIContent();
        }

        // ── TigerMoth overlay (top-left) ──
        DrawOverlay();

        // ── Split timer table (top-right) ──
        if (_tapMode)
            DrawTapTable();
        else if (_runActive && _managedSplits.Count > 0)
            DrawSplitTable();

        // ── Input & physics display (bottom-left) ──
        DrawInputDisplay();

        // ── Collider visualization ──
        DrawTapColliders();
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

        float tableH = rowH + OverlayBindings.Length * rowH; // header + rows

        float tableX = 10f;
        float tableY = 10f;

        // Background
        GUI.Box(new Rect(tableX, tableY, tableW + pad * 2, tableH + pad * 2),
            GUIContent.none, _panelBgStyle);

        float cx = tableX + pad;
        float cy = tableY + pad;

        // Header
        GUI.Label(new Rect(cx, cy, 400, rowH), "TigerMoth  v" + Version, _headerStyle);
        cy += rowH;

        // Keybindings
        for (int i = 0; i < OverlayBindings.Length; i++)
        {
            float ky = cy + (rowH - keycapH) * 0.5f;
            GUI.Box(new Rect(cx, ky, keycapW, keycapH), OverlayBindings[i][0], _keycapStyle);
            GUI.Label(new Rect(cx + keycapW + gap, cy, actionW, rowH),
                OverlayBindings[i][1], _actionStyle);
            cy += rowH;
        }
    }

    private GUIStyle _inputLabelStyle;
    private GUIStyle _inputMonoStyle;
    private GUIStyle _inputBtnStyle;
    private GUIStyle _inputBtnActiveStyle;
    private Texture2D _inputBtnTex;
    private Texture2D _inputBtnActiveTex;

    private void DrawInputDisplay()
    {
        if (_moth == null || _rb == null) return;

        if (_inputLabelStyle == null)
        {
            _inputLabelStyle = new GUIStyle(GUI.skin.label);
            _inputLabelStyle.fontSize = 28;
            _inputLabelStyle.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
            _inputLabelStyle.alignment = TextAnchor.MiddleLeft;

            _inputMonoStyle = new GUIStyle(_inputLabelStyle);
            _inputMonoStyle.alignment = TextAnchor.MiddleRight;

            _inputBtnTex = MakeSolidTexture(new Color(1f, 1f, 1f, 0.15f));
            _inputBtnActiveTex = MakeSolidTexture(new Color(1f, 1f, 1f, 0.6f));

            _inputBtnStyle = new GUIStyle(GUI.skin.label);
            _inputBtnStyle.fontSize = 24;
            _inputBtnStyle.fontStyle = FontStyle.Bold;
            _inputBtnStyle.alignment = TextAnchor.MiddleCenter;
            _inputBtnStyle.normal.textColor = new Color(1f, 1f, 1f, 0.4f);
            _inputBtnStyle.normal.background = _inputBtnTex;

            _inputBtnActiveStyle = new GUIStyle(_inputBtnStyle);
            _inputBtnActiveStyle.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
            _inputBtnActiveStyle.normal.background = _inputBtnActiveTex;
        }

        var controls = InputManager.controls;
        float h = controls != null ? controls.GetAxis("LookHorizontal") : 0f;
        float v = controls != null ? controls.GetAxis("LookVertical") : 0f;

        const float btnSize = 42f;
        const float btnGap = 4f;
        const float pad = 10f;
        float dpadW = (btnSize + btnGap) * 3 - btnGap;
        float dpadH = btnSize * 3 + btnGap * 2;

        float panelW, panelH;
        if (_inputDetailMode)
        {
            panelW = 340f;
            panelH = pad + dpadH + 8f + 32f + 32f + pad;
        }
        else
        {
            panelW = pad * 2 + dpadW;
            panelH = pad * 2 + dpadH;
        }

        float px = 10f;
        float py = Screen.height - panelH - 10f;

        GUI.Box(new Rect(px, py, panelW, panelH), GUIContent.none, _panelBgStyle);

        float cx = px + pad;
        float cy = py + pad;

        // D-pad: 3x3 grid, only up/down/left/right
        float gridX = cx;
        float gridY = cy;

        bool up = v > 0.1f;
        bool down = v < -0.1f;
        bool left = h < -0.1f;
        bool right = h > 0.1f;

        GUI.Box(new Rect(gridX + btnSize + btnGap, gridY, btnSize, btnSize),
            "\u25B2", up ? _inputBtnActiveStyle : _inputBtnStyle);
        GUI.Box(new Rect(gridX, gridY + btnSize + btnGap, btnSize, btnSize),
            "\u25C0", left ? _inputBtnActiveStyle : _inputBtnStyle);
        GUI.Box(new Rect(gridX + (btnSize + btnGap) * 2, gridY + btnSize + btnGap, btnSize, btnSize),
            "\u25B6", right ? _inputBtnActiveStyle : _inputBtnStyle);
        GUI.Box(new Rect(gridX + btnSize + btnGap, gridY + (btnSize + btnGap) * 2, btnSize, btnSize),
            "\u25BC", down ? _inputBtnActiveStyle : _inputBtnStyle);

        if (!_inputDetailMode)
            return;

        // Jump charge to the right of the d-pad
        float chargeX = gridX + (btnSize + btnGap) * 3 + 12f;
        float chargeY = gridY;
        float currentCharge = _cachedChargingTime;
        bool charging = currentCharge > 0f && !_jumpFiredStaleCharge;
        bool chargingJump1 = charging && _cachedJumps == _cachedMaxJumps;
        bool chargingJump2 = charging && _cachedJumps < _cachedMaxJumps && _cachedMaxJumps >= 2;

        float display1 = chargingJump1 ? currentCharge : _jumpCharge1;
        float display2 = chargingJump2 ? currentCharge : _jumpCharge2;

        GUI.Label(new Rect(chargeX, chargeY, 100f, 32f), "charge", _inputLabelStyle);
        chargeY += 28f;
        GUI.Label(new Rect(chargeX, chargeY, 100f, 32f),
            display1 > 0f ? string.Format("{0:F3}s", display1) : "---",
            ChargeStyle(display1, chargingJump1));
        chargeY += 28f;
        GUI.Label(new Rect(chargeX, chargeY, 100f, 32f),
            display2 > 0f ? string.Format("{0:F3}s", display2) : "---",
            ChargeStyle(display2, chargingJump2));

        cy = gridY + dpadH + 8f;

        // Velocity & normalized velocity — fixed columns
        Vector2 vel = _rb.velocity;
        const float colLabel = 0f;
        const float labelW = 80f;
        const float colX = 85f;
        const float colY = 190f;
        const float colW = 90f;
        const float rowHPhys = 32f;

        GUI.Label(new Rect(cx + colLabel, cy, labelW, rowHPhys), "vel", _inputLabelStyle);
        GUI.Label(new Rect(cx + colX, cy, colW, rowHPhys), string.Format("{0:F2}", vel.x), _inputMonoStyle);
        GUI.Label(new Rect(cx + colY, cy, colW, rowHPhys), string.Format("{0:F2}", vel.y), _inputMonoStyle);
        cy += rowHPhys;

        Vector2 norm = vel.sqrMagnitude > 0.0001f ? vel.normalized : Vector2.zero;
        GUI.Label(new Rect(cx + colLabel, cy, labelW, rowHPhys), "norm", _inputLabelStyle);
        GUI.Label(new Rect(cx + colX, cy, colW, rowHPhys), string.Format("{0:F2}", norm.x), _inputMonoStyle);
        GUI.Label(new Rect(cx + colY, cy, colW, rowHPhys), string.Format("{0:F2}", norm.y), _inputMonoStyle);
    }

    private GUIStyle _chargeColorStyle;

    private GUIStyle ChargeStyle(float charge, bool isLive)
    {
        const float maxCT = 0.7f;
        if (charge <= 0f)
            return _inputLabelStyle;

        float frac = (charge / maxCT) % 1f;
        int cycle = Mathf.FloorToInt(charge / maxCT);
        float effective = (cycle % 2 == 0) ? frac : 1f - frac;
        float distFromPeak = (1f - effective) * maxCT;

        Color color;
        if (distFromPeak <= 0.02f)
            color = new Color(0.2f, 1f, 0.2f, 0.9f);
        else if (distFromPeak <= 0.05f)
            color = new Color(1f, 1f, 0.2f, 0.9f);
        else if (distFromPeak <= 0.1f)
            color = new Color(1f, 0.6f, 0.1f, 0.9f);
        else if (isLive)
            color = new Color(1f, 1f, 1f, 0.85f);
        else
            return _inputLabelStyle;

        if (_chargeColorStyle == null)
        {
            _chargeColorStyle = new GUIStyle(_inputLabelStyle);
            _chargeColorStyle.fontStyle = FontStyle.Bold;
        }
        _chargeColorStyle.normal.textColor = color;
        return _chargeColorStyle;
    }

    private static readonly Color ColorAhead = new Color(0.27f, 1f, 0.27f);
    private static readonly Color ColorBehind = new Color(1f, 0.27f, 0.27f);
    private static readonly Color ColorGold = new Color(1f, 0.84f, 0f);
    private static readonly Color ColorGray = new Color(0.5f, 0.5f, 0.5f);

    private void DrawTapTable()
    {
        const float tableW = 340f;
        const float rowH = 46f;
        const float timerH = 60f;
        const float pad = 10f;
        bool hasBest = _tapBestTime > 0f;
        float tableH = rowH + timerH + 8f + (hasBest ? rowH : 0f);

        float tableX = Screen.width - tableW - pad * 2 - 10f;
        float tableY = 10f;

        GUI.Box(new Rect(tableX, tableY, tableW + pad * 2, tableH + pad * 2),
            GUIContent.none, _panelBgStyle);

        float cx = tableX + pad;
        float cy = tableY + pad;

        int touched = _tapTouchedPlatforms != null ? _tapTouchedPlatforms.Count : 0;
        string countText = touched + " / " + _tapTotalCount + " platforms";
        Color countColor = _tapComplete ? ColorAhead : Color.white;
        var origColor = _splitNameStyle.normal.textColor;
        _splitNameStyle.normal.textColor = countColor;
        GUI.Label(new Rect(cx, cy, tableW, rowH), countText, _splitNameStyle);
        _splitNameStyle.normal.textColor = origColor;
        cy += rowH + 8f;

        var origTimerColor = _timerStyle.normal.textColor;
        _timerStyle.normal.textColor = _tapComplete ? ColorAhead : Color.white;
        GUI.Label(new Rect(cx, cy, tableW, timerH), FormatTime(_tapTimer), _timerStyle);
        _timerStyle.normal.textColor = origTimerColor;
        cy += timerH;

        if (hasBest)
        {
            var orig2 = _splitNameStyle.normal.textColor;
            _splitNameStyle.normal.textColor = ColorGray;
            GUI.Label(new Rect(cx, cy, tableW, rowH),
                "Best: " + FormatTime(_tapBestTime), _splitNameStyle);
            _splitNameStyle.normal.textColor = orig2;
        }
    }

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
        bool segmentMode = IsSegmentMode();

        float deltaW = segmentMode ? deltaWPractice : deltaWNormal;
        float tableW = segmentMode
            ? nameW + deltaW + segW
            : nameW + deltaW + segW + totalW;
        const float infoH = 36f;
        float headerH = segmentMode ? rowH : 0f;
        float tableH = headerH + SplitNames.Length * rowH + timerGap + timerH
            + (_hasGolds ? infoH : 0f);

        float tableX = Screen.width - tableW - pad * 2 - 10f;
        float tableY = 10f;

        // Background
        GUI.Box(new Rect(tableX, tableY, tableW + pad * 2, tableH + pad * 2),
            GUIContent.none, _panelBgStyle);

        float cx = tableX + pad;
        float cy = tableY + pad;

        // Practice/TAS mode header
        if (segmentMode)
        {
            GUI.Label(new Rect(cx, cy, tableW, rowH), _tasMode ? "Tas Mode" : "Practice Mode", _headerStyle);
            cy += rowH;
        }

        // ── Split rows (static — no ticking inline) ──
        for (int i = 0; i < SplitNames.Length; i++)
        {
            float rowY = cy + i * rowH;
            float colX = cx;
            bool hasGold = _bestSegmentsSnapshot != null && i < _bestSegmentsSnapshot.Length
                && _bestSegmentsSnapshot[i] > 0;
            bool hasPb = _pbSnapshot != null && i < _pbSnapshot.Length
                && _pbSnapshot[i] > 0;
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
                if (segmentMode)
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
                            _displayTotalTimes[i] - _pbSnapshot[i],
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

                if (segmentMode)
                {
                    float goldSeg = hasGold ? _bestSegmentsSnapshot[i] : 0f;
                    if (isActive && hasGold && liveTime >= goldSeg - LiveDeltaLeadTime)
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
                    float pbTotal = hasPb ? _pbSnapshot[i] : 0f;
                    if (isActive && hasPb && liveTime >= pbTotal - LiveDeltaLeadTime)
                        DrawDelta(colX, rowY, deltaW, rowH, liveTime - pbTotal, false, true);
                    colX += deltaW;

                    float pbSeg = 0f;
                    if (hasPb)
                        pbSeg = i == 0 ? _pbSnapshot[0]
                            : _pbSnapshot[i] - _pbSnapshot[i - 1];

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
            float finalTime = segmentMode
                ? _displaySegTimes[SplitNames.Length - 1]
                : _displayTotalTimes[SplitNames.Length - 1];
            GUI.Label(new Rect(cx, timerY, tableW, timerH),
                FormatTime(finalTime), _timerStyle);
        }

        // ── Best Possible Time ──
        if (_hasGolds)
        {
            float bptY = timerY + timerH;
            string bptLabel = segmentMode ? "Sum of Best" : "Best Possible Time";
            GUI.Label(new Rect(cx, bptY, tableW, infoH), bptLabel, _splitNameStyle);
            GUI.Label(new Rect(cx, bptY, tableW, infoH), FormatTime(_bestPossibleTime), _splitTimeStyle);
        }
    }

    private void DrawDelta(float x, float y, float w, float h, float delta, bool isGold,
        bool live = false)
    {
        Color c = isGold ? ColorGold : (delta <= 0 ? ColorAhead : ColorBehind);
        float abs = Mathf.Abs(delta);
        string absFmt = abs.ToString("F2");
        string sign = delta >= 0 ? "+" : "\u2212";
        string secs = sign + Mathf.FloorToInt(abs).ToString();
        string decs = "." + absFmt.Substring(absFmt.IndexOf('.') + 1);

        _reusableContent.text = decs;
        float decW = _splitTimeStyle.CalcSize(_reusableContent).x;

        var orig = GUI.color;
        GUI.color = c;
        // Seconds right-aligned, leaving room for decimals on the right
        GUI.Label(new Rect(x, y, w - decW, h), secs, _splitTimeStyle);
        if (!live)
            GUI.Label(new Rect(x + w - decW, y, decW, h), decs, _splitTimeStyle);
        GUI.color = orig;
    }

    // ── Save / Load ───────────────────────────────────────

    private SavedState CaptureStateSnapshot()
    {
        if (_rb == null)
            return null;

        var state = new SavedState
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

        var animator = GetMothAnimator();
        if (animator != null)
        {
            var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
            state.animStateHash = stateInfo.fullPathHash;
            state.animNormalizedTime = stateInfo.normalizedTime;
        }

        for (int i = 0; i < _managedSplits.Count; i++)
            state.splitTimeValues[i] = (float)_splitTimeValueField.GetValue(_managedSplits[i]);

        return state;
    }

    private void SaveState()
    {
        SavedState state = CaptureStateSnapshot();
        if (state == null)
            return;

        _savedState = state;

        Logger.LogInfo(string.Format("TigerMoth saved state at ({0:F2}, {1:F2}) split={2}",
            _savedState.rbPosition.x, _savedState.rbPosition.y, _currentSplitIndex));
    }


    private void ApplyState()
    {
        if (_pendingRestore == null || _rb == null)
            return;
        var _savedState = _pendingRestore;
        _pendingRestore = null;

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
        ResetTrackingArrays();

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
            bool ticking = i == _savedState.currentSplitIndex;

            var newSplit = Object.Instantiate(splitPrefab, splitsInstance.transform).GetComponent<Split>();
            splitsList.Add(newSplit);
            _managedSplits.Add(newSplit);

            _splitLabelField.SetValue(newSplit, SplitNames[i]);
            _splitTimeValueField.SetValue(newSplit, timeValue);
            _splitTickingField.SetValue(newSplit, ticking);
            HideSplitVisuals(newSplit);

            if (i < _savedState.currentSplitIndex)
            {
                newSplit.Lock();
                _splitTimeValueField.SetValue(newSplit, timeValue);

                float seg = i == 0 ? timeValue : timeValue - _runTotals[i - 1];
                _displaySegTimes[i] = seg;
                _displayTotalTimes[i] = timeValue;
                _splitLocked[i] = true;
                float[] activeBestSegments = _tasMode ? _tasBestSegments : _bestSegments;
                _splitIsGold[i] = activeBestSegments != null && i < activeBestSegments.Length
                    && activeBestSegments[i] > 0 && seg <= activeBestSegments[i];
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
        var animator = GetMothAnimator();
        if (animator != null && _savedState.animStateHash != 0)
            animator.Play(_savedState.animStateHash, 0, _savedState.animNormalizedTime);

        // Restore camera position and zoom
        var cam = Singleton<AdvancedCamera>.Instance;
        cam.targetSize = _savedState.cameraTargetSize;
        Camera.main.orthographicSize = _savedState.cameraTargetSize;
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
