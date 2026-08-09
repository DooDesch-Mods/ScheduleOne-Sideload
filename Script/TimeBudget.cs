using Jint;

namespace Sideload.Script
{
    /// <summary>
    /// How long a single stretch of JavaScript may run before the engine stops it.
    ///
    /// Jint's own <c>TimeoutInterval</c> is fixed at construction, and one number cannot serve both jobs this host
    /// has. A handler must be short: it runs inside a frame, and a mistake there should cost one hitch rather than
    /// the session. Loading a page is a different shape of work - a bundled framework evaluates its modules and
    /// performs its first render in ONE call, and a tree large enough to matter walks straight past what a handler
    /// is allowed. The page then goes dark with a timeout, on a machine where the same bundle is fine in a browser.
    ///
    /// So the limit is a field rather than a constant, and <see cref="Ceiling"/> is raised around the initial
    /// evaluation and put back afterwards.
    /// </summary>
    internal sealed class TimeBudget : Constraint
    {
        /// <summary>Long enough for any honest page update, short enough that a mistake shows up as one hitched frame
        /// instead of a hung game.</summary>
        internal static readonly TimeSpan Handler = TimeSpan.FromMilliseconds(250);

        /// <summary>What loading a page gets. Parsing is cached and happens outside the engine, so this covers module
        /// evaluation plus the framework's first render - the two things that legitimately take longer than a frame
        /// and happen exactly once.</summary>
        internal static readonly TimeSpan Load = TimeSpan.FromSeconds(5);

        private DateTime _deadline = DateTime.MaxValue;

        /// <summary>The limit the NEXT run gets. Changing it mid-run does nothing; the deadline is stamped at
        /// <see cref="Reset"/>, which the engine calls as it enters.</summary>
        internal TimeSpan Ceiling { get; set; } = Handler;

        public override void Reset() => _deadline = DateTime.UtcNow + Ceiling;

        public override void Check()
        {
            if (DateTime.UtcNow <= _deadline) return;

            // The same exception Jint's built-in time constraint raises, so every caller that already knows how to
            // report one keeps working.
            throw new TimeoutException($"script ran longer than {Ceiling.TotalMilliseconds:0} ms");
        }

        /// <summary>
        /// Run something on the larger ceiling and put the handler ceiling back afterwards.
        ///
        /// <see cref="Jint.Engine.Constraints"/>.Reset() is called first because the engine only stamps a deadline
        /// when it ENTERS a call, and a nested run - a page whose script calls back into the host - would otherwise
        /// inherit the deadline of the call it is inside.
        /// </summary>
        internal void During(Engine engine, TimeSpan ceiling, Action run)
        {
            TimeSpan before = Ceiling;
            Ceiling = ceiling;

            try
            {
                engine?.Constraints.Reset();
                run();
            }
            finally
            {
                Ceiling = before;
                engine?.Constraints.Reset();
            }
        }
    }
}
