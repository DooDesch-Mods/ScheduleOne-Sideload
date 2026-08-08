using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Sideload.Script
{
    /// <summary>
    /// The DOM as JavaScript sees it. These types are thin wrappers around AngleSharp nodes, and every one of them is
    /// public on purpose: Jint reaches members through reflection and emitted accessors, which cannot see the public
    /// members of an internal type.
    ///
    /// The wrappers exist rather than handing AngleSharp straight to the engine for one reason: every mutation has to
    /// tell the renderer that the page is stale. Routing all writes through here means a script cannot change the
    /// document behind the renderer's back, which is what makes "JS owns the DOM" workable without AngleSharp's
    /// mutation observers.
    /// </summary>
    public sealed class JsElement
    {
        private readonly ScriptHost _host;

        internal JsElement(ScriptHost host, IElement element)
        {
            _host = host;
            Native = element;
        }

        internal IElement Native { get; }

        public string TagName => Native.LocalName;

        public string Id
        {
            get => Native.Id ?? "";
            set { if (Set(Native.Id, value)) { Native.Id = value; _host.MarkDirty(); } }
        }

        public string ClassName
        {
            get => Native.ClassName ?? "";
            set { if (Set(Native.ClassName, value)) { Native.ClassName = value; _host.MarkDirty(); } }
        }

        public JsClassList ClassList => new JsClassList(_host, Native);

        public JsStyle Style => new JsStyle(_host, Native);

        public string TextContent
        {
            get => Native.TextContent ?? "";
            set { if (Set(Native.TextContent, value)) { Native.TextContent = value; _host.MarkDirty(); } }
        }

        public string InnerHTML
        {
            get => Native.InnerHtml ?? "";
            set
            {
                if (!Set(Native.InnerHtml, value)) return;

                foreach (IElement child in Native.Children) _host.Forget(child);
                Native.InnerHtml = value;
                _host.MarkDirty();
            }
        }

        /// <summary>Form-control value. Backed by the `value` attribute, which is also what the painted input field
        /// writes back on every keystroke - so reading it here always sees what the player typed.</summary>
        public string Value
        {
            get => Native.GetAttribute("value") ?? "";
            set
            {
                if (!Set(Native.GetAttribute("value"), value)) return;
                Native.SetAttribute("value", value ?? "");
                _host.MarkDirty();
            }
        }

        public bool Disabled
        {
            get => Native.HasAttribute("disabled");
            set
            {
                if (value) Native.SetAttribute("disabled", "");
                else Native.RemoveAttribute("disabled");
                _host.MarkDirty();
            }
        }

        public JsElement ParentElement => _host.Wrap(Native.ParentElement);

        public JsValue Children
        {
            get
            {
                var wrapped = new List<JsValue>();
                foreach (IElement child in Native.Children) wrapped.Add(JsValue.FromObject(_host.Engine, _host.Wrap(child)));
                return new JsArray(_host.Engine, wrapped.ToArray());
            }
        }

        public string GetAttribute(string name) => Native.GetAttribute(name);

        public void SetAttribute(string name, string value)
        {
            if (!Set(Native.GetAttribute(name), value)) return;
            Native.SetAttribute(name, value ?? "");
            _host.MarkDirty();
        }

        public void RemoveAttribute(string name)
        {
            if (!Native.HasAttribute(name)) return;
            Native.RemoveAttribute(name);
            _host.MarkDirty();
        }

        public bool HasAttribute(string name) => Native.HasAttribute(name);

        public JsElement QuerySelector(string selector) => _host.Wrap(Native.QuerySelector(selector));

        public JsValue QuerySelectorAll(string selector) => _host.WrapAll(Native.QuerySelectorAll(selector));

        public JsElement AppendChild(JsElement child)
        {
            if (child != null) { Native.AppendChild(child.Native); _host.MarkDirty(); }
            return child;
        }

        public JsElement RemoveChild(JsElement child)
        {
            if (child == null) return null;

            Native.RemoveChild(child.Native);
            _host.Forget(child.Native);
            _host.MarkDirty();
            return child;
        }

        public JsElement InsertBefore(JsElement child, JsElement reference)
        {
            if (child == null) return null;
            Native.InsertBefore(child.Native, reference?.Native);
            _host.MarkDirty();
            return child;
        }

        public void Remove()
        {
            Native.Remove();
            _host.Forget(Native);
            _host.MarkDirty();
        }

        /// <summary>Drop every child in one go - the cheap way to re-render a list from script.</summary>
        public void ReplaceChildren()
        {
            // Every child leaves the document here, so the engine has to let go of their wrappers and listeners -
            // re-rendering a list is exactly this call, and it runs on every update.
            foreach (IElement child in Native.Children) _host.Forget(child);

            Native.InnerHtml = "";
            _host.MarkDirty();
        }

        public void AddEventListener(string type, JsValue handler) => _host.AddListener(Native, type, handler);

        public void RemoveEventListener(string type, JsValue handler) => _host.RemoveListener(Native, type, handler);

        public void Click() => _host.Dispatch(Native, "click");

        public void Focus() => _host.RequestFocus(Native);

        /// <summary>
        /// Pin a scrollable box to its end. Takes effect after the next render, which is the only moment it can mean
        /// anything: the box a script is looking at is about to be replaced by the one the renderer builds from the
        /// DOM it just changed. Without this a chat app shows you the top of the conversation after every message.
        /// </summary>
        public void ScrollToEnd() => _host.RequestScrollToEnd(Native);

        /// <summary>
        /// Where this box ended up on screen, in css pixels: `{ x, y, width, height }`, measured from the top left
        /// of the viewport - the same frame `position: fixed` is measured in, which is the whole point.
        ///
        /// A page cannot place anything against another element without this. It is what a browser calls
        /// getBoundingClientRect, and its absence is why an "on hover" label had to be laid out INSIDE the row it
        /// belonged to, pushing everything along, instead of floating over it like a tooltip.
        ///
        /// Reflects the LAST render. A box the page has just created has not been laid out yet and reads as zeroes;
        /// ask after the render that builds it, not in the handler that asks for it.
        /// </summary>
        public JsRect Rect()
        {
            float[] r = _host.RectOf(Native);
            return new JsRect { x = r[0], y = r[1], width = r[2], height = r[3] };
        }

        public override string ToString() => "<" + Native.LocalName + ">";

        /// <summary>
        /// Would this write change anything? A page that repaints a clock every second assigns the same string most
        /// of the time, and treating that as a change would rebuild the whole view for nothing.
        /// </summary>
        private static bool Set(string current, string next) => !string.Equals(current ?? "", next ?? "", StringComparison.Ordinal);
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
    ///
    /// This one derives from Jint's <see cref="ObjectInstance"/> instead of being a plain CLR object, because a CLR
    /// wrapper only exposes the members its type declares - and `el.style.backgroundColor = '#123'` needs a property
    /// that no C# type can declare ahead of time. Overriding Get/Set gives the arbitrary property names CSS requires.
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

            return Read(Kebab(name)) ?? (JsValue)"";
        }

        public override bool Set(JsValue property, JsValue value, JsValue receiver)
        {
            if (!property.IsString()) return false;

            string name = property.AsString();
            string text = value.IsNull() || value.IsUndefined() ? "" : value.ToString();

            string css = name == "cssText" ? null : Kebab(name);
            string before = css == null ? _element.GetAttribute(Attribute) : Read(css);
            if (string.Equals(before ?? "", text, StringComparison.Ordinal)) return true;

            Write(css, text);

            // A rebuild destroys and recreates every GameObject on the page - measured at roughly half a millisecond
            // per box, so a 200-box page costs ~100ms. Paying that to change a colour, and paying it per frame to
            // animate a transform, is what made panning a map impossible. These properties are handled by
            // Painter.Repaint, which is the same path :hover already takes: new style, same layout, no new objects.
            // css is null for cssText, which replaces the whole declaration block and can therefore change anything.
            if (Css.PaintOnlyProperties.Covers(css)) _host.MarkPaintDirty(_element);
            else _host.MarkDirty();

            return true;
        }

        public override bool HasProperty(JsValue property) =>
            property.IsString() && (property.AsString() == "cssText" || Read(Kebab(property.AsString())) != null);

        private string Read(string property)
        {
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
        /// other.</summary>
        private static string Kebab(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

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

        public JsElement Body => _host.Wrap(_document.Body ?? _document.DocumentElement);

        public JsElement GetElementById(string id) => _host.Wrap(_document.GetElementById(id));

        public JsElement QuerySelector(string selector) => _host.Wrap(_document.QuerySelector(selector));

        public JsValue QuerySelectorAll(string selector) => _host.WrapAll(_document.QuerySelectorAll(selector));

        public JsElement CreateElement(string tag) => _host.Wrap(_document.CreateElement(tag));

        public void AddEventListener(string type, JsValue handler) =>
            _host.AddListener(_document.Body ?? _document.DocumentElement, type, handler);
    }

    /// <summary>A laid-out rectangle handed to script. Lowercase members on purpose: this crosses into JavaScript
    /// and reads there exactly as a browser's rect does.</summary>
    public sealed class JsRect
    {
        public double x { get; set; }
        public double y { get; set; }
        public double width { get; set; }
        public double height { get; set; }
    }

    /// <summary>The event object handed to a listener.</summary>
    public sealed class JsEvent
    {
        internal JsEvent(string type, JsElement target)
        {
            Type = type;
            Target = target;
            CurrentTarget = target;
        }

        public string Type { get; }

        public JsElement Target { get; }

        public JsElement CurrentTarget { get; internal set; }

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

        public bool DefaultPrevented { get; private set; }

        internal bool PropagationStopped { get; private set; }

        public void PreventDefault() => DefaultPrevented = true;

        public void StopPropagation() => PropagationStopped = true;
    }
}
