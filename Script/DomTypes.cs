using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Sideload.Script
{
    /// <summary>
    /// The DOM's type hierarchy, so `instanceof` answers the way a browser answers it.
    ///
    /// Built in JavaScript rather than out of Jint's object model, for the same reason `fetch`'s Response is
    /// (<see cref="FetchApi"/>): what is wanted here IS a chain of ordinary script constructors, and hand-assembling
    /// one from the host side produces something subtly unlike what `Object.getPrototypeOf` and `instanceof` expect.
    ///
    /// Why it matters, concretely: the whole of react-dom 18 contains exactly ONE `instanceof`, and it is
    /// `b instanceof a.HTMLIFrameElement` in the code that restores focus after a render. With no such global the
    /// expression throws, the throw happens inside a scheduler callback, react catches nothing and reports nothing -
    /// and `createRoot(...).render(...)` returns normally having drawn an empty container. One missing name, and the
    /// failure looks like a renderer that simply does not work.
    ///
    /// Nothing here is a stand-in: an element really is a Node and really is an HTMLElement, and no node is ever an
    /// HTMLIFrameElement because this renderer draws no iframes. Both answers are true.
    /// </summary>
    internal sealed class DomTypes
    {
        /// <summary>
        /// The chain, as the DOM defines it. Only the leaves a page or a framework actually tests for are listed -
        /// the full set runs to a few hundred and every one of them would answer false for everything anyway.
        /// </summary>
        private const string Bootstrap = @"(function () {
  function extend(child, parent) {
    child.prototype = Object.create(parent.prototype);
    child.prototype.constructor = child;
    return child;
  }

  function EventTarget() {}
  function Node() {}                 extend(Node, EventTarget);
  function CharacterData() {}        extend(CharacterData, Node);
  function Text() {}                 extend(Text, CharacterData);
  function Comment() {}              extend(Comment, CharacterData);
  function Document() {}             extend(Document, Node);
  function Element() {}              extend(Element, Node);
  function HTMLElement() {}          extend(HTMLElement, Element);

  var tags = {
    HTMLDivElement: 'div', HTMLSpanElement: 'span', HTMLParagraphElement: 'p',
    HTMLButtonElement: 'button', HTMLInputElement: 'input', HTMLTextAreaElement: 'textarea',
    HTMLSelectElement: 'select', HTMLOptionElement: 'option', HTMLLabelElement: 'label',
    HTMLAnchorElement: 'a', HTMLImageElement: 'img', HTMLUListElement: 'ul',
    HTMLOListElement: 'ol', HTMLLIElement: 'li', HTMLFormElement: 'form',
    HTMLHeadingElement: 'h1', HTMLTableElement: 'table', HTMLCanvasElement: 'canvas',
    // Nothing is ever one of these. They are here because code branches on them and an absent name throws.
    HTMLIFrameElement: null, HTMLVideoElement: null, HTMLAudioElement: null, SVGElement: null,
  };

  // `new CustomEvent('picked', { detail: id })` and `el.dispatchEvent(evt)`, which is how one part of a page
  // tells another that something happened without the two knowing about each other. Only `type` reaches the
  // listeners - see ScriptHost.DispatchFromScript for why nothing else could be honest here.
  function Event(type, init) {
    init = init || {};
    this.type = String(type);
    this.bubbles = !!init.bubbles;
    this.cancelable = !!init.cancelable;
    this.defaultPrevented = false;
  }
  Event.prototype.preventDefault = function () { if (this.cancelable) this.defaultPrevented = true; };
  Event.prototype.stopPropagation = function () {};
  Event.prototype.stopImmediatePropagation = function () {};

  function CustomEvent(type, init) { Event.call(this, type, init); this.detail = init ? init.detail : null; }
  extend(CustomEvent, Event);

  var byTag = {};
  var globals = {
    EventTarget: EventTarget, Node: Node, CharacterData: CharacterData, Text: Text,
    Comment: Comment, Document: Document, Element: Element, HTMLElement: HTMLElement,
    Event: Event, CustomEvent: CustomEvent,
  };

  for (var name in tags) {
    var ctor = new Function('return function ' + name + '() {}')();
    extend(ctor, HTMLElement);
    globals[name] = ctor;
    if (tags[name]) byTag[tags[name]] = ctor.prototype;
  }

  for (var key in globals) globalThis[key] = globals[key];

  return {
    node: Node.prototype,
    text: Text.prototype,
    comment: Comment.prototype,
    document: Document.prototype,
    element: HTMLElement.prototype,
    byTag: byTag,
  };
})()";

        private readonly ObjectInstance _byTag;

        internal DomTypes(Engine engine)
        {
            var built = engine.Evaluate(Bootstrap).AsObject();

            Node = built.Get("node").AsObject();
            Text = built.Get("text").AsObject();
            Comment = built.Get("comment").AsObject();
            Document = built.Get("document").AsObject();
            Element = built.Get("element").AsObject();
            _byTag = built.Get("byTag").AsObject();
        }

        internal ObjectInstance Node { get; }

        internal ObjectInstance Text { get; }

        internal ObjectInstance Comment { get; }

        internal ObjectInstance Document { get; }

        /// <summary>HTMLElement's prototype - the fallback for a tag with no interface of its own, which is most of
        /// them and is what a browser does too.</summary>
        internal ObjectInstance Element { get; }

        /// <summary>The prototype for one tag: `document.createElement('input') instanceof HTMLInputElement` is true
        /// in a browser, and form code branches on exactly that.</summary>
        internal ObjectInstance For(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return Element;

            JsValue proto = _byTag.Get(tag.ToLowerInvariant());
            return proto.IsObject() ? proto.AsObject() : Element;
        }
    }
}
