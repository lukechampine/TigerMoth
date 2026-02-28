using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("com.speedrun.tigermoth", "TigerMoth", "1.2.0")]
public class TigerMothPlugin : BaseUnityPlugin
{
    // ── Split definitions (hardcoded order) ───────────────
    private static readonly string[] SplitNames = { "Double Jump", "Foot", "End" };

    // ── Harmony: block game's NewSplit after we take over ─
    [HarmonyPatch(typeof(SpeedrunSplits), "NewSplit")]
    private class PatchNewSplit
    {
        static bool Prefix(bool first)
        {
            var plugin = FindObjectOfType<TigerMothPlugin>();
            return plugin == null || !plugin._runActive || first;
        }
    }

    // ── Saved state ───────────────────────────────────────
    private class SavedState
    {
        public Vector2 rbPosition;
        public Vector2 rbVelocity;
        public float rbAngularVelocity;
        public float transformZ;
        public Vector3 eulerAngles;
        public int jumps;
        public int maxJumps;
        public float chargingTime;
        public int facingDirection;
        public bool jumpCanceled;
        public bool hitstopActive;
        public bool queuedJump;
        public Vector2 velocityBeforeHitstop;

        public bool splitsRunning;
        public bool runActive;
        public int currentSplitIndex;
        public List<SplitState> splits;

        public bool extraJumpUsed;

        public Vector3 cameraPosition;

        public int animStateHash;
        public float animNormalizedTime;
    }

    private class SplitState
    {
        public float timeValue;
        public bool ticking;
        public string label;
    }

    // ── Fields ────────────────────────────────────────────
    private MothController _moth;
    private Rigidbody2D _rb;
    private SavedState _savedState;
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
    private PropertyInfo _tmpTextProperty;

    // Reflection — ExtraJump
    private FieldInfo _extraJumpUsedField;
    private FieldInfo _extraJumpFunctionalChildField;

    // Reflection — Animator
    private FieldInfo _animatorField;

    // Reflection — ScreenTransition, AdvancedCamera, SaveSystem (cached for load)
    private FieldInfo _maskField;
    private FieldInfo _pureTransformField;
    private MethodInfo _saveSystemClearMethod;
    private object _saveSystemGameBucket;

    // TMP auto-sizing — disable on created splits so font size matches
    private PropertyInfo _tmpAutoSizingProperty;

    // Cached scene objects
    private ExtraJump _extraJump;

    // Split management
    private List<Split> _managedSplits = new List<Split>();
    private int _currentSplitIndex = -1;
    private bool _runActive;

    // Area trigger — "The Foot" collider for position-based detection
    private Collider2D _footCollider;

    // Camera zoom
    private float _defaultZoom;
    private int _zoomSteps;

    // GUI
    private GUIStyle _labelStyle;
    private GUIStyle _headerStyle;

    // ── Lifecycle ─────────────────────────────────────────

    void Awake()
    {
        new Harmony("com.speedrun.tigermoth").PatchAll();
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
                _defaultZoom = Camera.main.orthographicSize;

            _splitsRunningField = typeof(SpeedrunSplits).GetField("running", flags);
            _splitsListField = typeof(SpeedrunSplits).GetField("splits", flags);
            _splitPrefabField = typeof(SpeedrunSplits).GetField("splitPrefab", flags | BindingFlags.Public);
            _splitTickingField = typeof(Split).GetField("ticking", flags);
            _splitTimeValueField = typeof(Split).GetField("timeValue", flags);
            _splitLabelField = typeof(Split).GetField("label", flags);
            _splitTimerTextField = typeof(Split).GetField("timerText", flags);

            _extraJumpUsedField = typeof(ExtraJump).GetField("used", flags);
            _extraJumpFunctionalChildField = typeof(ExtraJump).GetField("functionalChild", flags);
            _extraJump = FindObjectOfType<ExtraJump>();

            _maskField = typeof(ScreenTransition).GetField("mask", flags | BindingFlags.Public);
            _pureTransformField = typeof(AdvancedCamera).GetField("pureTransform", flags);

            if (_saveSystemClearMethod == null)
            {
                var bucketType = typeof(SaveSystem).GetNestedType("BucketName");
                _saveSystemGameBucket = System.Enum.Parse(bucketType, "Game");
                _saveSystemClearMethod = typeof(SaveSystem).GetMethod("Clear");
            }

            // Find "The Foot" collider for area-based split trigger
            foreach (var lta in FindObjectsOfType<LocationTitleArea>())
            {
                if (lta.gameObject.name == "The Foot")
                {
                    _footCollider = lta.GetComponentInChildren<Collider2D>();
                    if (_footCollider != null)
                        Logger.LogInfo("TigerMoth: found Foot trigger collider");
                    else
                        Logger.LogWarning("TigerMoth: 'The Foot' has no Collider2D");
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
        // Mirror what the game's Reset (R key) does: cancel all tweens,
        // clear save data, reload the scene. Our plugin survives the reload
        // (BepInEx uses DontDestroyOnLoad) and applies saved state on the
        // fresh scene, avoiding all the manual cleanup of cutscene effects,
        // screen transitions, animation states, orbs, etc.
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
            case 0: // "Double Jump" — complete when ExtraJump collected
                return _extraJump != null && (bool)_extraJumpUsedField.GetValue(_extraJump);
            case 1: // "Foot" — complete when moth enters The Foot area
                return _footCollider != null && _footCollider.OverlapPoint(_rb.position);
            default: // "End" and beyond — triggered by game's EndRun
                return false;
        }
    }

    private void SetupManagedRun(List<Split> gameSplits)
    {
        _runActive = true;
        _currentSplitIndex = 0;
        _managedSplits.Clear();

        // First split already created by the game (Double Jump, ticking)
        _managedSplits.Add(gameSplits[0]);

        // Cache TMP auto-sizing properties from the first split's timerText
        // so we can disable auto-sizing on created splits (fixes font size mismatch)
        CacheTmpProperties(gameSplits[0]);

        // Create remaining splits as inactive with "--" display
        var prefab = (GameObject)_splitPrefabField.GetValue(Singleton<SpeedrunSplits>.Instance);
        for (int i = 1; i < SplitNames.Length; i++)
        {
            var split = Object.Instantiate(prefab, Singleton<SpeedrunSplits>.Instance.transform)
                .GetComponent<Split>();
            gameSplits.Add(split);
            _managedSplits.Add(split);

            _splitLabelField.SetValue(split, SplitNames[i] + ": ");
            _splitTickingField.SetValue(split, false);
            _splitTimeValueField.SetValue(split, 0f);
            DisableTmpAutoSizing(split);
            SetSplitText(split, SplitNames[i] + ": --");
        }

        Logger.LogInfo("TigerMoth: managed run started (" + SplitNames.Length + " splits)");
    }

    private void AdvanceSplit()
    {
        if (_currentSplitIndex >= _managedSplits.Count)
            return;

        float prevTime = _managedSplits[_currentSplitIndex].Lock();
        Logger.LogInfo(string.Format("TigerMoth: split '{0}' locked at {1:F2}",
            SplitNames[_currentSplitIndex], prevTime));

        _currentSplitIndex++;

        if (_currentSplitIndex < _managedSplits.Count)
        {
            var next = _managedSplits[_currentSplitIndex];
            _splitTimeValueField.SetValue(next, prevTime);
            _splitTickingField.SetValue(next, true);
        }
    }

    private void CacheTmpProperties(Split referenceSplit)
    {
        var timerText = _splitTimerTextField.GetValue(referenceSplit);
        if (timerText == null)
            return;
        var type = timerText.GetType();
        if (_tmpTextProperty == null)
            _tmpTextProperty = type.GetProperty("text");
        if (_tmpAutoSizingProperty == null)
            _tmpAutoSizingProperty = type.GetProperty("enableAutoSizing");
    }

    private void DisableTmpAutoSizing(Split split)
    {
        var timerText = _splitTimerTextField.GetValue(split);
        if (timerText == null || _tmpAutoSizingProperty == null)
            return;
        _tmpAutoSizingProperty.SetValue(timerText, false, null);
    }

    private void SetSplitText(Split split, string text)
    {
        var timerText = _splitTimerTextField.GetValue(split);
        if (timerText == null || _tmpTextProperty == null)
            return;
        _tmpTextProperty.SetValue(timerText, text, null);
    }

    // ── Camera zoom ───────────────────────────────────────

    private void ApplyZoom()
    {
        float size = _defaultZoom + _zoomSteps * 2f;
        Singleton<AdvancedCamera>.Instance.targetSize = size;
        Camera.main.orthographicSize = size;
    }

    // ── GUI ───────────────────────────────────────────────

    void OnGUI()
    {
        if (_moth == null)
            return;

        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 24;
            _labelStyle.normal.textColor = new Color(1f, 1f, 1f, 0.7f);

            _headerStyle = new GUIStyle(GUI.skin.label);
            _headerStyle.fontSize = 24;
            _headerStyle.fontStyle = FontStyle.Bold;
            _headerStyle.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
        }

        float x = 10f;
        float y = 10f;
        float lineHeight = 32f;

        GUI.Label(new Rect(x, y, 500, lineHeight), "TigerMoth", _headerStyle);
        y += lineHeight;
        GUI.Label(new Rect(x, y, 500, lineHeight), "H  Save state", _labelStyle);
        y += lineHeight;
        GUI.Label(new Rect(x, y, 500, lineHeight), "I  Load state", _labelStyle);
        y += lineHeight;
        GUI.Label(new Rect(x, y, 500, lineHeight), "[  Zoom in    ]  Zoom out", _labelStyle);
        y += lineHeight;

        string zoomStr = _zoomSteps == 0 ? "0" : (_zoomSteps > 0 ? "+" + _zoomSteps : _zoomSteps.ToString());
        GUI.Label(new Rect(x, y, 500, lineHeight),
            string.Format("Zoom: {0}", zoomStr), _labelStyle);
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
            rbAngularVelocity = _rb.angularVelocity,
            transformZ = _moth.transform.position.z,
            eulerAngles = _moth.transform.eulerAngles,
            jumps = (int)_jumpsField.GetValue(_moth),
            maxJumps = (int)_maxJumpsField.GetValue(_moth),
            chargingTime = (float)_chargingTimeField.GetValue(_moth),
            facingDirection = (int)_facingDirectionField.GetValue(_moth),
            jumpCanceled = (bool)_jumpCanceledField.GetValue(_moth),
            hitstopActive = (bool)_hitstopField.GetValue(_moth),
            queuedJump = (bool)_queuedJumpField.GetValue(_moth),
            velocityBeforeHitstop = (Vector2)_velocityBeforeHitstopField.GetValue(_moth),
            splitsRunning = (bool)_splitsRunningField.GetValue(Singleton<SpeedrunSplits>.Instance),
            runActive = _runActive,
            currentSplitIndex = _currentSplitIndex,
            splits = new List<SplitState>(),
            extraJumpUsed = _extraJump != null && (bool)_extraJumpUsedField.GetValue(_extraJump),
            cameraPosition = Singleton<AdvancedCamera>.Instance.transform.position,
        };

        // Animator state
        var animator = (Animator)_animatorField.GetValue(_moth);
        if (animator != null)
        {
            var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
            _savedState.animStateHash = stateInfo.fullPathHash;
            _savedState.animNormalizedTime = stateInfo.normalizedTime;
        }

        foreach (var split in _managedSplits)
        {
            _savedState.splits.Add(new SplitState
            {
                timeValue = (float)_splitTimeValueField.GetValue(split),
                ticking = (bool)_splitTickingField.GetValue(split),
                label = (string)_splitLabelField.GetValue(split),
            });
        }

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
        _rb.angularVelocity = _savedState.rbAngularVelocity;

        // Transform (z position and rotation)
        _moth.transform.position = new Vector3(
            _savedState.rbPosition.x,
            _savedState.rbPosition.y,
            _savedState.transformZ);
        _moth.transform.eulerAngles = _savedState.eulerAngles;

        // MothController state
        _jumpsField.SetValue(_moth, _savedState.jumps);
        _maxJumpsField.SetValue(_moth, _savedState.maxJumps);
        _chargingTimeField.SetValue(_moth, _savedState.chargingTime);
        _facingDirectionField.SetValue(_moth, _savedState.facingDirection);
        _jumpCanceledField.SetValue(_moth, _savedState.jumpCanceled);
        _hitstopField.SetValue(_moth, _savedState.hitstopActive);
        _queuedJumpField.SetValue(_moth, _savedState.queuedJump);
        _velocityBeforeHitstopField.SetValue(_moth, _savedState.velocityBeforeHitstop);

        // Restore splits — destroy the ones GameManager.Awake created, rebuild ours
        var splitsInstance = Singleton<SpeedrunSplits>.Instance;
        _splitsRunningField.SetValue(splitsInstance, _savedState.splitsRunning);
        var splitsList = (List<Split>)_splitsListField.GetValue(splitsInstance);
        var splitPrefab = (GameObject)_splitPrefabField.GetValue(splitsInstance);

        foreach (var split in splitsList)
            Object.Destroy(split.gameObject);
        splitsList.Clear();
        _managedSplits.Clear();

        for (int i = 0; i < _savedState.splits.Count; i++)
        {
            var saved = _savedState.splits[i];
            var newSplit = Object.Instantiate(splitPrefab, splitsInstance.transform).GetComponent<Split>();
            splitsList.Add(newSplit);
            _managedSplits.Add(newSplit);

            _splitLabelField.SetValue(newSplit, saved.label);
            _splitTimeValueField.SetValue(newSplit, saved.timeValue);
            _splitTickingField.SetValue(newSplit, saved.ticking);
            DisableTmpAutoSizing(newSplit);

            if (i < _savedState.currentSplitIndex)
            {
                // Completed split — apply lock visuals (green text, black bg)
                newSplit.Lock();
                _splitTimeValueField.SetValue(newSplit, saved.timeValue);
                SetSplitText(newSplit, saved.label + saved.timeValue.ToString("F"));
            }
            else if (i == _savedState.currentSplitIndex && saved.ticking)
            {
                // Active split — set initial text, Update() takes over next frame
                SetSplitText(newSplit, saved.label + saved.timeValue.ToString("F"));
            }
            else if (i > _savedState.currentSplitIndex)
            {
                // Future split — show "--"
                SetSplitText(newSplit, saved.label + "--");
            }
        }

        _currentSplitIndex = _savedState.currentSplitIndex;
        _runActive = _savedState.runActive;

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

        // Restore animator state
        var animator = (Animator)_animatorField.GetValue(_moth);
        if (animator != null && _savedState.animStateHash != 0)
            animator.Play(_savedState.animStateHash, 0, _savedState.animNormalizedTime);

        // Restore camera position and zoom
        var cam = Singleton<AdvancedCamera>.Instance;
        cam.transform.position = _savedState.cameraPosition;
        if (_pureTransformField != null)
        {
            var pure = (Transform)_pureTransformField.GetValue(cam);
            if (pure != null)
                pure.position = _savedState.cameraPosition;
        }
        ApplyZoom();

        Logger.LogInfo(string.Format("TigerMoth: restored state at ({0:F2}, {1:F2}) split={2}",
            _savedState.rbPosition.x, _savedState.rbPosition.y, _currentSplitIndex));
    }
}
