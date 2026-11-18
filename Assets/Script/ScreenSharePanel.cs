using System.Collections;
using ScreenRelay;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// "Connect your screen" step of a new task (the Yes answer): a panel in front of the user with the screen preview,
// the address to open on the computer, a code field with a poke number pad, and a Screen Share button.
// Connecting uses ScreenRelayViewer (WebRTC via the ScreenRelay server, as in the ScreenShareDemo scene). Once the
// live screen arrives, the code controls go away, the panel glides up and back to become a floating monitor that stays
// during the task, and the task flow continues with naming the task (same as answering No).
public class ScreenSharePanel : MonoBehaviour
{
    [SerializeField] private ScreenRelayViewer viewer;
    [SerializeField] private TaskCreationFlow flow;

    [Header("UI")]
    [SerializeField] private RawImage screenImage;
    [Tooltip("The area the video is fitted into (keeps the screen's aspect ratio).")]
    [SerializeField] private RectTransform screenArea;
    [SerializeField] private GameObject screenPlaceholder;
    [SerializeField] private TMP_InputField codeInput;
    [SerializeField] private Button shareButton;
    [SerializeField] private PokeKeyboard numberPad;
    [Tooltip("Shown only while connecting (hidden once the screen is live): instructions, code field, button...")]
    [SerializeField] private GameObject[] connectControls;
    [SerializeField] private TMP_Text statusText;

    [Header("Placement")]
    [Tooltip("While entering the code: distance from the head (within poke reach), height vs. the eyes, width.")]
    [SerializeField] private float connectDistance = 0.5f;
    [SerializeField] private float connectHeight = -0.06f;
    [SerializeField] private float connectWidth = 0.55f;
    [Tooltip("Once live, as a floating monitor in front of the user (it can be grabbed and moved/resized): " +
             "side angle (negative = left), distance, height vs. the eyes and width.")]
    [SerializeField] private float monitorYaw = 0f;
    [SerializeField] private float monitorDistance = 1.45f;
    [SerializeField] private float monitorHeight = 0.42f;
    [SerializeField] private float monitorWidth = 1.25f;
    [Tooltip("Grab area once live (the screen); empty = the whole canvas.")]
    [SerializeField] private RectTransform monitorGrabArea;
    [SerializeField] private float glideTime = 0.8f;

    [Header("Video quality")]
    [Tooltip("Copy each frame into a mipmapped texture (trilinear + anisotropic filtering). Without mipmaps a " +
             "computer screen shown in VR shimmers/jitters badly and text is hard to read.")]
    [SerializeField] private bool smoothVideo = true;
    [Range(1, 16)] [SerializeField] private int anisoLevel = 8;

    [Header("Audio")]
    [SerializeField] private AudioClip connectedSound;
    [Range(0f, 1f)] [SerializeField] private float volume = 0.7f;

    public bool IsLive { get; private set; }

    private Transform head;
    private RectTransform rect;
    private AudioSource source;
    private bool continued;
    private bool showing;
    private Oculus.Interaction.Grabbable grabbable;
    private BoxCollider grabBox;

    private RenderTexture smoothed;

    private bool IsGrabbed => grabbable != null && grabbable.SelectingPoints != null && grabbable.SelectingPoints.Count > 0;

    private void Awake()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;
        rect = (RectTransform)transform;
        grabbable = GetComponent<Oculus.Interaction.Grabbable>();
        grabBox = GetComponent<BoxCollider>();
        SetGrabArea(null);
        if (viewer == null)
            viewer = GetComponent<ScreenRelayViewer>();
        if (flow == null)
            flow = FindFirstObjectByType<TaskCreationFlow>();

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0.6f;

        if (shareButton != null)
            shareButton.onClick.AddListener(Share);
        if (codeInput != null)
        {
            codeInput.shouldHideSoftKeyboard = true;
            codeInput.onSelect.AddListener(_ => ShowPad());
        }
        if (numberPad != null)
            numberPad.onDone.AddListener(Share);
        if (viewer != null)
        {
            viewer.VideoChanged += OnVideoChanged;
            viewer.StatusChanged += OnStatusChanged;
        }
        // Hidden until asked for (unless this Awake is the first activation by Show()).
        if (!showing)
            gameObject.SetActive(false);
    }

    // The viewer's texture is updated in place by WebRTC; copy it into the mipmapped texture every frame.
    private void LateUpdate()
    {
        var source = viewer != null ? viewer.CurrentTexture : null;
        if (screenImage == null)
            return;
        if (source == null)
        {
            screenImage.enabled = false;
            return;
        }
        if (!smoothVideo)
        {
            screenImage.texture = source;
            screenImage.enabled = true;
            return;
        }
        if (smoothed == null || smoothed.width != source.width || smoothed.height != source.height)
        {
            if (smoothed != null)
                smoothed.Release();
            smoothed = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32)
            {
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                anisoLevel = anisoLevel,
                wrapMode = TextureWrapMode.Clamp,
                name = "ScreenShareSmoothed",
            };
            smoothed.Create();
            FitVideo(source);
        }
        Graphics.Blit(source, smoothed);
        screenImage.texture = smoothed;
        screenImage.enabled = true;
    }

    private void OnDestroy()
    {
        if (smoothed != null)
            smoothed.Release();
        if (viewer != null)
        {
            viewer.VideoChanged -= OnVideoChanged;
            viewer.StatusChanged -= OnStatusChanged;
        }
    }

    /// <summary>Shows the connect panel in front of the user, with the number pad up.</summary>
    public void Show()
    {
        continued = false;
        showing = true;
        gameObject.SetActive(true);
        SetConnectControls(!IsLive);
        Place(0f, connectDistance, connectHeight, connectWidth, false);
        if (!IsLive)
            ShowPad();
        else
            Continue(); // already sharing: go straight on
    }

    /// <summary>Stops sharing and hides the panel.</summary>
    public void Close()
    {
        if (viewer != null)
            viewer.EndViewing();
        HidePad();
        gameObject.SetActive(false);
    }

    private void Share()
    {
        HidePad();
        if (viewer != null)
            viewer.ConnectFromInput();
    }

    private void ShowPad()
    {
        if (numberPad != null && !IsLive)
            numberPad.Show(codeInput);
    }

    private void HidePad()
    {
        if (numberPad != null)
            numberPad.Hide();
    }

    private void OnStatusChanged(string status)
    {
        if (statusText != null)
            statusText.text = status;
        // A failed attempt brings the pad back for another try.
        if (!IsLive && isActiveAndEnabled && codeInput != null && codeInput.interactable && numberPad != null && !numberPad.IsShown
            && status != null && !status.StartsWith("Connecting") && !status.StartsWith("Waiting") && !status.StartsWith("Approved"))
            ShowPad();
    }

    private void OnVideoChanged(Texture texture)
    {
        bool live = texture != null;
        if (screenPlaceholder != null)
            screenPlaceholder.SetActive(!live);
        if (live)
            FitVideo(texture);

        if (live && !IsLive)
        {
            IsLive = true;
            HidePad();
            SetConnectControls(false);
            if (connectedSound != null)
                source.PlayOneShot(connectedSound, volume);
            Continue();
        }
        else if (!live && IsLive)
        {
            IsLive = false; // sharing ended; the monitor shows the placeholder/status
        }
    }

    // On to naming the task, with the screen moving out of the way as a floating monitor.
    private void Continue()
    {
        if (continued)
            return;
        continued = true;
        SetGrabArea(monitorGrabArea);
        Place(monitorYaw, monitorDistance, monitorHeight, monitorWidth, true);
        if (flow != null)
            flow.ContinueToTaskName();
    }

    private void SetConnectControls(bool on)
    {
        if (connectControls == null)
            return;
        foreach (var go in connectControls)
            if (go != null)
                go.SetActive(on);
    }

    // The box the hands can grab: a part of the canvas (e.g. just the screen), or all of it.
    private void SetGrabArea(RectTransform area)
    {
        if (grabBox == null)
            return;
        Rect r = rect.rect;
        Vector3 center = r.center;
        Vector2 size = r.size;
        if (area != null)
        {
            var corners = new Vector3[4];
            area.GetWorldCorners(corners);
            Vector3 a = rect.InverseTransformPoint(corners[0]), b = rect.InverseTransformPoint(corners[2]);
            center = (a + b) * 0.5f;
            size = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }
        grabBox.center = new Vector3(center.x, center.y, 0f);
        grabBox.size = new Vector3(size.x, size.y, 40f);
    }

    // Keeps the video's aspect ratio inside the screen area.
    private void FitVideo(Texture texture)
    {
        if (screenImage == null || screenArea == null || texture.height <= 0)
            return;
        float aspect = (float)texture.width / texture.height;
        Vector2 area = screenArea.rect.size;
        Vector2 size = area.x / area.y > aspect ? new Vector2(area.y * aspect, area.y) : new Vector2(area.x, area.x / aspect);
        screenImage.rectTransform.sizeDelta = size;
    }

    private void Place(float yaw, float distance, float height, float width, bool glide)
    {
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        forward = Quaternion.Euler(0f, yaw, 0f) * forward.normalized;
        Vector3 position = head.position + forward * distance + Vector3.up * height;
        // Tilt to face the eyes (a world canvas is read from its -Z side).
        Quaternion rotation = Quaternion.LookRotation(position - head.position, Vector3.up);
        float scale = width / Mathf.Max(rect.rect.width, 1f);

        StopAllCoroutines();
        if (glide && isActiveAndEnabled)
            StartCoroutine(Glide(position, rotation, scale));
        else
        {
            rect.SetPositionAndRotation(position, rotation);
            rect.localScale = Vector3.one * scale;
        }
    }

    private IEnumerator Glide(Vector3 position, Quaternion rotation, float scale)
    {
        Vector3 p0 = rect.position;
        Quaternion r0 = rect.rotation;
        float s0 = rect.localScale.x;
        for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(glideTime, 0.01f))
        {
            if (IsGrabbed)
                yield break; // the user took it: leave it to them
            float k = t * t * (3f - 2f * t);
            rect.SetPositionAndRotation(Vector3.Lerp(p0, position, k), Quaternion.Slerp(r0, rotation, k));
            rect.localScale = Vector3.one * Mathf.Lerp(s0, scale, k);
            yield return null;
        }
        rect.SetPositionAndRotation(position, rotation);
        rect.localScale = Vector3.one * scale;
    }
}
