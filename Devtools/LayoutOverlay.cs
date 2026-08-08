using System.Text;
using Sideload.Layout;

namespace Sideload.Devtools
{
    /// <summary>
    /// Debug-only layout inspector: an outline around every box plus a textual dump of the computed rectangles.
    ///
    /// The outline answers the question a screenshot cannot - where a box actually ENDS, as opposed to where its
    /// background happens to be painted. A container with no background is invisible until it is wrong.
    /// </summary>
    internal static class LayoutOverlay
    {
        /// <summary>Draw a magenta hairline around every box.</summary>
        internal static bool Outlines = false;   // off by default - switched on from the dev overlay

        /// <summary>Write the computed tree to the log after every build.</summary>
        internal static bool DumpTree = true;

        /// <summary>
        /// Force an absurdly small clip rectangle. Used once to prove that _ClipRect never reaches the shader at all:
        /// with (0,0,1,1) nothing disappeared, so the problem is the plumbing, not the coordinate space.
        /// </summary>
        internal static bool ClipProbe = false;

        internal static void Dump(LayoutNode root, float viewportWidth, float viewportHeight, float hostWidth, float hostHeight, float scale)
        {
            if (!DumpTree || root == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("[Sideload/layout] --- computed tree ---");
            sb.AppendLine($"[Sideload/layout] host rect    : {hostWidth:0.##} x {hostHeight:0.##} device units");
            sb.AppendLine($"[Sideload/layout] css viewport : {viewportWidth:0.##} x {viewportHeight:0.##} at {scale:0.####}x");
            sb.AppendLine($"[Sideload/layout] back-check   : {viewportWidth * scale:0.##} x {viewportHeight * scale:0.##} device units");

            Walk(root, 0, sb);
            Core.Log?.Msg(sb.ToString().TrimEnd());
        }

        private static void Walk(LayoutNode node, int depth, StringBuilder sb)
        {
            if (depth > 8) return;

            string indent = new string(' ', depth * 2);
            string name = Describe(node);
            sb.AppendLine($"[Sideload/layout] {indent}{name}  x={node.X:0.##} y={node.Y:0.##} w={node.Width:0.##} h={node.Height:0.##}" +
                          $"  right={node.X + node.Width:0.##} bottom={node.Y + node.Height:0.##}");

            // The compiled rich text, verbatim and in quotes: whitespace bugs are invisible in a screenshot but
            // obvious the moment the exact string is on screen.
            if (node.IsTextLeaf)
            {
                string preview = node.Text.Replace("\n", "\\n");
                if (preview.Length > 120) preview = preview.Substring(0, 120) + "...";
                sb.AppendLine($"[Sideload/layout] {indent}  text=\"{preview}\"");
            }

            foreach (LayoutNode child in node.Children) Walk(child, depth + 1, sb);
        }

        private static string Describe(LayoutNode node)
        {
            if (node.Tag is AngleSharp.Dom.IElement element)
            {
                string cls = element.GetAttribute("class");
                string tag = element.LocalName;
                if (node.IsTextLeaf) tag += "(text)";
                return string.IsNullOrEmpty(cls) ? tag : tag + "." + cls.Replace(' ', '.');
            }
            return node.IsTextLeaf ? "(text)" : "(box)";
        }
    }
}
