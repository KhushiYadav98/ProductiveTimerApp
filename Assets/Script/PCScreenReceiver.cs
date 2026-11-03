using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;

// Receives the PC screen from the browser sender page (ScreenShareServer/) over WebRTC
// and shows it on this object's renderer (use a Quad with a URP Unlit material).
[RequireComponent(typeof(Renderer))]
public class PCScreenReceiver : MonoBehaviour
{
    [Header("Connection")]
    [Tooltip("Find the PC server automatically on the local network.")]
    [SerializeField] private bool autoDiscover = true;
    [Tooltip("Used when auto-discovery is off or finds nothing. Printed by the server on startup, e.g. ws://192.168.1.20:8080")]
    [SerializeField] private string signalingUrl = "ws://10.59.1.216:8080";
    [SerializeField] private float reconnectDelay = 2f;

    private const int DiscoveryPort = 47800;

    [Header("Screen")]
    [Tooltip("Width in meters; height follows the stream's aspect ratio.")]
    [SerializeField] private float screenWidth = 1.0f;
    [Tooltip("Move the screen in front of the user when the first frame arrives.")]
    [SerializeField] private bool placeInFrontOfUser = true;
    [SerializeField] private float distanceFromHead = 0.8f;
    [SerializeField] private float heightOffsetFromHead = -0.05f;
    [Tooltip("Enable if the image appears upside down on device.")]
    [SerializeField] private bool flipVertically;

    [Serializable]
    private class SignalMessage
    {
        public string type;
        public string role;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    private readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    private readonly List<RTCIceCandidate> pendingCandidates = new List<RTCIceCandidate>();

    private ClientWebSocket socket;
    private CancellationTokenSource cancellation;
    private RTCPeerConnection peer;
    private bool remoteDescriptionSet;
    private bool placed;
    private Material screenMaterial;

    private void Start()
    {
        screenMaterial = GetComponent<Renderer>().material;
        if (flipVertically)
        {
            screenMaterial.mainTextureScale = new Vector2(1f, -1f);
            screenMaterial.mainTextureOffset = new Vector2(0f, 1f);
        }

        StartCoroutine(WebRTC.Update());

        cancellation = new CancellationTokenSource();
        _ = SignalingLoop(cancellation.Token);
    }

    private void OnDestroy()
    {
        cancellation?.Cancel();
        socket?.Dispose();
        ClosePeer();
    }

    private void Update()
    {
        while (inbox.TryDequeue(out var json))
            HandleMessage(JsonUtility.FromJson<SignalMessage>(json));
    }

    // ---------- Signaling (background thread) ----------

    private async Task SignalingLoop(CancellationToken token)
    {
        var buffer = new byte[64 * 1024];

        while (!token.IsCancellationRequested)
        {
            string url = signalingUrl;
            try
            {
                if (autoDiscover)
                    url = await DiscoverServerAsync(token) ?? signalingUrl;

                socket?.Dispose();
                socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(url), token);
                Debug.Log($"[PCScreen] Connected to {url}");
                await SendAsync(new SignalMessage { type = "hello", role = "receiver" });

                var message = new StringBuilder();
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        inbox.Enqueue(message.ToString());
                        message.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PCScreen] Signaling error ({url}): {e.Message}");
            }

            if (!token.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelay));
        }
    }

    // Broadcasts a discovery request; the PC server replies directly with its signaling port.
    // Returns null if no server answers.
    private static async Task<string> DiscoverServerAsync(CancellationToken token)
    {
        using var udp = new UdpClient(0) { EnableBroadcast = true };
        var request = Encoding.ASCII.GetBytes("PT_DISCOVER");

        for (int attempt = 0; attempt < 3 && !token.IsCancellationRequested; attempt++)
        {
            await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            // Windows doesn't loop broadcasts back to the sender, so also ask this machine directly (Editor / Link testing).
            await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, DiscoveryPort));

            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(1000, token)) != receive)
                continue;

            var reply = Encoding.ASCII.GetString(receive.Result.Buffer);
            if (reply.StartsWith("PT_SERVER:") && int.TryParse(reply.Substring(10), out int port))
            {
                var url = $"ws://{receive.Result.RemoteEndPoint.Address}:{port}";
                Debug.Log($"[PCScreen] Discovered server at {url}");
                return url;
            }
        }

        Debug.LogWarning("[PCScreen] No server found by auto-discovery; using Signaling Url.");
        return null;
    }

    private async Task SendAsync(SignalMessage msg)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));
        await sendLock.WaitAsync();
        try
        {
            if (socket != null && socket.State == WebSocketState.Open)
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void Send(SignalMessage msg) => _ = SendAsync(msg);

    // ---------- WebRTC (main thread) ----------

    private void HandleMessage(SignalMessage msg)
    {
        switch (msg.type)
        {
            case "offer":
                StartCoroutine(AnswerOffer(msg.sdp));
                break;

            case "candidate":
                var candidate = new RTCIceCandidate(new RTCIceCandidateInit
                {
                    candidate = msg.candidate,
                    sdpMid = msg.sdpMid,
                    sdpMLineIndex = msg.sdpMLineIndex
                });
                // Candidates can arrive before the offer has been applied.
                if (peer != null && remoteDescriptionSet)
                    peer.AddIceCandidate(candidate);
                else
                    pendingCandidates.Add(candidate);
                break;

            case "peer-left":
                if (msg.role == "sender")
                    ClosePeer();
                break;
        }
    }

    private IEnumerator AnswerOffer(string sdp)
    {
        ClosePeer();

        var config = new RTCConfiguration { iceServers = Array.Empty<RTCIceServer>() };
        peer = new RTCPeerConnection(ref config);
        peer.OnIceCandidate = c => Send(new SignalMessage
        {
            type = "candidate",
            candidate = c.Candidate,
            sdpMid = c.SdpMid,
            sdpMLineIndex = c.SdpMLineIndex ?? 0
        });
        peer.OnConnectionStateChange = state => Debug.Log($"[PCScreen] Connection: {state}");
        peer.OnTrack = e =>
        {
            if (e.Track is VideoStreamTrack video)
                video.OnVideoReceived += OnVideoReceived;
        };

        var offer = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
        var setRemote = peer.SetRemoteDescription(ref offer);
        yield return setRemote;
        if (setRemote.IsError)
        {
            Debug.LogError($"[PCScreen] SetRemoteDescription failed: {setRemote.Error.message}");
            yield break;
        }

        remoteDescriptionSet = true;
        foreach (var c in pendingCandidates)
            peer.AddIceCandidate(c);
        pendingCandidates.Clear();

        var createAnswer = peer.CreateAnswer();
        yield return createAnswer;
        if (createAnswer.IsError)
        {
            Debug.LogError($"[PCScreen] CreateAnswer failed: {createAnswer.Error.message}");
            yield break;
        }

        var answer = createAnswer.Desc;
        var setLocal = peer.SetLocalDescription(ref answer);
        yield return setLocal;
        if (setLocal.IsError)
        {
            Debug.LogError($"[PCScreen] SetLocalDescription failed: {setLocal.Error.message}");
            yield break;
        }

        Send(new SignalMessage { type = "answer", sdp = answer.sdp });
    }

    private void OnVideoReceived(Texture texture)
    {
        screenMaterial.mainTexture = texture;
        transform.localScale = new Vector3(screenWidth, screenWidth * texture.height / texture.width, 1f);

        if (placeInFrontOfUser && !placed)
        {
            PlaceInFrontOfUser();
            placed = true;
        }
    }

    private void PlaceInFrontOfUser()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        var head = rig != null ? rig.centerEyeAnchor : Camera.main.transform;

        var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        transform.position = head.position + forward * distanceFromHead + Vector3.up * heightOffsetFromHead;
        // A Quad's visible face points along -Z, so face it toward the user by looking along +forward.
        transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    private void ClosePeer()
    {
        remoteDescriptionSet = false;
        pendingCandidates.Clear();
        if (peer != null)
        {
            peer.Close();
            peer.Dispose();
            peer = null;
        }
    }
}
