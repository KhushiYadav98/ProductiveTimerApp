using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Minutes picker for the "Set timer" panel: + / - change the time in steps between min and max, and each button is
// disabled at its limit. The time changes with a quick flip-clock flip.
public class TimerPicker : MonoBehaviour
{
    [SerializeField] private Button minusButton;
    [SerializeField] private Button plusButton;
    [SerializeField] private TMP_Text timeText;

    [Header("Range (minutes)")]
    [SerializeField] private int minMinutes = 0;
    [Tooltip("The onboarding's first task is one minute, so + is disabled after one press.")]
    [SerializeField] private int maxMinutes = 1;
    [SerializeField] private int stepMinutes = 1;

    [Header("Animation")]
    [Tooltip("Total flip time in seconds (flip out + flip in).")]
    [SerializeField] private float flipDuration = 0.2f;
    [Tooltip("How far the new value swings past flat before settling (0 = none).")]
    [SerializeField] private float flipOvershoot = 1.6f;

    public UnityEvent<int> onMinutesChanged;

    public int Minutes { get; private set; }

    private Coroutine flipRoutine;
    private bool initialized;
    private Quaternion baseRotation;

    private void Awake()
    {
        Init();
    }

    // Also run from ResetTo, which the flow may call before this object has ever been active.
    private void Init()
    {
        if (initialized)
            return;
        initialized = true;

        baseRotation = timeText.rectTransform.localRotation;

        if (minusButton != null)
            minusButton.onClick.AddListener(() => Change(-stepMinutes));
        if (plusButton != null)
            plusButton.onClick.AddListener(() => Change(stepMinutes));

        Minutes = minMinutes;
        timeText.text = Format(Minutes);
        RefreshButtons();
    }

    private void OnDisable()
    {
        // A coroutine stops with the object; leave the text in its resting state.
        FinishAnimation();
    }

    /// <summary>Shows the given time without animating (e.g. when the panel opens).</summary>
    public void ResetTo(int minutes)
    {
        Init();
        FinishAnimation();
        Minutes = Mathf.Clamp(minutes, minMinutes, maxMinutes);
        timeText.text = Format(Minutes);
        RefreshButtons();
        onMinutesChanged?.Invoke(Minutes);
    }

    private void Change(int delta)
    {
        int next = Mathf.Clamp(Minutes + delta, minMinutes, maxMinutes);
        if (next == Minutes)
            return;

        string previous = Format(Minutes);
        Minutes = next;
        RefreshButtons();
        onMinutesChanged?.Invoke(Minutes);

        FinishAnimation();
        if (isActiveAndEnabled)
            flipRoutine = StartCoroutine(Flip(previous, Format(next), delta > 0 ? 1f : -1f));
        else
            timeText.text = Format(next);
    }

    private void RefreshButtons()
    {
        if (minusButton != null)
            minusButton.interactable = Minutes > minMinutes;
        if (plusButton != null)
            plusButton.interactable = Minutes < maxMinutes;
    }

    private static string Format(int minutes) => $"{minutes:00}:00";

    // Flip-clock style: the old value flips away to edge-on, the text swaps, then the new value flips in from the
    // other side with a small overshoot. Counting up flips top-away; counting down flips the other way.
    private IEnumerator Flip(string from, string to, float direction)
    {
        var rt = timeText.rectTransform;
        timeText.text = from;
        float half = flipDuration * 0.5f;

        for (float t = 0f; t < 1f; t += Time.deltaTime / half)
        {
            rt.localRotation = Quaternion.Euler(90f * direction * t * t, 0f, 0f); // ease-in
            yield return null;
        }

        timeText.text = to;
        for (float t = 0f; t < 1f; t += Time.deltaTime / half)
        {
            rt.localRotation = Quaternion.Euler(-90f * direction * (1f - EaseOutBack(t)), 0f, 0f);
            yield return null;
        }

        flipRoutine = null;
        FinishAnimation();
    }

    private float EaseOutBack(float t)
    {
        float c = flipOvershoot;
        float u = t - 1f;
        return 1f + (c + 1f) * u * u * u + c * u * u;
    }

    private void FinishAnimation()
    {
        if (flipRoutine != null)
        {
            StopCoroutine(flipRoutine);
            flipRoutine = null;
        }
        if (timeText != null && initialized)
        {
            timeText.rectTransform.localRotation = baseRotation;
            timeText.text = Format(Minutes);
        }
    }
}
