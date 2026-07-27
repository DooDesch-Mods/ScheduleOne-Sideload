using System.Text;
using Sideload.Css;

namespace Sideload.Devtools.Cdp
{
    /// <summary>A line/column span in a stylesheet's text. Both are zero-based, as the protocol has them.</summary>
    internal readonly struct SourceSpan
    {
        internal SourceSpan(int startLine, int startColumn, int endLine, int endColumn)
        {
            StartLine = startLine;
            StartColumn = startColumn;
            EndLine = endLine;
            EndColumn = endColumn;
        }

        internal int StartLine { get; }

        internal int StartColumn { get; }

        internal int EndLine { get; }

        internal int EndColumn { get; }

        internal string ToJson() =>
            new Json.Obj()
                .Num("startLine", StartLine)
                .Num("startColumn", StartColumn)
                .Num("endLine", EndLine)
                .Num("endColumn", EndColumn)
                .Done();
    }

    /// <summary>
    /// One rule as the protocol sees it: a selector list, one set of declarations, and where each part sits in the
    /// text.
    ///
    /// The parser splits `.a, .b { ... }` into two <see cref="StyleRule"/>s that share one declaration list, because
    /// the cascade wants one entry per selector. The protocol wants the opposite - one rule carrying a selector list,
    /// with the matching entries marked - so the two are folded back together here, by declaration-list identity.
    /// </summary>
    internal sealed class SourceRule
    {
        internal readonly List<StyleRule> Variants = new List<StyleRule>();

        internal readonly List<SourceSpan> SelectorSpans = new List<SourceSpan>();

        internal readonly List<SourceSpan> DeclarationSpans = new List<SourceSpan>();

        internal List<Declaration> Declarations;

        internal Orientation? Media;

        internal SourceSpan BodySpan;

        internal string SelectorText = "";
    }

    /// <summary>
    /// A stylesheet as text, rebuilt from the parsed rules.
    ///
    /// The parser keeps no source text - it is a scanner that produces rules and drops everything else - so the
    /// original bytes are not available to hand to the inspector. Serialising the rules back out gives a stylesheet
    /// that is faithful in content (same selectors, same declarations, same order) and whose ranges are exact by
    /// construction, because the same pass that writes the text records them. What it does not preserve is the
    /// author's formatting, comments, or the file each rule came from.
    /// </summary>
    internal sealed class SheetModel
    {
        internal string Id { get; private set; }

        /// <summary>The sheet this was built from, kept for identity: a page rebuilt from disk gets a new one, and
        /// every id handed out for the old one is then stale.</summary>
        internal Stylesheet Sheet { get; private set; }

        internal string Text { get; private set; } = "";

        internal List<SourceRule> Rules { get; } = new List<SourceRule>();

        internal int LineCount { get; private set; }

        internal static SheetModel Build(Stylesheet sheet, string id)
        {
            var model = new SheetModel { Id = id, Sheet = sheet };
            if (sheet == null) return model;

            var writer = new Writer();

            foreach (SourceRule rule in Group(sheet))
            {
                model.Rules.Add(rule);
                Emit(writer, rule);
            }

            model.Text = writer.Text;
            model.LineCount = writer.Line;
            return model;
        }

        /// <summary>Folds the parser's per-selector rules back into one rule per source declaration block.</summary>
        private static List<SourceRule> Group(Stylesheet sheet)
        {
            var grouped = new List<SourceRule>();
            SourceRule current = null;

            foreach (StyleRule rule in sheet.Rules)
            {
                // Reference equality on the declaration list is what marks two rules as having come from the same
                // block: the parser hands the very same list to every selector it split off.
                bool sameBlock = current != null
                                 && ReferenceEquals(current.Declarations, rule.Declarations)
                                 && Nullable.Equals(current.Media, rule.Media);

                if (!sameBlock)
                {
                    current = new SourceRule { Declarations = rule.Declarations, Media = rule.Media };
                    grouped.Add(current);
                }

                current.Variants.Add(rule);
            }

            return grouped;
        }

        private static void Emit(Writer writer, SourceRule rule)
        {
            string indent = "";

            if (rule.Media.HasValue)
            {
                writer.Write("@media (orientation: " + (rule.Media.Value == Orientation.Portrait ? "portrait" : "landscape") + ") {\n");
                indent = "  ";
            }

            writer.Write(indent);

            var selectors = new StringBuilder();
            for (int i = 0; i < rule.Variants.Count; i++)
            {
                if (i > 0) { writer.Write(", "); selectors.Append(", "); }

                string selector = rule.Variants[i].Selector ?? "";
                int line = writer.Line, column = writer.Column;
                writer.Write(selector);
                rule.SelectorSpans.Add(new SourceSpan(line, column, writer.Line, writer.Column));
                selectors.Append(selector);
            }

            rule.SelectorText = selectors.ToString();

            writer.Write(" {\n");
            int bodyLine = writer.Line, bodyColumn = writer.Column;

            foreach (Declaration declaration in rule.Declarations ?? new List<Declaration>())
            {
                writer.Write(indent + "  ");

                int line = writer.Line, column = writer.Column;
                writer.Write(DeclarationText(declaration));
                rule.DeclarationSpans.Add(new SourceSpan(line, column, writer.Line, writer.Column));

                writer.Write("\n");
            }

            rule.BodySpan = new SourceSpan(bodyLine, bodyColumn, writer.Line, writer.Column);

            writer.Write(indent + "}\n");
            if (rule.Media.HasValue) writer.Write("}\n");
            writer.Write("\n");
        }

        /// <summary>The text a span covers. Used for a rule's `cssText`, so what the inspector is told is a rule's
        /// body is literally the bytes at the range it was given.</summary>
        internal string Slice(SourceSpan span)
        {
            int start = Offset(span.StartLine, span.StartColumn);
            int end = Offset(span.EndLine, span.EndColumn);

            if (start < 0 || end < start || end > Text.Length) return "";
            return Text.Substring(start, end - start);
        }

        private int Offset(int line, int column)
        {
            int offset = 0;
            for (int i = 0; i < line; i++)
            {
                int next = Text.IndexOf('\n', offset);
                if (next < 0) return -1;
                offset = next + 1;
            }

            return Math.Min(offset + column, Text.Length);
        }

        /// <summary>One declaration as it is written in a stylesheet, semicolon included - what the inspector shows
        /// on the row and what it edits.</summary>
        internal static string DeclarationText(Declaration declaration) =>
            declaration.Property + ": " + declaration.Value + (declaration.Important ? " !important" : "") + ";";

        /// <summary>Appends text while keeping track of where it lands, which is the only reason the ranges the
        /// protocol wants are exact rather than estimated.</summary>
        private sealed class Writer
        {
            private readonly StringBuilder _sb = new StringBuilder();

            internal int Line { get; private set; }

            internal int Column { get; private set; }

            internal string Text => _sb.ToString();

            internal void Write(string text)
            {
                if (string.IsNullOrEmpty(text)) return;

                foreach (char c in text)
                {
                    if (c == '\n') { Line++; Column = 0; }
                    else Column++;
                }

                _sb.Append(text);
            }
        }
    }
}
