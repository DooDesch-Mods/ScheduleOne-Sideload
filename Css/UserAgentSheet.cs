namespace Sideload.Css
{
    /// <summary>
    /// What a browser already thinks every element looks like, before a page says anything.
    ///
    /// A browser ships one of these and no author ever sees it, which is exactly why its absence is expensive: an
    /// `h1` is large, a `p` has room around it, a `ul` is indented and a `strong` is bold in every browser on earth,
    /// so nobody writes those rules down. Without them the same markup came out as a wall of identical 15px lines,
    /// and the only clue was that the page looked wrong.
    ///
    /// OPT-IN, behind `&lt;meta name="sideload" content="web-defaults"&gt;`, and that is not caution for its own
    /// sake. Fourteen shipped apps were written against a renderer with no user-agent styles, so switching this on
    /// underneath them would put a margin around every paragraph and an indent on every list in all of them at once.
    /// The meta already means "behave the way the web behaves", and this is part of that answer.
    ///
    /// The sheet is deliberately the HTML spec's own suggested rendering rather than a hand-picked house style, and
    /// only the parts this engine can express. What it leaves out it leaves out visibly: there are no list markers
    /// here because a marker is a generated box rather than a declaration (see DomBuilder), and no table rules
    /// because there is no table layout to hang them on.
    /// </summary>
    internal static class UserAgentSheet
    {
        /// <summary>
        /// Where these rules sit in the cascade: below every author rule, layered or not.
        ///
        /// <see cref="StyleRule.LayerRank"/> is zero for unlayered and negative for layered, so one number below
        /// every layer a page could declare puts this at the bottom without a second concept. Halved so that
        /// nothing overflows if a rank is ever added to another.
        /// </summary>
        internal const int Rank = int.MinValue / 2;

        private const string Source = @"
/* Block-level elements stack their children downwards, which is what being a block means. Needed because
   web-defaults makes an UNDECLARED box a row - right for a toolchain app, where a div without a flex class is
   a div the toolchain never meant to lay out, and wrong for every element HTML already calls a block. A list
   whose items sat side by side was the visible half of that. */
address, article, aside, blockquote, dd, details, div, dl, dt, fieldset, figcaption, figure, footer, form,
h1, h2, h3, h4, h5, h6, header, hgroup, li, main, nav, ol, p, pre, section, summary, ul {
  flex-direction: column;
}

h1 { font-size: 2em; font-weight: bold; margin: 0.67em 0 }
h2 { font-size: 1.5em; font-weight: bold; margin: 0.83em 0 }
h3 { font-size: 1.17em; font-weight: bold; margin: 1em 0 }
h4 { font-weight: bold; margin: 1.33em 0 }
h5 { font-size: 0.83em; font-weight: bold; margin: 1.67em 0 }
h6 { font-size: 0.67em; font-weight: bold; margin: 2.33em 0 }

p { margin: 1em 0 }
blockquote { margin: 1em 40px }
figure { margin: 1em 40px }
hr { margin: 0.5em 0; border-width: 1px 0 0 0; border-color: currentColor; opacity: 0.35 }

ul, ol, menu { margin: 1em 0; padding-left: 40px }
dl { margin: 1em 0 }
dd { margin-left: 40px }
li { display: list-item }

b, strong, th { font-weight: bold }
i, em, cite, var, dfn, address { font-style: italic }
u, ins { text-decoration: underline }
s, del, strike { text-decoration: line-through }
small { font-size: 0.8em }
mark { background-color: #ffff00; color: #000000 }

code, kbd, samp, pre, tt { font-family: monospace }
pre { white-space: pre; margin: 1em 0 }

a { text-decoration: underline }

fieldset { margin: 0 2px; padding: 0.35em 0.75em 0.625em; border: 1px solid currentColor }
legend { padding: 0 2px }

button, input, select, textarea { font-family: inherit; font-size: inherit }
textarea { white-space: pre-wrap }
";

        private static Stylesheet _parsed;

        /// <summary>
        /// The parsed sheet, built once per process. Cached because the text never changes and every page would
        /// otherwise pay the parse - and because the rules are read-only from here on, so one copy is enough.
        /// </summary>
        internal static Stylesheet Rules
        {
            get
            {
                if (_parsed != null) return _parsed;

                Stylesheet sheet = CssParser.Parse(Source);
                foreach (StyleRule rule in sheet.Rules) rule.LayerRank = Rank;

                return _parsed = sheet;
            }
        }
    }
}
