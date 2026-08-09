using UnityEngine;

// Rolls a wheel to match how far it actually travels. Drop it on each wheel mesh — it is deliberately
// self-contained: it reads the vehicle's velocity and writes only its OWN localRotation, so it cannot
// affect steering, suspension or drive forces. Delete the component and the wheel simply stops turning.
//
// It also avoids assuming anything about how the model was authored, which is the usual source of
// "the wheel spins around the wrong axis":
//   * the axle is detected from the mesh bounds — on a wheel, the SMALLEST extent is always the axle,
//     whatever axis the artist built it on;
//   * the roll direction is derived from that axle rather than the vehicle's forward, so a model whose
//     nose points down +X works the same as one pointing down +Z;
//   * mirrored left/right wheels work without special-casing, because each derives its own direction.
[DisallowMultipleComponent]
public class WheelSpin : MonoBehaviour
{
    [Header("Axle")]
    [Tooltip("Detect the axle from the mesh bounds (smallest extent). Turn off to set it by hand.")]
    public bool autoDetectAxle = true;
    [Tooltip("Axle direction in this wheel's local space. Only used when Auto Detect Axle is off.")]
    public Vector3 axle = Vector3.right;

    [Header("Radius")]
    [Tooltip("Measure the radius from the mesh bounds (largest extent, scaled). Turn off to set it " +
             "by hand. The radius sets how fast the wheel turns for a given speed.")]
    public bool autoRadius = true;
    [Tooltip("Wheel radius in meters. Only used when Auto Radius is off.")]
    public float radius = 0.5f;

    [Header("Options")]
    [Tooltip("Flip the spin direction. Needed when the detected axle points the opposite way — the " +
             "one thing that can't be inferred from a symmetric mesh.")]
    public bool invert = false;
    [Tooltip("Vehicle rigidbody. Left empty it searches the parents; with no rigidbody at all it " +
             "falls back to tracking this transform's own movement, so kinematic rigs still work.")]
    public Rigidbody body;

    private Rigidbody _rb;
    private Vector3 _axleLocal = Vector3.right;
    private float _radius = 0.5f;
    private Vector3 _lastPos;

    private void OnEnable()
    {
        Resolve();
        _lastPos = transform.position;
    }

    private void OnValidate()
    {
        if (Application.isPlaying) Resolve();
    }

    private void Resolve()
    {
        _rb = body != null ? body : GetComponentInParent<Rigidbody>();

        _axleLocal = axle.sqrMagnitude > 1e-6f ? axle.normalized : Vector3.right;
        _radius = Mathf.Max(0.01f, radius);

        var mf = GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;
        Vector3 e = mf.sharedMesh.bounds.extents;

        if (autoDetectAxle)
        {
            // A wheel is round across two axes and thin along the axle, so the smallest extent is it.
            if (e.x <= e.y && e.x <= e.z) _axleLocal = Vector3.right;
            else if (e.y <= e.z) _axleLocal = Vector3.up;
            else _axleLocal = Vector3.forward;
        }
        if (autoRadius)
        {
            Vector3 s = transform.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            _radius = Mathf.Max(0.01f, Mathf.Max(e.x, Mathf.Max(e.y, e.z)) * maxScale);
        }
    }

    // Visual only, so this runs per FRAME rather than per physics step — a wheel updated at 50 Hz
    // against a 120 Hz render visibly strobes.
    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0.0f) return;

        Vector3 velocity = _rb != null
            ? _rb.GetPointVelocity(transform.position)
            : (transform.position - _lastPos) / dt;
        _lastPos = transform.position;

        // Rolling without slipping: spinning at w about axle A moves the contact point at -w*r*(A x up),
        // so the hub travels along (A x up). Project the measured velocity onto that direction to get
        // the speed this wheel is actually rolling at — no reference to the vehicle's forward needed.
        Vector3 axleWorld = transform.TransformDirection(_axleLocal);
        Vector3 rollDir = Vector3.Cross(axleWorld, Vector3.up);
        if (rollDir.sqrMagnitude < 1e-6f) return;      // axle vertical: nothing sensible to roll along
        rollDir.Normalize();

        float speed = Vector3.Dot(velocity, rollDir);
        float degrees = speed / _radius * Mathf.Rad2Deg * dt;
        transform.Rotate(_axleLocal, invert ? -degrees : degrees, Space.Self);
    }
}
