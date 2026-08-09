using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace Sideload.Script
{
    /// <summary>
    /// The DOM as JavaScript sees it: thin wrappers around AngleSharp nodes.
    ///
    /// The wrappers exist rather than handing AngleSharp straight to the engine for one reason: every mutation has to
    /// tell the renderer that the page is stale. Routing all writes through here means a script cannot change the
    /// document behind the renderer's back, which is what makes "JS owns the DOM" workable without AngleSharp's
    /// mutation observers.
    ///
    /// They derive from Jint's <see cref="ObjectInstance"/> rather than being plain CLR objects, and that is not a
    /// detail. A CLR wrapper exposes exactly the members its type declares and refuses every other name, so
    /// `node.__reactFiber$abc = fiber` throws - and hanging bookkeeping on the node it belongs to is how every
    /// virtual-DOM renderer written since 2015 works. Preact writes `_listeners`, React writes two fibre keys per
    /// node. With a sealed wrapper neither can mount at all. <see cref="JsStyle"/> took this route first and for the
    /// same reason.
    ///
    /// So: known DOM members are answered here, and anything else falls through to the ordinary property bag, which
    /// is what a browser's DOM node does too.
    /// </summary>
    public abstract class JsNode : ObjectInstance
    {
        internal JsNode(ScriptHost host, INode node) : base(host.Engine)
        {
            Host = host;
            Node = node;

            // Puts this wrapper on the DOM's own type chain, so `instanceof` answers the way a browser answers it.
            // See DomTypes for what one missing name costs.
            ObjectInstance proto = Interface(host.Types);
            if (proto != null) base.Prototype = proto;
        }

        /// <summary>Which of the DOM interfaces this node is. Null leaves the plain object prototype in place.</summary>
        private protected virtual ObjectInstance Interface(DomTypes types) => types?.Node;

        internal ScriptHost Host { get; }

        internal INode Node { get; }

        // --------------------------------------------------------------- the property protocol --

        public override JsValue Get(JsValue property, JsValue receiver)
        {
            if (!property.IsString()) return base.Get(property, receiver);

            string name = property.AsString();

            JsValue member = Member(name);
            if (member is not null) return member;

            Func<JsValue[], JsValue> method = Method(name);
            if (method != null) return Bind(name, method);

            return base.Get(property, receiver);
        }

        public override bool Set(JsValue property, JsValue value, JsValue receiver)
        {
            if (property.IsString() && Assign(property.AsString(), value)) return true;
            return base.Set(property, value, receiver);
        }

        public override bool HasProperty(JsValue property)
        {
            if (property.IsString())
            {
                string name = property.AsString();
                if (Member(name) is not null || Method(name) != null) return true;
            }

            return base.HasProperty(property);
        }

        /// <summary>
        /// A DOM member whose value is read fresh every time, or null when this object has no such member.
        ///
        /// C# null means "not a member"; a member that is genuinely absent - `parentNode` on a detached node -
        /// answers <see cref="JsValue.Null"/>. Conflating the two would make `'parentNode' in node` false for every
        /// node that has not been inserted yet, and that is the exact moment a renderer asks.
        /// </summary>
        private protected virtual JsValue Member(string name)
        {
            switch (name)
            {
                case "nodeType": return NodeTypeCode;
                case "nodeName": return NodeName;
                case "nodeValue": return NodeValue;
                case "textContent": return Node.TextContent ?? "";
                case "parentNode": return Wrap(Node.Parent);
                case "parentElement": return Wrap(Node.ParentElement);
                case "ownerDocument": return Host.DocumentObject;
                case "isConnected": return Node.Owner != null && Node.Parent != null;
                case "firstChild": return Wrap(Node.FirstChild);
                case "lastChild": return Wrap(Node.LastChild);
                case "nextSibling": return Wrap(Node.NextSibling);
                case "previousSibling": return Wrap(Node.PreviousSibling);
                case "childNodes": return Host.WrapNodes(Node.ChildNodes);
                default: return null;
            }
        }

        private protected virtual bool Assign(string name, JsValue value)
        {
            switch (name)
            {
                case "nodeValue":
                case "textContent":
                    SetText(Text(value));
                    return true;

                default: return false;
            }
        }

        private protected virtual Func<JsValue[], JsValue> Method(string name)
        {
            switch (name)
            {
                case "appendChild": return a => Insert(Arg(a, 0), null, append: true);
                case "insertBefore": return a => Insert(Arg(a, 0), Arg(a, 1), append: false);
                case "removeChild": return a => Detach(Arg(a, 0));
                case "replaceChild": return a => Replace(Arg(a, 0), Arg(a, 1));
                case "remove": return _ => { DetachSelf(); return JsValue.Undefined; };
                case "cloneNode": return a => Clone(a.Length > 0 && TypeConverter.ToBoolean(a[0]));
                case "contains": return a => Contains(Arg(a, 0));
                case "hasChildNodes": return _ => Node.ChildNodes.Length > 0;

                case "addEventListener": return a =>
                {
                    Host.AddListener(Node, a.Length > 0 ? a[0].ToString() : null, a.Length > 1 ? a[1] : null,
                                     Capture(a, 2));
                    return JsValue.Undefined;
                };

                case "removeEventListener": return a =>
                {
                    Host.RemoveListener(Node, a.Length > 0 ? a[0].ToString() : null, a.Length > 1 ? a[1] : null,
                                        Capture(a, 2));
                    return JsValue.Undefined;
                };

                case "dispatchEvent": return a => Host.DispatchFromScript(Node, Arg(a, 0) as ObjectInstance
                                                                                ?? (a.Length > 0 ? a[0] as ObjectInstance : null));

                default: return null;
            }
        }

        // --------------------------------------------------------------- what each kind of node is --

        private protected abstract int NodeTypeCode { get; }

        private protected abstract string NodeName { get; }

        /// <summary>Null for an element, the text for everything else - as the DOM has it.</summary>
        private protected virtual JsValue NodeValue => JsValue.Null;

        private protected virtual void SetText(string text)
        {
            if (string.Equals(Node.TextContent ?? "", text, StringComparison.Ordinal)) return;

            Node.TextContent = text;
            Host.MarkDirty();
        }

        // --------------------------------------------------------------- tree surgery --

        private JsValue Insert(JsNode child, JsNode reference, bool append)
        {
            if (child == null) return JsValue.Null;

            // A node that is being moved leaves its old parent first. AngleSharp does that itself, but the renderer
            // is told either way - the old parent's box has to go even when the new one is off-document.
            if (append || reference == null) Node.AppendChild(child.Node);
            else Node.InsertBefore(child.Node, reference.Node);

            Host.MarkDirty();
            return child;
        }

        private JsValue Detach(JsNode child)
        {
            if (child == null) return JsValue.Null;

            Node.RemoveChild(child.Node);
            Host.Forget(child.Node);
            Host.MarkDirty();
            return child;
        }

        private JsValue Replace(JsNode fresh, JsNode stale)
        {
            if (fresh == null || stale == null) return JsValue.Null;

            Node.ReplaceChild(fresh.Node, stale.Node);
            Host.Forget(stale.Node);
            Host.MarkDirty();
            return stale;
        }

        private void DetachSelf()
        {
            Node.Parent?.RemoveChild(Node);
            Host.Forget(Node);
            Host.MarkDirty();
        }

        private JsValue Clone(bool deep)
        {
            INode copy = Node.Clone(deep);
            return copy == null ? JsValue.Null : Wrap(copy);
        }

        private JsValue Contains(JsNode other)
        {
            if (other == null) return false;

            for (INode walk = other.Node; walk != null; walk = walk.Parent)
                if (ReferenceEquals(walk, Node)) return true;

            return false;
        }

        // --------------------------------------------------------------- plumbing --

        private protected JsValue Wrap(INode node) => (JsValue)Host.WrapNode(node) ?? JsValue.Null;

        private protected JsNode Arg(JsValue[] args, int index) =>
            index < args.Length ? args[index] as JsNode : null;

        /// <summary>
        /// The third argument of addEventListener: `true`, or `{ capture: true }`. Both spellings are in wide use -
        /// a hand-written page passes the boolean, a bundled framework passes the object - and a host that read only
        /// one of them would register half the capturing listeners on the bubble phase, where they fire in the wrong
        /// order rather than not at all.
        /// </summary>
        private static bool Capture(JsValue[] args, int index)
        {
            if (index >= args.Length) return false;

            JsValue options = args[index];
            if (options.IsBoolean()) return options.AsBoolean();

            if (options is ObjectInstance bag)
            {
                JsValue capture = bag.Get("capture");
                return !capture.IsUndefined() && TypeConverter.ToBoolean(capture);
            }

            return false;
        }

        private protected static string Text(JsValue value) =>
            value.IsNull() || value.IsUndefined() ? "" : value.ToString();

        /// <summary>
        /// Hand back the same function object every time. `node.addEventListener` is compared against itself by
        /// enough library code that building a fresh closure per read is a behaviour difference, not a cost worry -
        /// and the property bag is where a page's own override of the same name would land anyway, so caching there
        /// means an override wins from the second read exactly as it does in a browser.
        /// </summary>
        private JsValue Bind(string name, Func<JsValue[], JsValue> body)
        {
            PropertyDescriptor own = GetOwnProperty(name);
            if (own != PropertyDescriptor.Undefined) return own.Value;

            var function = new ClrFunction(Engine, name, (_, args) => body(args));
            FastSetProperty(name, new PropertyDescriptor(function, writable: true, enumerable: false, configurable: true));
            return function;
        }
    }

    /// <summary>An element node. Everything a page reaches for by name lives here.</summary>
    public sealed class JsElement : JsNode
    {
        internal JsElement(ScriptHost host, IElement element) : base(host, element) => Native = element;

        internal IElement Native { get; }

        // Reads the base's node rather than `Native`: this runs from the base constructor, before this class's own
        // field is assigned. With `Native` here every element came out a plain HTMLElement and `instanceof
        // HTMLInputElement` was false for every input on the page.
        private protected override ObjectInstance Interface(DomTypes types) =>
            types?.For((Node as IElement)?.LocalName);

        private protected override int NodeTypeCode => 1;

        /// <summary>Upper case, as a browser reports it for an HTML element. `localName` is the lower-case spelling;
        /// a page that compares against a lower-case literal wants that one.</summary>
        private protected override string NodeName => Native.LocalName?.ToUpperInvariant() ?? "";

        public override string ToString() => "<" + Native.LocalName + ">";

        private protected override JsValue Member(string name)
        {
            switch (name)
            {
                case "tagName": return NodeName;
                case "localName": return Native.LocalName ?? "";
                case "id": return Native.Id ?? "";
                case "className": return Native.ClassName ?? "";
                case "classList": return JsValue.FromObject(Engine, new JsClassList(Host, Native));
                case "style": return new JsStyle(Host, Native);
                case "innerHTML": return Native.InnerHtml ?? "";
                case "outerHTML": return Native.OuterHtml ?? "";
                case "value": return Native.GetAttribute("value") ?? "";
                case "children": return Host.WrapNodes(Native.Children);
                case "childElementCount": return Native.ChildElementCount;
                case "firstElementChild": return Wrap(Native.FirstElementChild);
                case "lastElementChild": return Wrap(Native.LastElementChild);
                case "nextElementSibling": return Wrap(Native.NextElementSibling);
                case "previousElementSibling": return Wrap(Native.PreviousElementSibling);
                case "tabIndex": return TabIndex;
                case "htmlFor": return Native.GetAttribute("for") ?? "";
                case "readOnly": return Native.HasAttribute("readonly");

                // React reads this on every host node to decide which namespace its children belong in - an <svg>
                // whose namespaceURI came back undefined put the whole subtree back into HTML, and the next element
                // it created was a plain <path> that nothing draws. Answering it costs nothing and keeps the
                // renderer's own bookkeeping straight even though this engine paints no SVG.
                case "namespaceURI": return (JsValue)Native.NamespaceUri ?? JsValue.Null;
            }

            // Plain attribute-backed strings and booleans. Kept as tables rather than more cases because the two
            // rules - "reads and writes the attribute of the same name" and "present means true" - are what makes
            // them one thing each, and a renderer decides between property and attribute by asking `name in node`.
            if (StringProperties.Contains(name)) return Native.GetAttribute(Attribute(name)) ?? "";
            if (BoolProperties.Contains(name)) return Native.HasAttribute(Attribute(name));
            if (IsHandlerName(name)) return Host.InlineHandler(Native, name.Substring(2));

            return base.Member(name);
        }

        private protected override bool Assign(string name, JsValue value)
        {
            switch (name)
            {
                case "id": return Write("id", Text(value));
                case "className": return Write("class", Text(value));
                case "htmlFor": return Write("for", Text(value));
                case "readOnly": return Flag("readonly", TypeConverter.ToBoolean(value));
                case "tabIndex": return Write("tabindex", Text(value));

                case "innerHTML":
                    if (string.Equals(Native.InnerHtml ?? "", Text(value), StringComparison.Ordinal)) return true;
                    foreach (INode child in Native.ChildNodes) Host.Forget(child);
                    Native.InnerHtml = Text(value);
                    Host.MarkDirty();
                    return true;

                case "textContent":
                    // The children go, so their wrappers and listeners have to go with them - a list re-rendered by
                    // assigning textContent would otherwise retain every row it ever held.
                    foreach (INode child in Native.ChildNodes) Host.Forget(child);
                    SetText(Text(value));
                    return true;
            }

            if (StringProperties.Contains(name)) return Write(Attribute(name), Text(value));
            if (BoolProperties.Contains(name)) return Flag(Attribute(name), TypeConverter.ToBoolean(value));

            if (IsHandlerName(name))
            {
                Host.SetInlineHandler(Native, name.Substring(2), value);
                return true;
            }

            return base.Assign(name, value);
        }

        private protected override Func<JsValue[], JsValue> Method(string name)
        {
            switch (name)
            {
                case "getAttribute": return a => (JsValue)Native.GetAttribute(Name(a, 0)) ?? JsValue.Null;
                case "hasAttribute": return a => Native.HasAttribute(Name(a, 0));
                case "setAttribute": return a => { Write(Name(a, 0), a.Length > 1 ? Text(a[1]) : ""); return JsValue.Undefined; };
                case "removeAttribute": return a => { Erase(Name(a, 0)); return JsValue.Undefined; };

                case "querySelector": return a => Wrap(Query(() => Native.QuerySelector(Name(a, 0))));
                case "querySelectorAll": return a => Host.WrapNodes(QueryAll(() => Native.QuerySelectorAll(Name(a, 0))));
                case "matches": return a => Query(() => Native.Matches(Name(a, 0)) ? Native : null) != null;
                case "closest": return a => Wrap(Query(() => Native.Closest(Name(a, 0))));

                case "replaceChildren": return _ => { ReplaceChildren(); return JsValue.Undefined; };
                case "click": return _ => { Host.Dispatch(Native, "click"); return JsValue.Undefined; };
                case "focus": return _ => { Host.RequestFocus(Native); return JsValue.Undefined; };
                case "blur": return _ => { Host.RequestBlur(Native); return JsValue.Undefined; };
                case "scrollToEnd": return _ => { Host.RequestScrollToEnd(Native); return JsValue.Undefined; };

                case "rect":
                case "getBoundingClientRect": return _ => JsValue.FromObject(Engine, Rect());
            }

            return base.Method(name);
        }

        /// <summary>
        /// Where this box ended up on screen, in css pixels, measured from the top left of the viewport - the same
        /// frame `position: fixed` is measured in, which is the whole point.
        ///
        /// A page cannot place anything against another element without this. Its absence is why an "on hover" label
        /// had to be laid out INSIDE the row it belonged to, pushing everything along, instead of floating over it.
        ///
        /// Reflects the LAST render. A box the page has just created has not been laid out yet and reads as zeroes;
        /// ask after the render that builds it, not in the handler that asks for it.
        /// </summary>
        private JsRect Rect()
        {
            float[] r = Host.RectOf(Native);
            return new JsRect
            {
                x = r[0], y = r[1], width = r[2], height = r[3],
                left = r[0], top = r[1], right = r[0] + r[2], bottom = r[1] + r[3],
            };
        }

        /// <summary>Drop every child in one go - the cheap way to re-render a list from script.</summary>
        private void ReplaceChildren()
        {
            foreach (INode child in Native.ChildNodes) Host.Forget(child);

            Native.InnerHtml = "";
            Host.MarkDirty();
        }

        private bool Write(string attribute, string value)
        {
            if (string.IsNullOrEmpty(attribute)) return true;
            if (string.Equals(Native.GetAttribute(attribute) ?? "", value, StringComparison.Ordinal)) return true;

            Native.SetAttribute(attribute, value ?? "");
            Host.MarkDirty();
            return true;
        }

        private bool Flag(string attribute, bool on)
        {
            if (Native.HasAttribute(attribute) == on) return true;

            if (on) Native.SetAttribute(attribute, "");
            else Native.RemoveAttribute(attribute);

            Host.MarkDirty();
            return true;
        }

        private void Erase(string attribute)
        {
            if (string.IsNullOrEmpty(attribute) || !Native.HasAttribute(attribute)) return;

            Native.RemoveAttribute(attribute);
            Host.MarkDirty();
        }

        private int TabIndex
        {
            get => int.TryParse(Native.GetAttribute("tabindex"), out int value) ? value : -1;
        }

        /// <summary>
        /// A selector a page made up is an ordinary event, not a fault: AngleSharp throws on one it cannot parse, and
        /// an uncaught throw here would take the whole page's script down over a typo in a string.
        /// </summary>
        private static IElement Query(Func<IElement> run)
        {
            try { return run(); }
            catch { return null; }
        }

        private static IEnumerable<IElement> QueryAll(Func<IEnumerable<IElement>> run)
        {
            try { return run(); }
            catch { return Array.Empty<IElement>(); }
        }

        private static string Name(JsValue[] args, int index) => index < args.Length ? args[index].ToString() : "";

        /// <summary>
        /// `onclick`, `oninput`, and every other all-lower-case `on...` name.
        ///
        /// A browser carries one of these for every event it knows, and a renderer uses their presence to work out
        /// how an `onClick` prop should be spelled: Preact asks `name.toLowerCase() in dom` and, when the answer is
        /// no, registers the listener as "Click" with a capital C while looking it up again as "click". The page
        /// then mounts, updates and reorders perfectly and does nothing at all when touched, which is exactly what
        /// happened here. So the test is the SHAPE of the name rather than a list of events - a list would answer no
        /// for the next event added to the web and put the same silence back.
        /// </summary>
        private static bool IsHandlerName(string name)
        {
            if (name.Length < 4 || name[0] != 'o' || name[1] != 'n') return false;

            for (int i = 2; i < name.Length; i++)
                if (name[i] < 'a' || name[i] > 'z') return false;

            return true;
        }

        /// <summary>The attribute a property name stands for. Only the handful that differ are listed; the rest are
        /// the same word.</summary>
        private static string Attribute(string property) => property switch
        {
            "className" => "class",
            "htmlFor" => "for",
            "readOnly" => "readonly",
            "tabIndex" => "tabindex",
            "maxLength" => "maxlength",
            _ => property,
        };

        private static readonly HashSet<string> StringProperties = new(StringComparer.Ordinal)
        {
            "value", "type", "name", "placeholder", "src", "href", "alt", "title", "rel", "target",
            "maxLength", "step", "min", "max", "accept", "pattern", "role",
        };

        private static readonly HashSet<string> BoolProperties = new(StringComparer.Ordinal)
        {
            "disabled", "checked", "selected", "required", "multiple", "hidden", "autofocus", "open",
        };
    }

    /// <summary>
    /// A text node.
    ///
    /// Without one there is no way to change a single string in a page from script except by replacing the markup
    /// around it, and no virtual-DOM renderer can work at all: every one of them creates text nodes for the text
    /// between elements and then writes `.data` on the same node for the rest of the node's life.
    /// </summary>
    public sealed class JsText : JsNode
    {
        internal JsText(ScriptHost host, IText text) : base(host, text) => Native = text;

        internal IText Native { get; }

        private protected override ObjectInstance Interface(DomTypes types) => types?.Text;

        private protected override int NodeTypeCode => 3;

        private protected override string NodeName => "#text";

        private protected override JsValue NodeValue => Native.Data ?? "";

        public override string ToString() => Native.Data ?? "";

        private protected override JsValue Member(string name) =>
            name switch
            {
                "data" => Native.Data ?? "",
                "length" => (Native.Data ?? "").Length,
                "wholeText" => Native.Data ?? "",
                _ => base.Member(name),
            };

        private protected override bool Assign(string name, JsValue value)
        {
            if (name != "data") return base.Assign(name, value);

            SetText(Text(value));
            return true;
        }
    }

    /// <summary>
    /// A comment node. It draws nothing, and that is what it is for: a renderer uses one as a placeholder to remember
    /// where a conditional subtree belongs while that subtree is absent.
    /// </summary>
    public sealed class JsComment : JsNode
    {
        internal JsComment(ScriptHost host, IComment comment) : base(host, comment) => Native = comment;

        internal IComment Native { get; }

        private protected override ObjectInstance Interface(DomTypes types) => types?.Comment;

        private protected override int NodeTypeCode => 8;

        private protected override string NodeName => "#comment";

        private protected override JsValue NodeValue => Native.Data ?? "";

        public override string ToString() => "<!--" + Native.Data + "-->";

        private protected override JsValue Member(string name) =>
            name == "data" ? Native.Data ?? "" : base.Member(name);

        private protected override bool Assign(string name, JsValue value)
        {
            if (name != "data") return base.Assign(name, value);

            SetText(Text(value));
            return true;
        }
    }

    /// <summary>`element.classList`, the part of it a page actually uses.</summary>
    public sealed class JsClassList
    {
        private readonly ScriptHost _host;
        private readonly IElement _element;

        internal JsClassList(ScriptHost host, IElement element)
        {
            _host = host;
            _element = element;
        }

        public int Length => _element.ClassList.Length;

        public bool Contains(string name) => _element.ClassList.Contains(name);

        public void Add(string name)
        {
            _element.ClassList.Add(name);
            _host.MarkDirty();
        }

        public void Remove(string name)
        {
            _element.ClassList.Remove(name);
            _host.MarkDirty();
        }

        public bool Toggle(string name)
        {
            bool on = !_element.ClassList.Contains(name);
            if (on) _element.ClassList.Add(name); else _element.ClassList.Remove(name);
            _host.MarkDirty();
            return on;
        }
    }

    /// <summary>
    /// `element.style`. Backed by the element's `style` attribute, which the cascade already reads as the
    /// highest-priority source, so an inline write lands in the next repaint with no special casing anywhere else.
    /// </summary>
    public sealed class JsStyle : ObjectInstance
    {
        private const string Attribute = "style";

        private readonly ScriptHost _host;
        private readonly IElement _element;

        internal JsStyle(ScriptHost host, IElement element) : base(host.Engine)
        {
            _host = host;
            _element = element;
        }

        public override JsValue Get(JsValue property, JsValue receiver)
        {
            string name = property.IsString() ? property.AsString() : null;
            if (name == null) return Undefined;
            if (name == "cssText") return _element.GetAttribute(Attribute) ?? "";

            switch (name)
            {
                // A custom property is the one thing the bracket form cannot carry - `--brand` is not a member name
                // a minifier will produce - so every framework reaches for setProperty when the name starts with a
                // dash, and a host without it drops exactly the theme variables.
                case "setProperty":
                    return new ClrFunction(Engine, name, (_, a) =>
                    {
                        Apply(a.Length > 0 ? Kebab(a[0].ToString()) : "", a.Length > 1 ? Str(a[1]) : "");
                        return Undefined;
                    });

                case "removeProperty":
                    return new ClrFunction(Engine, name, (_, a) =>
                    {
                        string css = a.Length > 0 ? Kebab(a[0].ToString()) : "";
                        JsValue had = (JsValue)Read(css) ?? "";
                        Apply(css, "");
                        return had;
                    });

                case "getPropertyValue":
                    return new ClrFunction(Engine, name, (_, a) =>
                        (JsValue)Read(a.Length > 0 ? Kebab(a[0].ToString()) : "") ?? "");
            }

            return Read(Kebab(name)) ?? (JsValue)"";
        }

        public override bool Set(JsValue property, JsValue value, JsValue receiver)
        {
            if (!property.IsString()) return false;

            string name = property.AsString();
            Apply(name == "cssText" ? null : Kebab(name), Str(value));
            return true;
        }

        public override bool HasProperty(JsValue property) =>
            property.IsString() && (property.AsString() == "cssText" || Read(Kebab(property.AsString())) != null);

        private static string Str(JsValue value) =>
            value.IsNull() || value.IsUndefined() ? "" : value.ToString();

        /// <summary>One declaration in or out. A null property replaces the whole block, which is what cssText is.</summary>
        private void Apply(string css, string text)
        {
            string before = css == null ? _element.GetAttribute(Attribute) : Read(css);
            if (string.Equals(before ?? "", text, StringComparison.Ordinal)) return;

            Write(css, text);

            // A rebuild destroys and recreates every GameObject on the page - measured at roughly half a millisecond
            // per box, so a 200-box page costs ~100ms. Paying that to change a colour, and paying it per frame to
            // animate a transform, is what made panning a map impossible. These properties are handled by
            // Painter.Repaint, which is the same path :hover already takes: new style, same layout, no new objects.
            // css is null for cssText, which replaces the whole declaration block and can therefore change anything.
            if (Css.PaintOnlyProperties.Covers(css)) _host.MarkPaintDirty(_element);
            else _host.MarkDirty();
        }

        private string Read(string property)
        {
            if (string.IsNullOrEmpty(property)) return null;

            foreach (Css.Declaration d in Css.CssParser.ParseDeclarations(_element.GetAttribute(Attribute) ?? ""))
                if (string.Equals(d.Property, property, StringComparison.OrdinalIgnoreCase)) return d.Value;
            return null;
        }

        /// <summary>Rewrites the inline declaration block. A null property replaces the whole thing (cssText).</summary>
        private void Write(string property, string value)
        {
            if (property == null)
            {
                _element.SetAttribute(Attribute, value);
                return;
            }

            var kept = new List<string>();
            foreach (Css.Declaration d in Css.CssParser.ParseDeclarations(_element.GetAttribute(Attribute) ?? ""))
                if (!string.Equals(d.Property, property, StringComparison.OrdinalIgnoreCase))
                    kept.Add(d.Property + ": " + d.Value);

            if (!string.IsNullOrEmpty(value)) kept.Add(property + ": " + value);
            _element.SetAttribute(Attribute, string.Join("; ", kept));
        }

        /// <summary>`backgroundColor` is the same property as `background-color`; JS spells it one way and CSS the
        /// other. A name that already carries a dash is left alone, which is what keeps `--brand` intact.</summary>
        private static string Kebab(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('-') >= 0) return name;

            var sb = new System.Text.StringBuilder(name.Length + 4);
            foreach (char c in name)
            {
                if (char.IsUpper(c)) { sb.Append('-'); sb.Append(char.ToLowerInvariant(c)); }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>The global `document`.</summary>
    public sealed class JsDocument
    {
        private readonly ScriptHost _host;
        private readonly IDocument _document;

        internal JsDocument(ScriptHost host, IDocument document)
        {
            _host = host;
            _document = document;
        }

        /// <summary>9, as the DOM numbers a document. Read by every renderer that checks what it was handed before
        /// it mounts into it.</summary>
        public int NodeType => 9;

        public string NodeName => "#document";

        /// <summary>Always "complete". The script only runs once the document is parsed and the stylesheet resolved,
        /// so there is no window in which a page could observe anything else.</summary>
        public string ReadyState => "complete";

        public JsElement Body => _host.Wrap(_document.Body ?? _document.DocumentElement);

        public JsElement DocumentElement => _host.Wrap(_document.DocumentElement);

        public JsElement Head => _host.Wrap(_document.Head);

        /// <summary>The field the player is typing in, or null. Answered by the view, because a caret lives in
        /// TextMeshPro rather than in the document.</summary>
        public JsElement ActiveElement => _host.Wrap(_host.ActiveElement());

        public string Title
        {
            get => _document.Title ?? "";
            set { _document.Title = value ?? ""; }
        }

        public JsElement GetElementById(string id) => _host.Wrap(_document.GetElementById(id));

        public JsElement QuerySelector(string selector) => _host.Wrap(Try(() => _document.QuerySelector(selector)));

        public JsValue QuerySelectorAll(string selector) =>
            _host.WrapNodes(Try(() => (IEnumerable<IElement>)_document.QuerySelectorAll(selector))
                            ?? Array.Empty<IElement>());

        public JsElement CreateElement(string tag) => _host.Wrap(_document.CreateElement(tag));

        /// <summary>
        /// The two-argument spelling, `createElement(tag, { is: 'my-thing' })`.
        ///
        /// The options are ignored - customised built-ins have no meaning without a custom-element registry - but the
        /// overload has to exist, because a renderer passes the second argument unconditionally and a host with only
        /// the one-argument form throws on the first element it is asked to build. Which is what happened: Preact
        /// could not create a single node.
        /// </summary>
        public JsElement CreateElement(string tag, JsValue options) => CreateElement(tag);

        /// <summary>
        /// The namespaced spelling. The element really is created in the namespace it names, so `namespaceURI` reads
        /// back what the caller asked for - which is what a renderer uses to decide the namespace of the children it
        /// creates next.
        ///
        /// Nothing draws an SVG here. The subtree builds and lays out as empty boxes, which is the fail-soft the rest
        /// of the engine follows: a page with one icon in it renders the other ninety-nine per cent rather than
        /// throwing halfway through a mount.
        /// </summary>
        public JsElement CreateElementNS(string ns, string tag) =>
            _host.Wrap(string.IsNullOrEmpty(ns)
                           ? _document.CreateElement(tag)
                           : Try(() => _document.CreateElement(ns, tag)) ?? _document.CreateElement(tag));

        public JsElement CreateElementNS(string ns, string tag, JsValue options) => CreateElementNS(ns, tag);

        public JsNode CreateTextNode(string text) => _host.WrapNode(_document.CreateTextNode(text ?? ""));

        public JsNode CreateComment(string text) => _host.WrapNode(_document.CreateComment(text ?? ""));

        // No createDocumentFragment. Standing in with a detached <div> was tried and taken back out: appending a
        // fragment moves its CHILDREN and leaves the fragment behind, so the stand-in inserts one extra box into
        // every page that used it - silently, and only in the game. An absent member throws where the author can
        // see it.

        public void AddEventListener(string type, JsValue handler) => AddEventListener(type, handler, JsBoolean.False);

        public void AddEventListener(string type, JsValue handler, JsValue options) =>
            _host.AddListener(_document.Body ?? (INode)_document.DocumentElement, type, handler, IsCapture(options));

        public void RemoveEventListener(string type, JsValue handler) => RemoveEventListener(type, handler, JsBoolean.False);

        public void RemoveEventListener(string type, JsValue handler, JsValue options) =>
            _host.RemoveListener(_document.Body ?? (INode)_document.DocumentElement, type, handler, IsCapture(options));

        internal static bool IsCapture(JsValue options)
        {
            if (options == null || options.IsUndefined() || options.IsNull()) return false;
            if (options.IsBoolean()) return options.AsBoolean();

            if (options is ObjectInstance bag)
            {
                JsValue capture = bag.Get("capture");
                return !capture.IsUndefined() && TypeConverter.ToBoolean(capture);
            }

            return false;
        }

        private static T Try<T>(Func<T> run) where T : class
        {
            try { return run(); }
            catch { return null; }
        }
    }

    /// <summary>A laid-out rectangle handed to script. Lowercase members on purpose: this crosses into JavaScript
    /// and reads there exactly as a browser's rect does.</summary>
    public sealed class JsRect
    {
        public double x { get; set; }
        public double y { get; set; }
        public double width { get; set; }
        public double height { get; set; }
        public double top { get; set; }
        public double left { get; set; }
        public double right { get; set; }
        public double bottom { get; set; }
    }

    /// <summary>The event object handed to a listener.</summary>
    public sealed class JsEvent
    {
        internal JsEvent(string type, JsNode target)
        {
            Type = type;
            Target = target;
            CurrentTarget = target;
        }

        public string Type { get; }

        public JsNode Target { get; }

        public JsNode CurrentTarget { get; internal set; }

        /// <summary>1 while capturing handlers run, 2 at the target, 3 on the way back up - the numbers the DOM
        /// uses. A delegating listener reads this to tell a click on itself from one on a child.</summary>
        public int EventPhase { get; internal set; }

        public bool Bubbles { get; internal set; } = true;

        /// <summary>Current text of the control the event came from, for `input` and `keydown`.</summary>
        public string Value { get; internal set; } = "";

        /// <summary>The key for a `keydown`, spelled as the DOM spells it - "Enter", "Tab", "ArrowUp", "r". Empty for
        /// other events.</summary>
        public string Key { get; internal set; } = "";

        /// <summary>Modifier state on a `keydown`. Always false for other events, and always false for Enter, which
        /// comes from the field's own submit and carries no modifier information.</summary>
        public bool CtrlKey { get; internal set; }

        public bool ShiftKey { get; internal set; }

        public bool AltKey { get; internal set; }

        /// <summary>True when a `keydown` is the player holding the key rather than pressing it. A page that must act
        /// once per press - accepting a completion, submitting - checks this and returns early.</summary>
        public bool Repeat { get; internal set; }

        /// <summary>
        /// Whether the field had text selected when the key was pressed.
        ///
        /// Here because the field acts on some of the same keys the page claimed, and for one of them both meanings
        /// are right depending on this: Ctrl+C copies a selection and interrupts when there is none, which is what
        /// every terminal on Windows does. A page that claims Ctrl+C without checking this takes copy away.
        /// </summary>
        public bool HasSelection { get; internal set; }

        /// <summary>What raised the event, where more than one thing can: "rightClick" or "escape" for `back`.
        /// Empty otherwise. Most pages should treat every source alike; it is here for the ones that must not.</summary>
        public string Source { get; internal set; } = "";

        /// <summary>
        /// Where inside the clicked element the pointer landed, in CSS pixels from its top-left corner. Zero for
        /// events that have no position (`back`, `input`, `keydown`, `orientationchange`).
        ///
        /// This is what lets a page answer "where did they point at", which a map needs and a plain button does not.
        /// </summary>
        public float OffsetX { get; internal set; }

        public float OffsetY { get; internal set; }

        /// <summary>The same point as a 0..1 fraction of the element's own width and height - what you want when the
        /// element stands for something with its own coordinate space, like a map.</summary>
        public float NormX { get; internal set; }

        public float NormY { get; internal set; }

        /// <summary>
        /// How far the pointer moved since the previous event, in CSS pixels. Set on `drag`; zero on `dragstart` and
        /// `dragend`, which mark the ends of a gesture rather than a movement within it.
        ///
        /// Measured against the page rather than the element, so an element that is being moved BY the drag still
        /// reports the movement of the hand rather than the movement of itself.
        /// </summary>
        public float DeltaX { get; internal set; }

        public float DeltaY { get; internal set; }

        /// <summary>One wheel notch on a `wheel` event, positive downwards - the sign the DOM uses.</summary>
        public float WheelDelta { get; internal set; }

        /// <summary>
        /// Where the pointer was in the PAGE, in css pixels from its top left - what a browser calls clientX/clientY.
        ///
        /// The counterpart to <see cref="OffsetX"/>, which is measured inside the element. A page placing a context
        /// menu where the pointer is needs this one; a page asking which part of a map was hit needs the other.
        /// </summary>
        public float ClientX { get; internal set; }

        public float ClientY { get; internal set; }

        /// <summary>Which mouse button: 0 left, 1 middle, 2 right - the numbering the DOM uses. Zero for events that
        /// have no button.</summary>
        public int Button { get; internal set; }

        /// <summary>How many clicks in the run this event belongs to: 1 for a click, 2 for the second of a
        /// double-click. Zero for events that do not count.</summary>
        public int Detail { get; internal set; }

        public bool DefaultPrevented { get; private set; }

        internal bool PropagationStopped { get; private set; }

        /// <summary>Set by stopImmediatePropagation, which also ends the handlers registered on the CURRENT element -
        /// the difference that matters when two handlers share one node and the first decides the second must not
        /// run.</summary>
        internal bool ImmediatelyStopped { get; private set; }

        public void PreventDefault() => DefaultPrevented = true;

        public void StopPropagation() => PropagationStopped = true;

        public void StopImmediatePropagation()
        {
            PropagationStopped = true;
            ImmediatelyStopped = true;
        }
    }
}
