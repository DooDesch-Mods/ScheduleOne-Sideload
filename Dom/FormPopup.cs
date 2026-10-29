using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Sideload.Css;
using Sideload.Layout;

namespace Sideload.Dom
{
    /// <summary>
    /// The list a select drops open and the picker a colour input opens - the two pieces of a form control that a
    /// browser draws outside the page.
    ///
    /// Built as a small document of its own, with its own stylesheet, and laid out and painted by the same engine as
    /// the page. That is what makes it cheap: the rows get hover, the list scrolls, the hex field is a real text field,
    /// and none of it needed a line of drawing code. The page never sees these elements - they are not in its
    /// document, so no query finds them and no listener of the page fires on them.
    ///
    /// The document lives as long as the popup is open and is changed in place, never rebuilt: the hex field keeps
    /// its caret across a page rebuild only because its element is the same object each time.
    ///
    /// Everything here is Unity-free. The view decides when a popup opens and feeds it the pointer; this class owns
    /// what the popup looks like and what a point inside it means.
    /// </summary>
    internal sealed class FormPopup
    {
        internal enum Kinds { Select, Color }

        /// <summary>Rows a list shows before it scrolls.</summary>
        internal const int VisibleRows = 8;

        internal const float RowHeight = 24f;
        internal const float GroupHeight = 22f;
        internal const float SvWidth = 200f;
        internal const float SvHeight = 130f;
        internal const float HueHeight = 12f;

        /// <summary>Gap between the control and its popup, and the least distance kept from the viewport's edge.</summary>
        private const float Gap = 2f;
        private const float Margin = 4f;

        internal Kinds Kind { get; }
        internal IElement Owner { get; }
        internal IDocument Document { get; }

        internal IElement Scrim { get; private set; }
        internal IElement Panel { get; private set; }

        // ---- select
        internal IElement List { get; private set; }

        /// <summary>One entry per option, in the order <see cref="FormValues.Options"/> gives them.</summary>
        internal List<IElement> Rows { get; } = new();

        /// <summary>The row the keyboard is on. Starts on the chosen option.</summary>
        internal int Highlight { get; private set; } = -1;

        // ---- colour
        internal IElement Sv { get; private set; }
        internal IElement Hue { get; private set; }
        internal IElement Hex { get; private set; }
        private IElement _svBase, _svCursor, _hueCursor, _swatch;

        internal double H { get; private set; }
        internal double S { get; private set; }
        internal double V { get; private set; }

        /// <summary>The control's value when the popup opened. `change` fires on close only if the value moved off it -
        /// a player who drags around and comes back to where they started changed nothing.</summary>
        internal string ValueAtOpen { get; }

        /// <summary>The control's box, in css pixels from the viewport's top left, as last measured by the view.</summary>
        internal float[] Anchor { get; set; } = { 0f, 0f, 0f, 0f };

        private FormPopup(Kinds kind, IElement owner)
        {
            Kind = kind;
            Owner = owner;
            Document = new HtmlParser().ParseDocument("<!DOCTYPE html><html><head></head><body></body></html>");
            ValueAtOpen = kind == Kinds.Select ? FormValues.SelectValue(owner) : FormValues.ColorValue(owner);
        }

        internal static FormPopup ForSelect(IElement select)
        {
            var popup = new FormPopup(Kinds.Select, select);
            popup.BuildSelect();
            return popup;
        }

        internal static FormPopup ForColor(IElement input)
        {
            var popup = new FormPopup(Kinds.Color, input);
            popup.BuildColor();
            return popup;
        }

        /// <summary>Whether an element belongs to this popup rather than to the page.</summary>
        internal bool Owns(IElement element) => element != null && ReferenceEquals(element.Owner, Document);

        /// <summary>The `data-part` of a popup element, or "" - what the view switches on when the pointer lands.</summary>
        internal static string PartOf(IElement element) => element?.GetAttribute("data-part") ?? "";

        /// <summary>The elements that need a hit target: everything the pointer can do something to, plus the panel,
        /// so a click on its padding does not fall through to the scrim and close it.</summary>
        internal IEnumerable<IElement> Interactive()
        {
            yield return Scrim;
            yield return Panel;

            if (Kind == Kinds.Select)
            {
                foreach (IElement row in Rows)
                    if (row != null) yield return row;
                yield break;
            }

            yield return Sv;
            yield return Hue;
            yield return Hex;
        }

        // ------------------------------------------------------------------------------------------- select --

        private void BuildSelect()
        {
            Scrim = Part("div", "scrim", "scrim");
            Panel = Part("div", "panel select", "panel");
            List = Part("div", "list", "list");
            Panel.AppendChild(List);
            Scrim.AppendChild(Panel);
            Document.Body.AppendChild(Scrim);

            List<IElement> options = FormValues.Options(Owner);
            IElement groupWas = null;

            for (int i = 0; i < options.Count; i++)
            {
                IElement option = options[i];
                IElement group = IsGroup(option.ParentElement) ? option.ParentElement : null;

                // A group's heading goes in once, above its first option; a group with no options shows nothing,
                // as in a browser.
                if (group != null && !ReferenceEquals(group, groupWas))
                {
                    IElement heading = Part("div", "group", "group");
                    heading.TextContent = group.GetAttribute("label") ?? "";
                    List.AppendChild(heading);
                }
                groupWas = group;

                IElement row = Part("div", "opt", "option");
                row.SetAttribute("data-index", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                row.TextContent = FormValues.OptionLabel(option);
                if (group != null) row.ClassList.Add("in-group");
                if (FormValues.IsDisabled(option)) row.ClassList.Add("disabled");
                List.AppendChild(row);
                Rows.Add(row);
            }

            SetHighlight(FormValues.SelectedIndex(Owner));
        }

        /// <summary>Move the keyboard's row. Out of range is ignored rather than clamped: the caller asked for a row
        /// that is not there, and quietly picking another would hide that.</summary>
        internal void SetHighlight(int index)
        {
            if (index < -1 || index >= Rows.Count) return;

            if (Highlight >= 0 && Highlight < Rows.Count) Rows[Highlight].ClassList.Remove("active");
            Highlight = index;
            if (index >= 0) Rows[index].ClassList.Add("active");
        }

        /// <summary>The option index a row stands for, or -1 for anything that is not an option row.</summary>
        internal int IndexOf(IElement element)
        {
            if (element == null || PartOf(element) != "option") return -1;
            return Rows.IndexOf(element);
        }

        /// <summary>The list's full height: every row and every group heading.</summary>
        internal float ContentHeight()
        {
            float height = 0f;
            foreach (IElement child in List.Children)
                height += PartOf(child) == "group" ? GroupHeight : RowHeight;
            return height;
        }

        /// <summary>Where the highlighted row sits inside the list, in css px from the list's top. -1 when none.</summary>
        internal float HighlightTop()
        {
            if (Highlight < 0) return -1f;

            float top = 0f;
            foreach (IElement child in List.Children)
            {
                if (ReferenceEquals(child, Rows[Highlight])) return top;
                top += PartOf(child) == "group" ? GroupHeight : RowHeight;
            }
            return -1f;
        }

        // ------------------------------------------------------------------------------------------- colour --

        private void BuildColor()
        {
            Scrim = Part("div", "scrim", "scrim");
            Panel = Part("div", "panel color", "panel");

            Sv = Part("div", "sv", "sv");
            _svBase = Part("div", "sv-layer", "");
            IElement white = Part("div", "sv-layer sv-white", "");
            IElement black = Part("div", "sv-layer sv-black", "");
            _svCursor = Part("div", "sv-cursor", "");
            Sv.AppendChild(_svBase);
            Sv.AppendChild(white);
            Sv.AppendChild(black);
            Sv.AppendChild(_svCursor);

            // Six two-stop segments, red to red, because a gradient here has two stops. Side by side they draw the
            // whole hue circle with the seams on the six primaries, where the eye expects a corner anyway.
            Hue = Part("div", "hue", "hue");
            for (int i = 0; i < 6; i++)
            {
                IElement segment = Part("div", "hue-seg", "");
                segment.SetAttribute("style", "background: linear-gradient(90deg, "
                                              + FormValues.HsvToHex(i * 60d, 1d, 1d) + ", "
                                              + FormValues.HsvToHex((i + 1) * 60d % 360d, 1d, 1d) + ")");
                Hue.AppendChild(segment);
            }
            _hueCursor = Part("div", "hue-cursor", "");
            Hue.AppendChild(_hueCursor);

            IElement row = Part("div", "row", "");
            _swatch = Part("div", "swatch", "");
            Hex = Part("input", "hex", "hex");
            Hex.SetAttribute("type", "text");
            Hex.SetAttribute("maxlength", "7");
            row.AppendChild(_swatch);
            row.AppendChild(Hex);

            Panel.AppendChild(Sv);
            Panel.AppendChild(Hue);
            Panel.AppendChild(row);
            Scrim.AppendChild(Panel);
            Document.Body.AppendChild(Scrim);

            FormValues.HexToRgb(ValueAtOpen, out double r, out double g, out double b);
            FormValues.RgbToHsv(r, g, b, out double h, out double s, out double v);
            SetHsv(h, s, v);
        }

        /// <summary>
        /// Move the picker to a colour and return it as `#rrggbb`. The hue is kept apart from the colour because it
        /// cannot be read back from grey: dragging to the left edge of the field would otherwise throw the hue away
        /// and snap the field to red.
        /// </summary>
        internal string SetHsv(double h, double s, double v)
        {
            H = Clamp(h, 0d, 360d);
            S = Clamp(s, 0d, 1d);
            V = Clamp(v, 0d, 1d);

            string hex = FormValues.HsvToHex(H, S, V);
            Style(_svBase, "background-color: " + FormValues.HsvToHex(H, 1d, 1d));
            Style(_svCursor, $"left: {Px(S * SvWidth - 8d)}; top: {Px((1d - V) * SvHeight - 8d)}");
            Style(_hueCursor, $"left: {Px(H / 360d * SvWidth - 4d)}");
            Style(_swatch, "background-color: " + hex);
            Hex.SetAttribute("value", hex);
            return hex;
        }

        /// <summary>A point in the saturation/brightness field, in css px from its top left. Returns the new colour.</summary>
        internal string PickSv(float x, float y) =>
            SetHsv(H, Clamp(x / SvWidth, 0d, 1d), 1d - Clamp(y / SvHeight, 0d, 1d));

        /// <summary>A point on the hue bar, in css px from its left edge. Returns the new colour.</summary>
        internal string PickHue(float x) => SetHsv(Clamp(x / SvWidth, 0d, 1d) * 360d, S, V);

        /// <summary>
        /// Typed hex. Null while the text is not a colour yet - half a code is what the player has while typing one,
        /// and refusing it there would fight every keystroke. The hue is kept for a grey, as in <see cref="SetHsv"/>.
        /// </summary>
        internal string TypeHex(string text)
        {
            if (!FormValues.ParseHex(text, out string hex)) return null;

            FormValues.HexToRgb(hex, out double r, out double g, out double b);
            FormValues.RgbToHsv(r, g, b, out double h, out double s, out double v);
            H = s > 0d ? h : H;
            S = s;
            V = v;

            Style(_svBase, "background-color: " + FormValues.HsvToHex(H, 1d, 1d));
            Style(_svCursor, $"left: {Px(S * SvWidth - 8d)}; top: {Px((1d - V) * SvHeight - 8d)}");
            Style(_hueCursor, $"left: {Px(H / 360d * SvWidth - 4d)}");
            Style(_swatch, "background-color: " + hex);
            return hex;
        }

        // ------------------------------------------------------------------------------------------- placement --

        /// <summary>
        /// Size the scrim to the viewport and put the panel against the control: below it, or above it when below
        /// has no room and above has more, and never past the viewport's edge. A list too long for either side gets
        /// the larger one and scrolls.
        /// </summary>
        internal void Place(float viewportW, float viewportH)
        {
            Style(Scrim, $"left: 0px; top: 0px; width: {Px(viewportW)}; height: {Px(viewportH)}");

            float ax = Anchor[0], ay = Anchor[1], aw = Anchor[2], ah = Anchor[3];
            float width, height, chrome;

            if (Kind == Kinds.Select)
            {
                chrome = 2f * 4f + 2f;   // panel padding and border
                width = Math.Max(aw, 120f);
                height = Math.Min(ContentHeight(), VisibleRows * RowHeight) + chrome;
            }
            else
            {
                chrome = 0f;
                width = SvWidth + 2f * 10f + 2f;
                height = 10f + SvHeight + 8f + HueHeight + 8f + 28f + 10f + 2f;
            }

            width = Math.Min(width, Math.Max(viewportW - 2f * Margin, 40f));

            float below = viewportH - Margin - (ay + ah + Gap);
            float above = ay - Gap - Margin;
            bool up = height > below && above > below;
            if (Kind == Kinds.Select) height = Math.Min(height, Math.Max(up ? above : below, RowHeight + chrome));

            float top = up ? ay - Gap - height : ay + ah + Gap;
            top = Math.Max(Margin, Math.Min(top, viewportH - Margin - height));
            float left = Math.Max(Margin, Math.Min(ax, viewportW - Margin - width));

            Style(Panel, $"left: {Px(left)}; top: {Px(top)}; width: {Px(width)}; height: {Px(height)}");
            if (List != null) Style(List, $"height: {Px(height - chrome)}");
        }

        // ------------------------------------------------------------------------------------------- layout --

        /// <summary>
        /// Cascade the popup's own document and build its boxes. The scrim comes back as one fixed box the size of
        /// the viewport, ready to be appended to the page's tree - the painter lifts fixed boxes into the top layer,
        /// so it lands above everything the page drew, and the layout places it against the viewport.
        /// </summary>
        internal LayoutNode Build(StyleContext context, out Dictionary<IElement, ComputedStyle> styles)
        {
            styles = StyleResolver.Resolve(Document, Sheet, context, out PseudoStyles generated);
            return DomBuilder.Build(Scrim, styles, generated);
        }

        internal Dictionary<IElement, ComputedStyle> Resolve(StyleContext context) =>
            StyleResolver.Resolve(Document, Sheet, context);

        /// <summary>
        /// The popup's look, from the page's s1 theme tokens. Not the page's stylesheet: a page has no way to address
        /// these elements, and a list that took on whatever `div { }` the page happened to write would look broken in
        /// a different way in every app.
        /// </summary>
        internal const string Css = @"
.scrim { position: fixed; background-color: rgba(0,0,0,0) }
.panel { position: absolute; display: flex; flex-direction: column; padding: 4px; background-color: #1B1D24;
         border: 1px solid #3A3D4A; border-radius: 6px; box-shadow: 0 6px 18px rgba(0,0,0,0.45);
         color: #ECEDF1; font-size: 13px; line-height: 1.2 }
.list { display: flex; flex-direction: column; overflow-y: auto; flex-shrink: 0 }
.opt { display: flex; flex-direction: row; align-items: center; flex-shrink: 0; height: 24px; padding: 0 8px;
       border-radius: 4px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis }
.opt.in-group { padding-left: 18px }
.opt:hover { background-color: rgba(255,255,255,0.08) }
.opt.active { background-color: #5E6AD2; color: #FFFFFF }
.opt.disabled { color: rgba(236,237,241,0.38) }
.opt.disabled:hover { background-color: rgba(0,0,0,0) }
.group { display: flex; flex-direction: row; align-items: center; flex-shrink: 0; height: 22px; padding: 0 8px;
         font-size: 12px; font-weight: bold; color: rgba(236,237,241,0.55); white-space: nowrap }
.panel.color { padding: 10px }
.sv { position: relative; flex-shrink: 0; width: 200px; height: 130px }
.sv-layer { position: absolute; left: 0px; top: 0px; width: 200px; height: 130px; pointer-events: none; border-radius: 4px }
.sv-white { background: linear-gradient(90deg, #FFFFFF, rgba(255,255,255,0)) }
.sv-black { background: linear-gradient(180deg, rgba(0,0,0,0), #000000) }
.sv-cursor { position: absolute; width: 16px; height: 16px; border: 2px solid #FFFFFF; border-radius: 8px;
             box-shadow: 0 0 3px rgba(0,0,0,0.8); pointer-events: none }
.hue { position: relative; display: flex; flex-direction: row; flex-shrink: 0; width: 200px; height: 12px; margin-top: 8px }
.hue-seg { flex-grow: 1; height: 12px; pointer-events: none }
.hue-cursor { position: absolute; top: -3px; width: 8px; height: 18px; border: 2px solid #FFFFFF; border-radius: 3px;
              box-shadow: 0 0 3px rgba(0,0,0,0.8); pointer-events: none }
.row { display: flex; flex-direction: row; align-items: center; flex-shrink: 0; margin-top: 8px; height: 28px }
.swatch { flex-shrink: 0; width: 28px; height: 28px; border: 1px solid #3A3D4A; border-radius: 4px }
.hex { flex-grow: 1; height: 28px; margin-left: 8px; padding: 0 8px; background-color: #111217; color: #ECEDF1;
       border: 1px solid #3A3D4A; border-radius: 4px; font-size: 13px }
";

        private static Stylesheet _sheet;

        internal static Stylesheet Sheet => _sheet ??= CssParser.Parse(Css);

        // ------------------------------------------------------------------------------------------- helpers --

        private IElement Part(string tag, string classes, string part)
        {
            IElement element = Document.CreateElement(tag);
            if (!string.IsNullOrEmpty(classes)) element.SetAttribute("class", classes);
            if (!string.IsNullOrEmpty(part)) element.SetAttribute("data-part", part);
            return element;
        }

        private static void Style(IElement element, string css) => element?.SetAttribute("style", css);

        private static bool IsGroup(IElement element) =>
            element != null && string.Equals(element.LocalName, "optgroup", StringComparison.OrdinalIgnoreCase);

        private static string Px(double value) =>
            value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";

        private static double Clamp(double value, double min, double max) =>
            double.IsNaN(value) ? min : Math.Max(min, Math.Min(max, value));
    }
}
