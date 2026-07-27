using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The one place a socket thread is allowed to touch the game.
    ///
    /// Every protocol command arrives on a thread pool thread, and everything a command wants - a WebView, its
    /// AngleSharp document, the page's Jint engine - is single-threaded by construction and lives on Unity's main
    /// thread. So the socket thread hands over a closure and blocks on it: <see cref="Pump"/> runs the closure from
    /// the mod's update loop and releases the waiter with the result.
    ///
    /// The wait is bounded. If the game is not pumping (still loading, or hung) the command fails with a timeout the
    /// developer can see in DevTools, rather than parking a pool thread forever.
    /// </summary>
    internal static class MainThread
    {
        /// <summary>How long a command may wait to be run. Generous next to a frame, short enough that a stalled game
        /// reports itself instead of looking like a broken inspector.</summary>
        internal const int DefaultTimeoutMs = 5000;

        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        /// <summary>Run a closure on the main thread and wait for what it returns. Anything the closure throws is
        /// rethrown here, on the calling thread, with its stack intact.</summary>
        internal static T Run<T>(Func<T> work, int timeoutMs = DefaultTimeoutMs)
        {
            if (work == null) return default;

            T result = default;
            ExceptionDispatchInfo failure = null;

            using var done = new ManualResetEventSlim(false);
            _queue.Enqueue(() =>
            {
                try { result = work(); }
                catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
                finally { try { done.Set(); } catch { /* already disposed after a timeout */ } }
            });

            if (!done.Wait(timeoutMs))
                throw new TimeoutException($"the game did not run the command within {timeoutMs} ms - is it paused or still loading?");

            failure?.Throw();
            return result;
        }

        /// <summary>Hand work to the main thread without waiting for it. For anything whose result nobody reads.</summary>
        internal static void Post(Action work)
        {
            if (work != null) _queue.Enqueue(work);
        }

        /// <summary>Run everything queued up to now. Called once per frame from the mod's update loop.
        ///
        /// The batch size is fixed at entry on purpose: a closure that queues more work must not be able to keep this
        /// loop running past the end of the frame.</summary>
        internal static void Pump()
        {
            int budget = _queue.Count;

            while (budget-- > 0 && _queue.TryDequeue(out Action work))
            {
                try { work(); }
                catch (Exception e) { Core.Log?.Warning("[Sideload/cdp] a queued command failed: " + e.Message); }
            }
        }

        /// <summary>Drop whatever is still queued. Used when the server stops, so pending closures do not run against
        /// a torn-down server on the next frame; the waiters time out and report it.</summary>
        internal static void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
