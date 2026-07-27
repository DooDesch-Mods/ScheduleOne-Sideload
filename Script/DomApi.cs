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
            _host.MarkDirty();
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

        /// <summary>The key for a `keydown`, spelled as the DOM spells it - "Enter". Empty for other events.</summary>
        public string Key { get; internal set; } = "";

        /// <summary>What raised the event, where more than one thing can: "rightClick" or "escape" for `back`.
        /// Empty otherwise. Most pages should treat every source alike; it is here for the ones that must not.</summary>
        public string Source { get; internal set; } = "";

        public bool DefaultPrevented { get; private set; }

        internal bool PropagationStopped { get; private set; }

        public void PreventDefault() => DefaultPrevented = true;

        public void StopPropagation() => PropagationStopped = true;
    }
}
