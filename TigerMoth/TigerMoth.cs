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
        { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };

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

    // ── Fields ────────────────────────────────────────────
    private MothController _moth;
    private Rigidbody2D _rb;
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

    // Camera zoom
    private float _defaultZoom;
    private int _zoomSteps;

    // Personal best / gold tracking
    private float[] _pbTotalTimes;
    private float[] _bestSegments;
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
                    _zoomSteps = 0;
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
            ApplyState();
            return;
        }

        ManageSplits();

        if (Input.GetKeyDown(KeyCode.H))
            SaveState();

        if (Input.GetKeyDown(KeyCode.I))
        {
            if (_savedState == null)
                Logger.LogWarning("TigerMoth: no saved state to load");
            else
                ReloadAndRestore();
        }

        // Checkpoints: 1,2,3,4 → load hardcoded checkpoints
        for (int i = 0; i < 4; i++)
        {
            if (Input.GetKeyDown(CpKeys[i]))
            {
                _savedState = _checkpoints[i];
                ReloadAndRestore();
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

        float totalTime = _managedSplits[_currentSplitIndex].Lock();
        float segmentTime = _currentSplitIndex == 0
            ? totalTime
            : totalTime - _runTotals[_currentSplitIndex - 1];
        _runTotals[_currentSplitIndex] = totalTime;

        bool isGold = UpdateBestSegment(_currentSplitIndex, segmentTime);

        _displaySegTimes[_currentSplitIndex] = segmentTime;
        _displayTotalTimes[_currentSplitIndex] = totalTime;
        _splitLocked[_currentSplitIndex] = true;
        _splitIsGold[_currentSplitIndex] = isGold;

        SavePB();

        Logger.LogInfo(string.Format("TigerMoth: split '{0}' seg={1} total={2}",
            SplitNames[_currentSplitIndex], FormatTime(segmentTime), FormatTime(totalTime)));

        _currentSplitIndex++;

        if (_currentSplitIndex < _managedSplits.Count)
        {
            var next = _managedSplits[_currentSplitIndex];
            _splitTimeValueField.SetValue(next, totalTime);
            _splitTickingField.SetValue(next, true);
        }
    }

    private void OnEndRun()
    {
        int lastIdx = SplitNames.Length - 1;
        if (_currentSplitIndex != lastIdx)
            return;
        if (lastIdx >= _managedSplits.Count || _managedSplits[lastIdx] == null)
            return;

        // Lock() was already called by the game's EndRun
        float totalTime = (float)_splitTimeValueField.GetValue(_managedSplits[lastIdx]);
        float segmentTime = lastIdx == 0
            ? totalTime
            : totalTime - _runTotals[lastIdx - 1];
        _runTotals[lastIdx] = totalTime;

        bool isGold = UpdateBestSegment(lastIdx, segmentTime);

        _displaySegTimes[lastIdx] = segmentTime;
        _displayTotalTimes[lastIdx] = totalTime;
        _splitLocked[lastIdx] = true;
        _splitIsGold[lastIdx] = isGold;

        // Check for PB — all splits must have been hit in this run
        bool complete = true;
        for (int i = 0; i < SplitNames.Length; i++)
        {
            if (_runTotals[i] <= 0) { complete = false; break; }
        }
        if (complete && (_pbTotalTimes == null || totalTime < _pbTotalTimes[lastIdx]))
        {
            _pbTotalTimes = (float[])_runTotals.Clone();
            Logger.LogInfo("TigerMoth: New PB! " + FormatTime(totalTime));
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

    // ── Camera zoom ───────────────────────────────────────

    private void ApplyZoom()
    {
        float size = _defaultZoom + _zoomSteps * 2f;
        Singleton<AdvancedCamera>.Instance.targetSize = size;
        Camera.main.orthographicSize = size;
    }

    // ── Hardcoded checkpoints ─────────────────────────────

    private static SavedState[] CreateCheckpoints()
    {
        return new[]
        {
            // 1: Start area (before Church)
            new SavedState
            {
                rbPosition = new Vector2(-22.591f, 7.028f),
                jumps = 1,
                facingDirection = 1,
                currentSplitIndex = 0,
                cameraTargetSize = 5.2f,
                cameraPosition = new Vector3(-21.63f, 10.325f, -10f),
                spiderPosition = new Vector3(-26.567f, 67.769f, -0.079f),
                animStateHash = 618468143,
            },
            // 2: After Gift (entering Tower)
            new SavedState
            {
                rbPosition = new Vector2(-0.437f, 34.58f),
                jumps = 2,
                facingDirection = -1,
                currentSplitIndex = 2,
                extraJumpUsed = true,
                cameraTargetSize = 6.2f,
                cameraPosition = new Vector3(-1.426f, 37.846f, -10f),
                spiderPosition = new Vector3(-26.424f, 34.553f, 0f),
                spiderCanFollow = true,
                animStateHash = 618468143,
            },
            // 3: Mid-Tower
            new SavedState
            {
                rbPosition = new Vector2(-32.829f, 71.38f),
                jumps = 2,
                facingDirection = -1,
                currentSplitIndex = 2,
                extraJumpUsed = true,
                cameraTargetSize = 6.2f,
                cameraPosition = new Vector3(-33.819f, 74.629f, -10f),
                spiderPosition = new Vector3(-25.026f, 46.041f, 0f),
                spiderCanFollow = true,
                animStateHash = 618468143,
            },
            // 4: Near End
            new SavedState
            {
                rbPosition = new Vector2(-21.371f, 135.431f),
                jumps = 2,
                facingDirection = -1,
                currentSplitIndex = 3,
                extraJumpUsed = true,
                cameraTargetSize = 7.819f,
                cameraPosition = new Vector3(-22.329f, 138.661f, -10f),
                spiderPosition = new Vector3(-23.915f, 13.268f, 0f),
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

            _panelBgTex = new Texture2D(1, 1);
            _panelBgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
            _panelBgTex.Apply();
            _panelBgStyle = new GUIStyle();
            _panelBgStyle.normal.background = _panelBgTex;

            _splitActiveTex = new Texture2D(1, 1);
            _splitActiveTex.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.08f));
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
            new[] { "1-4", "Checkpoints" },
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

        // Zoom info
        string zoomStr = _zoomSteps == 0 ? "0" : (_zoomSteps > 0 ? "+" + _zoomSteps : _zoomSteps.ToString());
        GUI.Label(new Rect(cx, cy, tableW, rowH), "Zoom: " + zoomStr, _infoStyle);
    }

    private static readonly Color ColorAhead = new Color(0.27f, 1f, 0.27f);
    private static readonly Color ColorBehind = new Color(1f, 0.27f, 0.27f);
    private static readonly Color ColorGold = new Color(1f, 0.84f, 0f);
    private static readonly Color ColorGray = new Color(0.5f, 0.5f, 0.5f);

    private void DrawSplitTable()
    {
        const float nameW = 130f;
        const float deltaW = 140f;
        const float segW = 120f;
        const float totalW = 120f;
        const float tableW = nameW + deltaW + segW + totalW;
        const float rowH = 46f;
        const float pad = 6f;
        float tableH = SplitNames.Length * rowH;

        float tableX = Screen.width - tableW - pad * 2 - 10f;
        float tableY = 10f;

        // Background
        GUI.Box(new Rect(tableX, tableY, tableW + pad * 2, tableH + pad * 2),
            GUIContent.none, _panelBgStyle);

        float cx = tableX + pad;
        float cy = tableY + pad;

        for (int i = 0; i < SplitNames.Length; i++)
        {
            float rowY = cy + i * rowH;
            float colX = cx;
            bool hasPb = _pbTotalTimes != null && i < _pbTotalTimes.Length
                && _pbTotalTimes[i] > 0;
            bool locked = _splitLocked != null && i < _splitLocked.Length
                && _splitLocked[i];

            // Highlight active row
            if (i == _currentSplitIndex && !locked)
                GUI.Box(new Rect(cx, rowY, tableW, rowH), GUIContent.none, _splitActiveStyle);

            // Name
            GUI.Label(new Rect(colX, rowY, nameW, rowH), SplitNames[i], _splitNameStyle);
            colX += nameW;

            if (locked)
            {
                // Delta
                if (hasPb)
                {
                    float delta = _displayTotalTimes[i] - _pbTotalTimes[i];
                    Color c = (_splitIsGold != null && _splitIsGold[i])
                        ? ColorGold : (delta <= 0 ? ColorAhead : ColorBehind);
                    string sign = delta >= 0 ? "+" : "\u2212";

                    var orig = GUI.color;
                    GUI.color = c;
                    GUI.Label(new Rect(colX, rowY, deltaW, rowH),
                        sign + FormatTime(Mathf.Abs(delta)), _splitTimeStyle);
                    GUI.color = orig;
                }
                colX += deltaW;

                // Segment
                GUI.Label(new Rect(colX, rowY, segW, rowH),
                    FormatTime(_displaySegTimes[i]), _splitTimeStyle);
                colX += segW;

                // Total
                GUI.Label(new Rect(colX, rowY, totalW, rowH),
                    FormatTime(_displayTotalTimes[i]), _splitTimeStyle);
            }
            else if (i == _currentSplitIndex && i < _managedSplits.Count
                && _managedSplits[i] != null)
            {
                // Ticking — live delta + running total
                float currentTime = (float)_splitTimeValueField.GetValue(_managedSplits[i]);

                if (hasPb)
                {
                    float delta = currentTime - _pbTotalTimes[i];
                    Color c = delta <= 0 ? ColorAhead : ColorBehind;
                    string sign = delta >= 0 ? "+" : "\u2212";
                    var orig = GUI.color;
                    GUI.color = c;
                    GUI.Label(new Rect(colX, rowY, deltaW, rowH),
                        sign + FormatTime(Mathf.Abs(delta)), _splitTimeStyle);
                    GUI.color = orig;
                }
                colX += deltaW;
                colX += segW;

                GUI.Label(new Rect(colX, rowY, totalW, rowH),
                    FormatTime(currentTime), _splitTimeStyle);
            }
            else
            {
                // Future split — show PB hint or "--"
                colX += deltaW;
                colX += segW;

                if (hasPb)
                {
                    var orig = GUI.color;
                    GUI.color = ColorGray;
                    GUI.Label(new Rect(colX, rowY, totalW, rowH),
                        FormatTime(_pbTotalTimes[i]), _splitTimeStyle);
                    GUI.color = orig;
                }
                else
                {
                    GUI.Label(new Rect(colX, rowY, totalW, rowH), "--", _splitTimeStyle);
                }
            }
        }
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
            bool ticking = (i == _savedState.currentSplitIndex);

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
