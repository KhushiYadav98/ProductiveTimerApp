using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TMPro;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

namespace ScreenRelay
{

    //https://fsmiitd.in:8443/
    
    [DisallowMultipleComponent]
    public sealed class ScreenRelayViewer : MonoBehaviour
    {
        [SerializeField] private string signalingUrl = "wss://fsmiitd.in:8443/signal";
        [SerializeField] private TMP_InputField codeInput;
        [SerializeField] private RawImage remoteImage;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private bool runWebRtcUpdate = true;

        public event Action<string> StatusChanged;
        public event Action<Texture> VideoChanged;
        public Texture CurrentTexture { get; private set; }

        private Attempt active;
        private Coroutine webRtcUpdate;

        private void OnEnable()
        {
            if (runWebRtcUpdate) webRtcUpdate = StartCoroutine(WebRTC.Update());
            if (codeInput != null) codeInput.onSubmit.AddListener(RequestView);
            SetVideo(null);
            Report("Enter the host’s eight-digit code.");
        }

        private void OnDisable()
        {
            if (codeInput != null) codeInput.onSubmit.RemoveListener(RequestView);
            if (active != null) StopAttempt(active);
            if (webRtcUpdate != null) StopCoroutine(webRtcUpdate);
            webRtcUpdate = null;
        }

        private void Update()
        {
            if (active != null && active.Deadline.HasValue && Time.realtimeSinceStartup >= active.Deadline.Value)
                StopAttempt(active, "Connection timed out. Try the host’s current code.");
        }

        public void ConnectFromInput()
        {
            if (codeInput != null) RequestView(codeInput.text);
        }

        public void RequestView(string code)
        {
            if (!isActiveAndEnabled || active != null) return;
            var digits = Regex.Replace(code ?? string.Empty, @"\s+", string.Empty);
            if (!Regex.IsMatch(digits, @"\A[0-9]{8}\z"))
            {
                if (codeInput != null) codeInput.SetTextWithoutNotify(string.Empty);
                Report("Enter exactly eight digits.");
                return;
            }

            var attempt = new Attempt { Deadline = Time.realtimeSinceStartup + 16f };
            active = attempt;
            if (codeInput != null)
            {
                codeInput.SetTextWithoutNotify(string.Empty);
                codeInput.interactable = false;
            }
            Report("Connecting to ScreenRelay…");
            _ = RunAttemptAsync(attempt, digits);
        }

        public void EndViewing()
        {
            if (active != null) StopAttempt(active, "Disconnected. Enter a code to view again.");
        }

        private async Task RunAttemptAsync(Attempt attempt, string code)
        {
            try
            {
                var hello = await attempt.Socket.ConnectAsync(signalingUrl);
                if (attempt != active) return;
                if (hello == null || JsonUtility.FromJson<Signal>(hello)?.type != "ready")
                {
                    StopAttempt(attempt, "The signaling server didn’t respond. Try again.");
                    return;
                }

                await attempt.Socket.SendAsync(JsonUtility.ToJson(new Signal { type = "request-view", code = code }));
                while (attempt == active)
                {
                    var message = await attempt.Socket.ReceiveAsync();
                    if (attempt != active) return;
                    if (message == null)
                    {
                        StopAttempt(attempt, "Disconnected from ScreenRelay.");
                        return;
                    }
                    HandleSignal(attempt, JsonUtility.FromJson<Signal>(message));
                }
            }
            catch (Exception error)
            {
                if (attempt != active) return;
                Debug.LogWarning($"ScreenRelay signaling failed ({error.GetType().Name}).", this);
                StopAttempt(attempt, "Couldn’t connect to ScreenRelay. Check your network and try again.");
            }
        }

        private void HandleSignal(Attempt attempt, Signal message)
        {
            switch (message?.type)
            {
                case "error":
                    StopAttempt(attempt, message.message);
                    break;
                case "request-sent":
                    attempt.Id = message.id;
                    attempt.Deadline = Time.realtimeSinceStartup + 50f;
                    Report($"Waiting for approval. Request ID {message.id.Substring(0, 8).ToUpperInvariant()}.");
                    break;
                case "request-ended":
                case "session-ended":
                    if (message.id == attempt.Id) StopAttempt(attempt, EndMessage(message.reason));
                    break;
                case "session-started":
                    if (message.id == attempt.Id && message.role == "viewer" && attempt.Peer == null)
                        StartPeer(attempt, message.iceServers);
                    break;
                case "description":
                    if (message.id == attempt.Id && attempt.Peer != null && !attempt.OfferStarted && message.description?.type == "offer")
                    {
                        attempt.OfferStarted = true;
                        StartCoroutine(AnswerOffer(attempt, message.description.sdp));
                    }
                    break;
                case "candidate":
                    if (message.id == attempt.Id && attempt.Peer != null && message.candidate != null)
                    {
                        if (attempt.RemoteDescriptionSet) AddRemoteCandidate(attempt, message.candidate);
                        else attempt.PendingCandidates.Add(message.candidate);
                    }
                    break;
            }
        }

        private void StartPeer(Attempt attempt, IceServer[] servers)
        {
            var config = new RTCConfiguration
            {
                iceServers = Array.ConvertAll(servers ?? Array.Empty<IceServer>(), server => new RTCIceServer
                {
                    urls = server.urls,
                    username = server.username ?? string.Empty,
                    credential = server.credential ?? string.Empty,
                    credentialType = RTCIceCredentialType.Password,
                }),
            };
            var peer = new RTCPeerConnection(ref config);
            attempt.Peer = peer;
            attempt.Deadline = Time.realtimeSinceStartup + 40f;
            peer.OnIceCandidate = candidate =>
            {
                if (candidate == null) return;
                using (candidate)
                {
                    if (attempt != active || string.IsNullOrEmpty(candidate.Candidate)) return;
                    _ = SendSignalAsync(attempt, new Signal
                    {
                        type = "candidate",
                        id = attempt.Id,
                        candidate = new IceCandidate
                        {
                            candidate = candidate.Candidate,
                            sdpMid = candidate.SdpMid,
                            sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
                        },
                    });
                }
            };
            peer.OnTrack = trackEvent =>
            {
                if (attempt != active || !(trackEvent.Track is VideoStreamTrack track)) return;
                if (attempt.Track != null) attempt.Track.OnVideoReceived -= attempt.OnFrame;
                attempt.Track = track;
                attempt.OnFrame = texture =>
                {
                    if (attempt != active || texture == null) return;
                    if (!attempt.HasVideo)
                    {
                        attempt.HasVideo = true;
                        attempt.Deadline = null;
                        SetVideo(texture);
                        if (attempt == active) Report("Live screen");
                    }
                    else SetVideo(texture);
                };
                track.OnVideoReceived += attempt.OnFrame;
            };
            peer.OnConnectionStateChange = state =>
            {
                if (attempt != active) return;
                if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Closed)
                    StopAttempt(attempt, "Video connection ended. Try the host’s current code.");
                else if (state == RTCPeerConnectionState.Disconnected)
                {
                    attempt.Deadline = Time.realtimeSinceStartup + 12f;
                    Report("Reconnecting video…");
                }
                else if (state == RTCPeerConnectionState.Connected && attempt.HasVideo)
                {
                    attempt.Deadline = null;
                    Report("Live screen");
                }
            };
            Report("Approved. Connecting video…");
        }

        private IEnumerator AnswerOffer(Attempt attempt, string sdp)
        {
            var peer = attempt.Peer;
            var offer = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
            var remote = peer.SetRemoteDescription(ref offer);
            yield return remote;
            if (attempt != active) yield break;
            if (remote.IsError)
            {
                StopAttempt(attempt, "Couldn’t accept the host’s video offer.");
                yield break;
            }

            attempt.RemoteDescriptionSet = true;
            foreach (var candidate in attempt.PendingCandidates) AddRemoteCandidate(attempt, candidate);
            attempt.PendingCandidates.Clear();

            var answer = peer.CreateAnswer();
            yield return answer;
            if (attempt != active) yield break;
            if (answer.IsError)
            {
                StopAttempt(attempt, "Couldn’t create a video answer.");
                yield break;
            }

            var description = answer.Desc;
            var local = peer.SetLocalDescription(ref description);
            yield return local;
            if (attempt != active) yield break;
            if (local.IsError)
            {
                StopAttempt(attempt, "Couldn’t start the video session.");
                yield break;
            }

            _ = SendSignalAsync(attempt, new Signal
            {
                type = "description",
                id = attempt.Id,
                description = new Description { type = "answer", sdp = description.sdp },
            });
        }

        private static void AddRemoteCandidate(Attempt attempt, IceCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.candidate)) return;
            using (var ice = new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex,
            }))
            {
                attempt.Peer.AddIceCandidate(ice);
            }
        }

        private async Task SendSignalAsync(Attempt attempt, Signal message)
        {
            try
            {
                await attempt.Socket.SendAsync(JsonUtility.ToJson(message));
            }
            catch (Exception)
            {
                if (attempt == active) StopAttempt(attempt, "Lost the connection to ScreenRelay.");
            }
        }

        private void StopAttempt(Attempt attempt, string status = null)
        {
            active = null;
            attempt.Socket.Dispose();
            if (attempt.Track != null) attempt.Track.OnVideoReceived -= attempt.OnFrame;
            attempt.Peer?.Dispose();
            if (codeInput != null) codeInput.interactable = true;
            SetVideo(null);
            if (status != null) Report(status);
        }

        private void SetVideo(Texture texture)
        {
            if (remoteImage != null)
            {
                remoteImage.texture = texture;
                remoteImage.enabled = texture != null;
            }
            if (ReferenceEquals(CurrentTexture, texture)) return;
            CurrentTexture = texture;
            VideoChanged?.Invoke(texture);
        }

        private void Report(string status)
        {
            if (statusText != null) statusText.text = status;
            else if (codeInput != null && codeInput.placeholder is TMP_Text placeholder) placeholder.text = status;
            StatusChanged?.Invoke(status);
        }

        private static string EndMessage(string reason)
        {
            switch (reason)
            {
                case "declined": return "The host declined the request.";
                case "cancelled": return "The request was cancelled.";
                case "expired": return "Approval timed out. Enter the host’s current code.";
                case "code-expired": return "The share code expired. Ask the host for a new code.";
                case "host-stopped": return "The host stopped sharing.";
                case "failed": return "Video connection failed. Try again or switch networks.";
                case "left": return "The host disconnected.";
                default: return "The viewing session ended.";
            }
        }

        private sealed class Attempt
        {
            public readonly ScreenRelaySocket Socket = new ScreenRelaySocket();
            public readonly List<IceCandidate> PendingCandidates = new List<IceCandidate>();
            public string Id;
            public RTCPeerConnection Peer;
            public VideoStreamTrack Track;
            public OnVideoReceived OnFrame;
            public float? Deadline;
            public bool OfferStarted;
            public bool RemoteDescriptionSet;
            public bool HasVideo;
        }

        [Serializable]
        private sealed class Signal
        {
            public string type;
            public string code;
            public string id;
            public string role;
            public string message;
            public string reason;
            public IceServer[] iceServers;
            public Description description;
            public IceCandidate candidate;
        }

        [Serializable]
        private sealed class Description
        {
            public string type;
            public string sdp;
        }

        [Serializable]
        private sealed class IceCandidate
        {
            public string candidate;
            public string sdpMid;
            public int sdpMLineIndex;
        }

        [Serializable]
        private sealed class IceServer
        {
            public string[] urls;
            public string username;
            public string credential;
        }
    }
}
