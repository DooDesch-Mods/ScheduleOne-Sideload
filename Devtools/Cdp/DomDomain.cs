using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jint.Native;
using Sideload.Host;
using Sideload.Script;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The nodes one DevTools window has been told about.
    ///
    /// The protocol addresses nodes by a number the server hands out, so both directions of the mapping are kept.
    /// Ids are per session: two windows may number the same document differently, and closing one must not disturb
    /// the other.
    /// </summary>
    internal sealed class NodeStore
    {
        private readonly Dictionary<int, INode> _byId = new Dictionary<int, INode>();
        private readonly Dictionary<INode, int> _byNode = new Dictionary<INode, int>();
        private int _next = 1;

        internal int IdOf(INode node)
        {
            if (node == null) return 0;
            if (_byNode.TryGetValue(node, out int existing)) return existing;

            int id = _next++;
            _byNode[node] = id;
            _byId[id] = node;
            return id;
        }

        internal bool Knows(INode node) => node != null && _byNode.ContainsKey(node);

        internal INode Get(int id) => _byId.TryGetValue(id, out INode node) ? node : null;

        /// <summary>Forget everything. Called when the document underneath is replaced, which makes every id stale.</summary>
        internal void Clear()
        {
            _byId.Clear();
            _byNode.Clear();
            _next = 1;
        }
    }

    /// <summary>
    /// The DOM domain: the Elements panel, reading and editing the page's real AngleSharp document.
    ///
    /// Editing goes through the same path the page's own script uses - change the document, then mark the view dirty
    /// so the renderer rebuilds it on the next frame. That is why an attribute typed into DevTools shows up in the
    /// game: nothing here draws anything, it only edits the document the renderer already reads.
    ///
    /// What the Elements panel cannot show is the Styles sidebar. That needs the CSS domain (getMatchedStylesForNode,
    /// getComputedStyleForNode), which is not implemented here.
    /// </summary>
    internal static class DomDomain
    {
        /// <summary>How deep an unlimited request actually goes. A page is nowhere near this deep, and it means a
        /// malformed depth cannot turn into a stack overflow.</summary>
        private const int MaxDepth = 32;

        internal static string GetDocument(CdpSession session, JsonValue args)
        {
            IDocument document = DocumentOf(session);

            // The frontend asks again after every documentUpdated, so the old numbering is thrown away here rather
            // than left to accumulate.
            session.Nodes.Clear();

            // The whole tree unless a depth was asked for. DevTools sends this with no depth at all and then never
            // asks for the children it is missing - a browser backend pushes them unprompted, and the Elements panel
            // is simply empty below the first level without them. A Sideload page is one phone screen, so handing
            // over all of it costs nothing.
            int depth = Depth(args, MaxDepth);
            return new Json.Obj().Raw("root", NodeJson(session, document, depth)).Done();
        }

        internal static string RequestChildNodes(CdpSession session, JsonValue args)
        {
            INode node = NodeOf(session, args);
            int depth = Depth(args, 1);

            var children = new List<string>();
            foreach (INode child in Visible(node)) children.Add(NodeJson(session, child, depth - 1));

            session.EmitAfterReply("DOM.setChildNodes", new Json.Obj()
                .Num("parentId", session.Nodes.IdOf(node))
                .Raw("nodes", Json.Array(children))
                .Done());

            return Json.EmptyObject;
        }

        internal static string GetOuterHtml(CdpSession session, JsonValue args)
        {
            INode node = NodeOf(session, args);

            string html = node switch
            {
                IElement element => element.OuterHtml,
                IDocument document => document.DocumentElement?.OuterHtml ?? "",
                _ => node.TextContent ?? "",
            };

            return new Json.Obj().Str("outerHTML", html).Done();
        }

        internal static string SetOuterHtml(CdpSession session, JsonValue args)
        {
            INode node = NodeOf(session, args);
            string html = args["outerHTML"].AsString();

            if (node is not IElement element)
                throw new CdpException(CdpException.InvalidParams, "only an element can have its outer HTML replaced");

            element.OuterHtml = html;

            // The element that was there is gone and its subtree with it, so every id handed out is meaningless.
            session.Nodes.Clear();
            Dirty(session);
            session.EmitAfterReply("DOM.documentUpdated", Json.EmptyObject);

            return Json.EmptyObject;
        }

        internal static string SetAttributeValue(CdpSession session, JsonValue args)
        {
            IElement element = ElementOf(session, args);
            string name = args["name"].AsString();
            if (string.IsNullOrEmpty(name)) throw new CdpException(CdpException.InvalidParams, "name is empty");

            element.SetAttribute(name, args["value"].AsString());
            Dirty(session);

            session.EmitAfterReply("DOM.attributeModified", new Json.Obj()
                .Num("nodeId", session.Nodes.IdOf(element))
                .Str("name", name)
                .Str("value", args["value"].AsString())
                .Done());

            return Json.EmptyObject;
        }

        /// <summary>
        /// The whole attribute text of an element, which is what the Elements panel sends when a name is edited or an
        /// attribute is deleted. Parsed by giving it to the HTML parser as a tag, so quoting rules are the parser's
        /// and not a hand-rolled approximation of them.
        /// </summary>
        internal static string SetAttributesAsText(CdpSession session, JsonValue args)
        {
            IElement element = ElementOf(session, args);
            string text = args["text"].AsString();
            string replaced = args["name"].AsString();

            IElement parsed = new HtmlParser()
                .ParseDocument("<span " + text + "></span>")
                .QuerySelector("span");

            if (parsed == null) throw new CdpException(CdpException.InvalidParams, "the attribute text could not be parsed");

            // An edit that renames or removes an attribute sends the old name; it goes first, so re-adding it under
            // the new spelling still works.
            if (!string.IsNullOrEmpty(replaced)) element.RemoveAttribute(replaced);

            foreach (IAttr attribute in parsed.Attributes)
                element.SetAttribute(attribute.Name, attribute.Value);

            Dirty(session);
            session.EmitAfterReply("DOM.documentUpdated", Json.EmptyObject);

            return Json.EmptyObject;
        }

        internal static string RemoveNode(CdpSession session, JsonValue args)
        {
            INode node = NodeOf(session, args);
            INode parent = node.Parent
                ?? throw new CdpException(CdpException.InvalidParams, "the document itself cannot be removed");

            int nodeId = session.Nodes.IdOf(node);
            int parentId = session.Nodes.IdOf(parent);

            parent.RemoveChild(node);
            Dirty(session);

            session.EmitAfterReply("DOM.childNodeRemoved", new Json.Obj()
                .Num("parentNodeId", parentId)
                .Num("nodeId", nodeId)
                .Done());

            return Json.EmptyObject;
        }

        internal static string SetNodeValue(CdpSession session, JsonValue args)
        {
            INode node = NodeOf(session, args);
            string value = args["value"].AsString();

            node.NodeValue = value;
            Dirty(session);

            session.EmitAfterReply("DOM.characterDataModified", new Json.Obj()
                .Num("nodeId", session.Nodes.IdOf(node))
                .Str("characterData", value)
                .Done());

            return Json.EmptyObject;
        }

        internal static string DescribeNode(CdpSession session, JsonValue args) =>
            new Json.Obj().Raw("node", NodeJson(session, NodeOf(session, args), 0)).Done();

        /// <summary>
        /// Hand the console a JavaScript handle on a node - what `$0` is. The wrapper is the page's own, so the
        /// handle behaves exactly like an element the page's script got from `querySelector`, mutations included.
        /// </summary>
        internal static string ResolveNode(CdpSession session, JsonValue args)
        {
            INode node = NodeOf(session, args);
            if (node is not IElement element)
                throw new CdpException(CdpException.InvalidParams, "only an element can be resolved to a value");

            ScriptHost script = RuntimeDomain.ScriptOf(session);
            JsValue value = JsValue.FromObject(script.Engine, script.Wrap(element));

            return new Json.Obj()
                .Raw("object", Remote.Describe(value, session.Objects, args["objectGroup"].AsString("console")))
                .Done();
        }

        // ------------------------------------------------------------------ shapes --

        private static string NodeJson(CdpSession session, INode node, int depth)
        {
            int id = session.Nodes.IdOf(node);

            var obj = new Json.Obj()
                .Num("nodeId", id)
                .Num("backendNodeId", id)
                .Num("nodeType", (int)node.NodeType)
                .Str("nodeName", node.NodeName ?? "")
                .Str("localName", node is IElement element ? element.LocalName : "")
                .Str("nodeValue", node.NodeValue ?? "");

            if (node.Parent != null && session.Nodes.Knows(node.Parent))
                obj.Num("parentId", session.Nodes.IdOf(node.Parent));

            var children = new List<INode>(Visible(node));
            obj.Num("childNodeCount", children.Count);

            if (node is IElement withAttributes) obj.Raw("attributes", AttributesJson(withAttributes));

            if (node is IDocument)
            {
                // The frame id is not decoration: the frontend binds a document to the frame it is inspecting by
                // this field, and a document that names no frame leaves the Elements tree empty.
                string url = Targets.UrlOf(Targets.Find(session.TargetId));
                obj.Str("documentURL", url)
                   .Str("baseURL", url)
                   .Str("xmlVersion", "")
                   .Str("frameId", Targets.FrameOf(session.TargetId));
            }

            if (node is IDocumentType doctype)
                obj.Str("publicId", doctype.PublicIdentifier ?? "").Str("systemId", doctype.SystemIdentifier ?? "");

            if (depth != 0 && children.Count > 0)
            {
                var encoded = new List<string>();
                foreach (INode child in children) encoded.Add(NodeJson(session, child, depth - 1));
                obj.Raw("children", Json.Array(encoded));
            }

            return obj.Done();
        }

        private static string AttributesJson(IElement element)
        {
            // A flat [name, value, name, value] list, which is how the protocol spells attributes.
            var flat = new List<string>();
            foreach (IAttr attribute in element.Attributes)
            {
                flat.Add(Json.Quote(attribute.Name));
                flat.Add(Json.Quote(attribute.Value ?? ""));
            }

            return Json.Array(flat);
        }

        /// <summary>
        /// The children worth showing. Whitespace between tags is a text node in the document and noise in the tree;
        /// it is filtered here, and the reported child count is filtered the same way so the frontend never asks for
        /// a child that is not coming.
        /// </summary>
        private static IEnumerable<INode> Visible(INode node)
        {
            foreach (INode child in node.ChildNodes)
            {
                if (child.NodeType == NodeType.Text && string.IsNullOrWhiteSpace(child.TextContent)) continue;
                yield return child;
            }
        }

        private static int Depth(JsonValue args, int fallback)
        {
            int depth = args["depth"].AsInt(fallback);
            return depth < 0 || depth > MaxDepth ? MaxDepth : depth;
        }

        internal static IDocument DocumentOf(CdpSession session)
        {
            WebView view = ViewOf(session);

            return view.Document
                ?? throw new CdpException(CdpException.ServerError,
                    "the page has not been built yet - open the app on the phone first");
        }

        internal static WebView ViewOf(CdpSession session) =>
            Targets.Find(session.TargetId)
            ?? throw new CdpException(CdpException.ServerError, $"the page '{session.TargetId}' is no longer mounted");

        internal static INode NodeOf(CdpSession session, JsonValue args)
        {
            int id = args["nodeId"].AsInt(args["backendNodeId"].AsInt(0));
            if (id <= 0) throw new CdpException(CdpException.InvalidParams, "nodeId is missing");

            return session.Nodes.Get(id)
                ?? throw new CdpException(CdpException.InvalidParams,
                    $"node {id} is not known to this session - the document may have been reloaded");
        }

        internal static IElement ElementOf(CdpSession session, JsonValue args) =>
            NodeOf(session, args) as IElement
            ?? throw new CdpException(CdpException.InvalidParams, "that node is not an element");

        /// <summary>
        /// Tell the view its document changed, through the same path a script mutation takes: the render is queued
        /// for the next frame, so twenty edits in a row still cost one rebuild.
        /// </summary>
        private static void Dirty(CdpSession session) => ViewOf(session).MarkDirty();
    }
}
