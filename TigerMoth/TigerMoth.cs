using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using UnityEngine;

[BepInPlugin("com.speedrun.tigermoth", "TigerMoth", "1.0.0")]
public class TigerMothPlugin : BaseUnityPlugin
{
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

        // Speedrun timer
        public bool splitsRunning;
        public List<SplitState> splits;

        // ExtraJump powerup
        public bool extraJumpUsed;
    }

    private class SplitState
    {
        public float timeValue;
        public bool ticking;
        public string label;
    }

    private MothController _moth;
    private Rigidbody2D _rb;
    private SavedState _savedState;

    // Cached reflection — MothController
    private FieldInfo _rbField;
    private FieldInfo _jumpsField;
    private FieldInfo _maxJumpsField;
    private FieldInfo _chargingTimeField;
    private FieldInfo _facingDirectionField;
    private FieldInfo _jumpCanceledField;
    private FieldInfo _hitstopField;
    private FieldInfo _queuedJumpField;
    private FieldInfo _velocityBeforeHitstopField;

    // Cached reflection — SpeedrunSplits / Split
    private FieldInfo _splitsRunningField;
    private FieldInfo _splitsListField;
    private FieldInfo _splitPrefabField;
    private FieldInfo _splitTickingField;
    private FieldInfo _splitTimeValueField;
    private FieldInfo _splitLabelField;

    // Cached reflection — ExtraJump
    private ExtraJump _extraJump;
    private FieldInfo _extraJumpUsedField;
    private FieldInfo _extraJumpFunctionalChildField;
    private GameObject _extraJumpChildBackup;

    void Awake()
    {
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
            _rbField = typeof(MothController).GetField("rb", flags);
            _jumpsField = typeof(MothController).GetField("jumps", flags);
            _maxJumpsField = typeof(MothController).GetField("maxJumps", flags);
            _chargingTimeField = typeof(MothController).GetField("chargingTime", flags);
            _facingDirectionField = typeof(MothController).GetField("facingDirection", flags);
            _jumpCanceledField = typeof(MothController).GetField("jumpCanceled", flags);
            _hitstopField = typeof(MothController).GetField("hitstopActive", flags);
            _queuedJumpField = typeof(MothController).GetField("queuedJump", flags);
            _velocityBeforeHitstopField = typeof(MothController).GetField("velocityBeforeHitstop", flags);

            if (_rbField != null)
                _rb = (Rigidbody2D)_rbField.GetValue(_moth);

            // SpeedrunSplits / Split reflection
            _splitsRunningField = typeof(SpeedrunSplits).GetField("running", flags);
            _splitsListField = typeof(SpeedrunSplits).GetField("splits", flags);
            _splitPrefabField = typeof(SpeedrunSplits).GetField("splitPrefab", flags | BindingFlags.Public);
            _splitTickingField = typeof(Split).GetField("ticking", flags);
            _splitTimeValueField = typeof(Split).GetField("timeValue", flags);
            _splitLabelField = typeof(Split).GetField("label", flags);

            // ExtraJump — cache reference and back up the collectible child
            _extraJump = FindObjectOfType<ExtraJump>();
            if (_extraJump != null)
            {
                _extraJumpUsedField = typeof(ExtraJump).GetField("used", flags);
                _extraJumpFunctionalChildField = typeof(ExtraJump).GetField("functionalChild", flags);
                var child = (GameObject)_extraJumpFunctionalChildField.GetValue(_extraJump);
                if (child != null)
                {
                    _extraJumpChildBackup = Object.Instantiate(child, _extraJump.transform);
                    _extraJumpChildBackup.SetActive(false);
                }
            }

            Logger.LogInfo("TigerMoth found MothController");
        }

        if (Input.GetKeyDown(KeyCode.H))
            SaveState();

        if (Input.GetKeyDown(KeyCode.I))
            LoadState();

        if (Input.GetKeyDown(KeyCode.LeftBracket))
        {
            Singleton<AdvancedCamera>.Instance.targetSize -= 2f;
            Camera.main.orthographicSize = Singleton<AdvancedCamera>.Instance.targetSize;
        }

        if (Input.GetKeyDown(KeyCode.RightBracket))
        {
            Singleton<AdvancedCamera>.Instance.targetSize += 2f;
            Camera.main.orthographicSize = Singleton<AdvancedCamera>.Instance.targetSize;
        }
    }

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
            splits = new List<SplitState>(),
            extraJumpUsed = _extraJump != null && (bool)_extraJumpUsedField.GetValue(_extraJump),
        };

        var splitsList = (List<Split>)_splitsListField.GetValue(Singleton<SpeedrunSplits>.Instance);
        foreach (var split in splitsList)
        {
            _savedState.splits.Add(new SplitState
            {
                timeValue = (float)_splitTimeValueField.GetValue(split),
                ticking = (bool)_splitTickingField.GetValue(split),
                label = (string)_splitLabelField.GetValue(split),
            });
        }

        Logger.LogInfo(string.Format("TigerMoth saved state at ({0:F2}, {1:F2})",
            _savedState.rbPosition.x, _savedState.rbPosition.y));
    }

    private void LoadState()
    {
        if (_savedState == null)
        {
            Logger.LogWarning("TigerMoth: no saved state to load");
            return;
        }

        if (_rb == null)
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

        // Restore speedrun splits — clear and rebuild
        var splitsInstance = Singleton<SpeedrunSplits>.Instance;
        _splitsRunningField.SetValue(splitsInstance, _savedState.splitsRunning);
        var splitsList = (List<Split>)_splitsListField.GetValue(splitsInstance);
        var splitPrefab = (GameObject)_splitPrefabField.GetValue(splitsInstance);

        // Destroy all current split GameObjects
        foreach (var split in splitsList)
            Object.Destroy(split.gameObject);
        splitsList.Clear();

        // Recreate from saved state
        foreach (var saved in _savedState.splits)
        {
            var newSplit = Object.Instantiate(splitPrefab, splitsInstance.transform).GetComponent<Split>();
            splitsList.Add(newSplit);
            _splitTimeValueField.SetValue(newSplit, saved.timeValue);
            _splitTickingField.SetValue(newSplit, saved.ticking);
            _splitLabelField.SetValue(newSplit, saved.label);
        }

        // Restore ExtraJump powerup
        if (_extraJump != null && _extraJumpChildBackup != null)
        {
            bool currentlyUsed = (bool)_extraJumpUsedField.GetValue(_extraJump);
            if (_savedState.extraJumpUsed != currentlyUsed)
            {
                if (!_savedState.extraJumpUsed)
                {
                    // Powerup was not collected at save time — restore the collectible
                    _extraJumpUsedField.SetValue(_extraJump, false);
                    var currentChild = (GameObject)_extraJumpFunctionalChildField.GetValue(_extraJump);
                    if (currentChild != null)
                        Object.Destroy(currentChild);
                    var restored = Object.Instantiate(_extraJumpChildBackup, _extraJump.transform);
                    restored.SetActive(true);
                    _extraJumpFunctionalChildField.SetValue(_extraJump, restored);
                }
                else
                {
                    // Powerup was collected at save time — destroy the collectible
                    _extraJumpUsedField.SetValue(_extraJump, true);
                    var currentChild = (GameObject)_extraJumpFunctionalChildField.GetValue(_extraJump);
                    if (currentChild != null)
                        Object.Destroy(currentChild);
                }
            }
        }

        Logger.LogInfo(string.Format("TigerMoth loaded state at ({0:F2}, {1:F2})",
            _savedState.rbPosition.x, _savedState.rbPosition.y));
    }
}
