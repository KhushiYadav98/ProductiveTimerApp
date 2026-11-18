using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenRelay
{
    internal sealed class ScreenRelaySocket : IDisposable
    {
        private const int MaxMessageBytes = 128 * 1024;
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1);
        private readonly byte[] receiveBuffer = new byte[8192];
        private readonly CancellationToken token;

        public ScreenRelaySocket()
        {
            token = lifetime.Token;
        }

        public async Task<string> ConnectAsync(string signalingUrl)
        {
            if (!Uri.TryCreate(signalingUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "wss")
                throw new ArgumentException("Use a secure wss:// signaling URL.", nameof(signalingUrl));

            var origin = new UriBuilder("https", endpoint.Host, endpoint.Port).Uri.GetLeftPart(UriPartial.Authority);
            socket.Options.SetRequestHeader("Origin", origin);

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
                return await ReceiveAsync(deadline.Token).ConfigureAwait(false);
            }
        }

        public async Task SendAsync(string json)
        {
            var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));
            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        public Task<string> ReceiveAsync() => ReceiveAsync(token);

        private async Task<string> ReceiveAsync(CancellationToken cancellation)
        {
            using (var message = new MemoryStream())
            {
                WebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellation)
                        .ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close) return null;
                    if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > MaxMessageBytes)
                        throw new InvalidDataException("Invalid ScreenRelay signaling message.");

                    message.Write(receiveBuffer, 0, frame.Count);
                } while (!frame.EndOfMessage);

                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }

        public void Dispose()
        {
            lifetime.Cancel();
            socket.Dispose();
            lifetime.Dispose();
        }
    }
}
