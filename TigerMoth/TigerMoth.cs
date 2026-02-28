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
    }

    private MothController _moth;
    private Rigidbody2D _rb;
    private SavedState _savedState;

    // Cached reflection
    private FieldInfo _rbField;
    private FieldInfo _jumpsField;
    private FieldInfo _maxJumpsField;
    private FieldInfo _chargingTimeField;
    private FieldInfo _facingDirectionField;
    private FieldInfo _jumpCanceledField;
    private FieldInfo _hitstopField;
    private FieldInfo _queuedJumpField;
    private FieldInfo _velocityBeforeHitstopField;

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

            Logger.LogInfo("TigerMoth found MothController");
        }

        if (Input.GetKeyDown(KeyCode.H))
            SaveState();

        if (Input.GetKeyDown(KeyCode.I))
            LoadState();
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
        };

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

        Logger.LogInfo(string.Format("TigerMoth loaded state at ({0:F2}, {1:F2})",
            _savedState.rbPosition.x, _savedState.rbPosition.y));
    }
}
