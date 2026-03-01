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
    private static readonly string[] SplitNames = { "Church", "Double Jump", "Tower", "End" };

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
    private PropertyInfo _tmpTextProperty;

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

    // TMP auto-sizing — disable on created splits so font size matches
    private PropertyInfo _tmpAutoSizingProperty;

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

    // GUI
    private GUIStyle _labelStyle;
    private GUIStyle _headerStyle;

    // ── Lifecycle ─────────────────────────────────────────

    void Awake()
    {
        _instance = this;
        new Harmony("com.speedrun.tigermoth").PatchAll();
        _checkpoints = CreateCheckpoints();
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
            case 0: // "Church" — moth enters The Ruined Church
                return AreaOverlap("The Ruined Church");
            case 1: // "Double Jump" — ExtraJump collected
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

        // Cache TMP properties from the game's split before we destroy it
        CacheTmpProperties(gameSplits[0]);

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

            _splitLabelField.SetValue(split, SplitNames[i] + ": ");
            ConfigureSplitDisplay(split);

            if (i == 0)
            {
                // First split starts ticking from 0
                _splitTickingField.SetValue(split, true);
                _splitTimeValueField.SetValue(split, 0f);
            }
            else
            {
                _splitTickingField.SetValue(split, false);
                _splitTimeValueField.SetValue(split, 0f);
                SetSplitText(split, SplitNames[i] + ": --");
            }
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

    private void ConfigureSplitDisplay(Split split)
    {
        // Disable TMP auto-sizing so font size is consistent across splits
        var timerText = _splitTimerTextField.GetValue(split);
        if (timerText != null && _tmpAutoSizingProperty != null)
            _tmpAutoSizingProperty.SetValue(timerText, false, null);

        // Widen the split's root RectTransform so longer labels fit
        var rt = split.GetComponent<RectTransform>();
        if (rt != null)
        {
            var size = rt.sizeDelta;
            size.x += 40f;
            rt.sizeDelta = size;
        }
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
            // 2: After Double Jump (entering Tower)
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
        GUI.Label(new Rect(x, y, 500, lineHeight), "H  Save    I  Load    1-4  Checkpoints", _labelStyle);
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

        // Restore splits — destroy the ones GameManager.Awake created, rebuild ours
        var splitsInstance = Singleton<SpeedrunSplits>.Instance;
        _splitsRunningField.SetValue(splitsInstance, true);
        var splitsList = (List<Split>)_splitsListField.GetValue(splitsInstance);
        var splitPrefab = (GameObject)_splitPrefabField.GetValue(splitsInstance);

        foreach (var split in splitsList)
            Object.Destroy(split.gameObject);
        splitsList.Clear();
        _managedSplits.Clear();

        for (int i = 0; i < SplitNames.Length; i++)
        {
            string label = SplitNames[i] + ": ";
            float timeValue = (_savedState.splitTimeValues != null && i < _savedState.splitTimeValues.Length)
                ? _savedState.splitTimeValues[i]
                : 0f;
            bool ticking = (i == _savedState.currentSplitIndex);

            var newSplit = Object.Instantiate(splitPrefab, splitsInstance.transform).GetComponent<Split>();
            splitsList.Add(newSplit);
            _managedSplits.Add(newSplit);

            _splitLabelField.SetValue(newSplit, label);
            _splitTimeValueField.SetValue(newSplit, timeValue);
            _splitTickingField.SetValue(newSplit, ticking);
            ConfigureSplitDisplay(newSplit);

            if (i < _savedState.currentSplitIndex)
            {
                newSplit.Lock();
                _splitTimeValueField.SetValue(newSplit, timeValue);
                SetSplitText(newSplit, label + timeValue.ToString("F"));
            }
            else if (i == _savedState.currentSplitIndex && ticking)
            {
                SetSplitText(newSplit, label + timeValue.ToString("F"));
            }
            else if (i > _savedState.currentSplitIndex)
            {
                SetSplitText(newSplit, label + "--");
            }
        }

        _currentSplitIndex = _savedState.currentSplitIndex;
        _runActive = _savedState.currentSplitIndex >= 0;

        // Restore ExtraJump powerup state (just the gameplay effect, not camera —
        // camera zoom is restored separately from savedState.cameraTargetSize)
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

        // Restore camera position and zoom (layer user zoom on top of saved zoom)
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
