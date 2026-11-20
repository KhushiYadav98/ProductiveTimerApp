using Oculus.Interaction;
using UnityEngine;

// Grab transformer for the sun/moon: instead of moving freely with the hand, the body slides along its fixed arc
// (DayNightSwitcher places it at the point of the arc closest to the grabbing hand).
public class CelestialArcTransformer : MonoBehaviour, ITransformer
{
    [SerializeField] private DayNightSwitcher switcher;
    [SerializeField] private DayNightSwitcher.Body body;

    private IGrabbable grabbable;

    public void Initialize(IGrabbable target)
    {
        grabbable = target;
        if (switcher == null)
            switcher = FindFirstObjectByType<DayNightSwitcher>();
    }

    public void BeginTransform() => switcher?.BeginDrag(body);

    public void UpdateTransform()
    {
        if (grabbable == null || grabbable.GrabPoints.Count == 0)
            return;
        switcher?.Drag(body, grabbable.GrabPoints[0].position);
    }

    public void EndTransform() => switcher?.EndDrag(body);
}
