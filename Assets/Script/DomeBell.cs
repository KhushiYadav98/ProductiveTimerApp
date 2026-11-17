using Oculus.Interaction.Input;
using UnityEngine;

// On the mini town's bell: touching it with a hand confirms the boundary and raises the dome.
// Fingertips are checked directly against the bell's collider every frame (works regardless of
// rigidbody/trigger setup); hand trigger colliders entering the bell also count.
[RequireComponent(typeof(Collider))]
public class DomeBell : MonoBehaviour
{
    [SerializeField] private OnboardingManager onboarding;
    [Tooltip("Colliders that can ring the bell. If empty, any collider under the camera rig (the hands) counts.")]
    [SerializeField] private Collider[] handColliders;
    [Tooltip("Tracked hands whose fingertips can ring the bell (e.g. the rig's ComprehensiveInteractorsLeft/Right).")]
    [SerializeField] private Hand[] hands;
    [Tooltip("How close (meters) a fingertip must get to the bell's collider to ring it.")]
    [SerializeField] private float touchDistance = 0.015f;

    private static readonly HandJointId[] TouchJoints =
    {
        HandJointId.HandIndexTip, HandJointId.HandMiddleTip, HandJointId.HandThumbTip, HandJointId.HandRingTip
    };

    private Collider bellCollider;

    private void Awake()
    {
        bellCollider = GetComponent<Collider>();
        if (onboarding == null)
            onboarding = FindFirstObjectByType<OnboardingManager>();
    }

    private void Update()
    {
        if (hands == null)
            return;

        foreach (var hand in hands)
        {
            if (hand == null || !hand.IsTrackedDataValid)
                continue;
            foreach (var joint in TouchJoints)
            {
                if (!hand.GetJointPose(joint, out Pose pose))
                    continue;
                Vector3 closest = bellCollider.ClosestPoint(pose.position);
                if ((closest - pose.position).sqrMagnitude <= touchDistance * touchDistance)
                {
                    Ring($"{hand.name} {joint}");
                    return;
                }
            }
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (IsHand(other))
            Ring(other.name);
    }

    private void Ring(string by)
    {
        // Hands are all over the town while moving/scaling it, so only a deliberate touch counts.
        if (onboarding == null || onboarding.IsTownGrabbed || onboarding.IsConfirmed)
            return;

        Debug.Log($"[DomeBell] Rung by {by}");
        onboarding.ConfirmBoundary();
    }

    private bool IsHand(Collider other)
    {
        if (handColliders != null && handColliders.Length > 0)
            return System.Array.IndexOf(handColliders, other) >= 0;
        return other.GetComponentInParent<OVRCameraRig>() != null;
    }
}
