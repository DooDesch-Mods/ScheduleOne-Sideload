using Jint;

namespace Sideload.Script
{
    /// <summary>
    /// Web globals that are written in JavaScript rather than in C#.
    ///
    /// Everything here could be a CLR class handed to Jint, and none of it should be. A CLR wrapper exposes CLR
    /// shapes: `for (const [name, value] of form)` needs a real iterator, `formData.entries()` needs a real generator,
    /// and a page is entitled to subclass what it is given. Written as script it is simply the object the web
    /// specifies, and the engine has one less interop seam to keep honest. <see cref="FetchApi"/> builds its Response
    /// the same way and for the same reason.
    /// </summary>
    internal static class WebApis
    {
        /// <summary>
        /// `FormData`, including the constructor a form is passed to.
        ///
        /// This is what React 19 form actions are built on: the action receives a FormData assembled from the form
        /// that submitted, and reads its fields by name. Without it there is no way to write one.
        ///
        /// The constructor walks the form's own controls exactly as HTML says a submission does - a control with no
        /// name is not submitted, a disabled one is not submitted, and a checkbox or radio is submitted only while it
        /// is on. Getting any of those wrong produces a form that posts fields the server never sees on the web, which
        /// is worse than not having FormData at all.
        /// </summary>
        private const string FormDataSource = @"(function (global) {
  var ENTRIES = Symbol('entries');

  function pairs(form) {
    var out = [];
    if (!form || !form.querySelectorAll) return out;

    var controls = form.querySelectorAll('input, textarea, select');
    for (var i = 0; i < controls.length; i++) {
      var el = controls[i];
      var name = el.getAttribute('name');
      if (!name || el.hasAttribute('disabled')) continue;

      var type = (el.getAttribute('type') || '').toLowerCase();
      if (type === 'submit' || type === 'reset' || type === 'button' || type === 'file' || type === 'image') continue;
      if ((type === 'checkbox' || type === 'radio') && !el.hasAttribute('checked')) continue;

      var value = el.tagName === 'TEXTAREA' ? el.textContent : el.getAttribute('value');
      if (value === null || value === undefined) value = (type === 'checkbox' || type === 'radio') ? 'on' : '';
      out.push([name, String(value)]);
    }
    return out;
  }

  function FormData(form) {
    if (!(this instanceof FormData)) throw new TypeError(""FormData requires 'new'"");
    this[ENTRIES] = pairs(form);
  }

  FormData.prototype.append = function (name, value) { this[ENTRIES].push([String(name), String(value)]); };

  FormData.prototype.set = function (name, value) {
    var key = String(name), wrote = false, kept = [];
    for (var i = 0; i < this[ENTRIES].length; i++) {
      if (this[ENTRIES][i][0] !== key) { kept.push(this[ENTRIES][i]); continue; }
      if (wrote) continue;
      kept.push([key, String(value)]);
      wrote = true;
    }
    if (!wrote) kept.push([key, String(value)]);
    this[ENTRIES] = kept;
  };

  FormData.prototype.get = function (name) {
    var key = String(name);
    for (var i = 0; i < this[ENTRIES].length; i++) if (this[ENTRIES][i][0] === key) return this[ENTRIES][i][1];
    return null;
  };

  FormData.prototype.getAll = function (name) {
    var key = String(name), out = [];
    for (var i = 0; i < this[ENTRIES].length; i++) if (this[ENTRIES][i][0] === key) out.push(this[ENTRIES][i][1]);
    return out;
  };

  FormData.prototype.has = function (name) { return this.get(name) !== null; };

  FormData.prototype['delete'] = function (name) {
    var key = String(name), kept = [];
    for (var i = 0; i < this[ENTRIES].length; i++) if (this[ENTRIES][i][0] !== key) kept.push(this[ENTRIES][i]);
    this[ENTRIES] = kept;
  };

  FormData.prototype.forEach = function (fn, self) {
    for (var i = 0; i < this[ENTRIES].length; i++) fn.call(self, this[ENTRIES][i][1], this[ENTRIES][i][0], this);
  };

  // An array's own iterator rather than a generator. Generators work in this engine - `function* g(){...}` iterates
  // exactly as it should - but one that reads `this`, which a method on a prototype must, yields a single value: its
  // LAST. So every one of these read as a one-item form, and a two-field form posted one field. An array iterator is
  // an iterator, so `for...of`, spread and Array.from all take it, which is the whole contract.
  function walk(list) { return list[Symbol.iterator](); }

  FormData.prototype.entries = function () {
    var out = [];
    for (var i = 0; i < this[ENTRIES].length; i++) out.push([this[ENTRIES][i][0], this[ENTRIES][i][1]]);
    return walk(out);
  };

  FormData.prototype.keys = function () {
    var out = [];
    for (var i = 0; i < this[ENTRIES].length; i++) out.push(this[ENTRIES][i][0]);
    return walk(out);
  };

  FormData.prototype.values = function () {
    var out = [];
    for (var i = 0; i < this[ENTRIES].length; i++) out.push(this[ENTRIES][i][1]);
    return walk(out);
  };

  FormData.prototype[Symbol.iterator] = FormData.prototype.entries;

  // What `fetch(url, { body: formData })` sends. A browser would encode multipart and name a boundary; this host
  // reads the body as text, so the form is urlencoded and the request carries the matching content type. Files are
  // the one thing that shape cannot express, which is why the constructor skips `type=file` rather than putting a
  // filename where a browser would put bytes.
  FormData.prototype.toString = function () {
    var parts = [];
    for (var i = 0; i < this[ENTRIES].length; i++)
      parts.push(encodeURIComponent(this[ENTRIES][i][0]) + '=' + encodeURIComponent(this[ENTRIES][i][1]));
    return parts.join('&');
  };

  global.FormData = FormData;
})(globalThis);";

        /// <summary>Define them on the page's global object. Runs once per engine, before the page's own script.</summary>
        internal static void Install(Engine engine)
        {
            if (engine == null) return;

            try { engine.Execute(FormDataSource); }
            catch (Exception e) { Model.Platform.Warning("[Sideload] installing the web globals failed: " + e.Message); }
        }

        /// <summary>
        /// Whether a value handed to `fetch` as a body is a FormData.
        ///
        /// Asked so the request can carry the content type the body is actually in. A server that is told nothing
        /// reads `a=1&amp;b=2` as an opaque string and every field arrives empty, which looks like a bug in the page.
        /// </summary>
        internal static bool IsFormData(Engine engine, Jint.Native.JsValue value)
        {
            if (value == null || !value.IsObject()) return false;

            try
            {
                // By its constructor's name rather than `instanceof`, because a page is allowed to build its own
                // FormData - a bundler that ships a polyfill produces one that is not this one - and what the body
                // needs to know is what shape it is in, not which class it came from.
                Jint.Native.JsValue ctor = value.AsObject().Get("constructor");
                return ctor.IsObject() && ctor.AsObject().Get("name").ToString() == "FormData";
            }
            catch { return false; }
        }
    }
}
