using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// One connected DevTools window, talking to one mounted page.
    ///
    /// The socket lives on the thread pool and nothing here touches the game directly: a command is parsed here,
    /// executed by <see cref="Domains"/> on the main thread through <see cref="MainThread.Run{T}"/>, and the answer
    /// is written back here. Sends are funnelled through one queue and one gate, because two SendAsync calls in
    /// flight on the same WebSocket is an error in .NET and because the protocol is order-sensitive: a result must
    /// not overtake the events that belong before it.
    /// </summary>
    internal sealed class CdpSession
    {
        /// <summary>Biggest inbound frame accepted. `DOM.setOuterHTML` can carry a whole page, so it is generous -
        /// but it is a limit, because the sender is not trusted to be DevTools.</summary>
        private const int MaxMessageBytes = 8 * 1024 * 1024;

        private readonly WebSocket _ws;
        private readonly ConcurrentQueue<string> _outbox = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly List<string> _deferred = new List<string>();

        internal CdpSession(WebSocket socket, string targetId)
        {
            _ws = socket;
            TargetId = targetId;
        }

        /// <summary>Which page this window is attached to.</summary>
        internal string TargetId { get; }

        /// <summary>Objects handed out by reference during this session.</summary>
        internal ObjectStore Objects { get; } = new ObjectStore();

        /// <summary>DOM nodes this window knows by id.</summary>
        internal NodeStore Nodes { get; } = new NodeStore();

        internal bool RuntimeEnabled { get; set; }

        internal bool LogEnabled { get; set; }

        internal bool DomEnabled { get; set; }

        internal bool PageEnabled { get; set; }

        internal bool CssEnabled { get; set; }

        /// <summary>The page's stylesheet as this window has been shown it. Rebuilt when the page hands out a
        /// different rule list.</summary>
        internal SheetModel Sheet { get; set; }

        /// <summary>Bumped for every sheet handed out, so an id from before a reload can never be mistaken for a
        /// live one.</summary>
        internal int SheetGeneration { get; set; }

        internal bool Alive => _ws != null && _ws.State == WebSocketState.Open;

        /// <summary>Read commands until the window goes away.</summary>
        internal async Task RunAsync(CancellationToken cancel)
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();

            try
            {
                while (!cancel.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult received = await _ws
                        .ReceiveAsync(new ArraySegment<byte>(buffer), cancel).ConfigureAwait(false);

                    if (received.MessageType == WebSocketMessageType.Close) break;

                    message.Write(buffer, 0, received.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        Core.Log?.Warning("[Sideload/cdp] a message exceeded the size limit - dropping the connection.");
                        break;
                    }

                    if (!received.EndOfMessage) continue;

                    string text = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);

                    Handle(text);
                }
            }
            catch (OperationCanceledException) { /* the server is stopping */ }
            catch (Exception e)
            {
                if (!cancel.IsCancellationRequested) Core.Log?.Msg("[Sideload/cdp] devtools disconnected: " + e.Message);
            }
        }

        /// <summary>
        /// Run one command and answer it. Protocol errors are answered, not thrown: DevTools sends methods no
        /// embedded implementation has, and a connection that dies on the first unknown one is useless.
        /// </summary>
        private void Handle(string raw)
        {
            JsonValue message = JsonValue.Parse(raw);
            string method = message["method"].AsString();
            int id = message["id"].AsInt(-1);

            if (string.IsNullOrEmpty(method))
            {
                if (id >= 0) SendError(id, -32600, "the message has no method");
                return;
            }

            JsonValue args = message["params"];
            string result;

            try
            {
                result = MainThread.Run(() => Domains.Invoke(this, method, args));
            }
            catch (CdpException e)
            {
                if (id >= 0) SendError(id, e.Code, e.Message);
                FlushDeferred();
                return;
            }
            catch (TimeoutException e)
            {
                if (id >= 0) SendError(id, -32000, e.Message);
                return;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[Sideload/cdp] {method} failed: {e.Message}");
                if (id >= 0) SendError(id, -32000, e.Message);
                FlushDeferred();
                return;
            }

            if (id >= 0)
                Send(new Json.Obj().Num("id", id).Raw("result", result ?? Json.EmptyObject).Done());

            FlushDeferred();
        }

        /// <summary>Send an event now.</summary>
        internal void Emit(string method, string parameters)
        {
            Send(new Json.Obj().Str("method", method).Raw("params", parameters ?? Json.EmptyObject).Done());
        }

        /// <summary>
        /// Send an event as soon as the command being handled has been answered. `Runtime.enable` is the reason this
        /// exists: the execution context it announces has to arrive after the reply, or the frontend files it against
        /// a session it does not consider ready yet.
        /// </summary>
        internal void EmitAfterReply(string method, string parameters)
        {
            _deferred.Add(new Json.Obj().Str("method", method).Raw("params", parameters ?? Json.EmptyObject).Done());
        }

        private void FlushDeferred()
        {
            if (_deferred.Count == 0) return;

            foreach (string message in _deferred) Send(message);
            _deferred.Clear();
        }

        private void SendError(int id, int code, string message)
        {
            Send(new Json.Obj()
                .Num("id", id)
                .Raw("error", new Json.Obj().Num("code", code).Str("message", message ?? "").Done())
                .Done());
        }

        /// <summary>Queue a frame. Ordering is the queue's; delivery is one drain task at a time.</summary>
        internal void Send(string message)
        {
            if (string.IsNullOrEmpty(message) || !Alive) return;

            _outbox.Enqueue(message);
            _ = DrainAsync();
        }

        private async Task DrainAsync()
        {
            if (!await _sendGate.WaitAsync(0).ConfigureAwait(false)) return;   // another drain is already running

            try
            {
                while (_outbox.TryDequeue(out string message))
                {
                    if (_ws.State != WebSocketState.Open) return;

                    byte[] bytes = Encoding.UTF8.GetBytes(message);
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Core.Log?.Msg("[Sideload/cdp] send failed, closing the session: " + e.Message);
                Close();
            }
            finally
            {
                try { _sendGate.Release(); } catch { /* disposed with the session */ }
            }

            // Anything queued between the last dequeue and releasing the gate would otherwise sit there until the
            // next send; picking the gate back up drains it.
            if (!_outbox.IsEmpty) _ = DrainAsync();
        }

        internal void Close()
        {
            try { _ws?.Abort(); } catch { /* already gone */ }
        }

        internal void Dispose()
        {
            Close();
            try { _sendGate.Dispose(); } catch { /* already disposed */ }
            Objects.Clear();
            Nodes.Clear();
        }
    }

    /// <summary>A protocol-level failure: answered as an `error` member, never as a dropped connection.</summary>
    internal sealed class CdpException : Exception
    {
        /// <summary>JSON-RPC's "no such method". The one DevTools expects for a domain an implementation does not
        /// have.</summary>
        internal const int MethodNotFound = -32601;

        internal const int InvalidParams = -32602;

        internal const int ServerError = -32000;

        internal CdpException(int code, string message) : base(message) => Code = code;

        internal int Code { get; }
    }
}
