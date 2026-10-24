using Jint;
using Jint.Native;

namespace Sideload.Script
{
    /// <summary>
    /// The <c>s1api</c> global: game data for a page without a C# handler of its own, when S1API is installed.
    ///
    /// Two halves, and only this one is always there. The other lives in a separate assembly that ships inside
    /// Sideload.dll and is loaded only when S1API is (Integrations/S1ApiLoader.cs): it registers handlers named
    /// <c>s1api.*</c> for every app and pushes <c>s1api:&lt;topic&gt;</c> events. This half is the script every page
    /// gets, which turns those handlers into properties - <c>s1api.money.cash</c> rather than
    /// <c>JSON.parse(s1.call('s1api.money')).cash</c>.
    ///
    /// Nothing crosses but strings, the same as every other call: no CLR object, and so no IL2CPP object, ever reaches
    /// a page. Without S1API the object still exists, says <c>available: false</c> and answers null everywhere, so a
    /// page can show a hint instead of dying on its first line.
    ///
    /// Two kinds of area. The curated ones (money, time, leveling, law, player, deliveries, products) are written by
    /// hand below. Every other static property, reading method and event of S1API is generated at build time from the
    /// S1API DLL (tools/S1ApiGen) and arrives here as <see cref="Surface"/>: area -> property, method and write names.
    /// A generated area joins a curated one of the same name, and a curated key wins.
    ///
    /// Cost when unused: one small prepared script per page, parsed once per process, and no call into C# until a
    /// property is read. The surface is one JSON string parsed once per page.
    /// </summary>
    internal static class S1Api
    {
        /// <summary>The version of the PAGE-FACING surface, not of S1API. Bumped when a property or topic is added, so
        /// a page can ask for what it needs.</summary>
        internal const int Version = 2;

        /// <summary>Set by the loader once the integration has registered its handlers.</summary>
        internal static bool Available;

        /// <summary>The generated areas, from the resource the build embeds next to the integration. Set by the loader
        /// whether or not S1API is installed; null in a build without it, and then pages get the curated areas only.</summary>
        internal static string Surface;

        private static Prepared<Esprima.Ast.Script>? _prepared;

        /// <summary>Define <c>s1api</c> on a fresh engine. Called right after <c>s1</c> itself.</summary>
        internal static void Install(Engine engine)
        {
            if (engine == null) return;

            try
            {
                _prepared ??= Engine.PrepareScript(Prelude, "s1api.js");

                JsValue factory = engine.Evaluate(_prepared.Value);
                JsValue api = engine.Invoke(factory, Available, Version, engine.GetValue("s1"), Surface ?? "");
                engine.SetValue("s1api", api);
            }
            catch (Exception e)
            {
                Model.Platform.Warning("s1api could not be set up for this page: " + e.Message);
            }
        }

        /// <summary>
        /// The page-side script. Evaluates to a function, so nothing but <c>s1api</c> itself lands in the page's
        /// global scope. Written in ES5 on purpose: it runs in every page, and the cheapest parse is the plainest one.
        /// </summary>
        internal const string Prelude = @"(function () {
  return function (available, version, s1, surface) {
    function read(name, arg) {
      if (!available) return null;
      var raw = s1.call('s1api.' + name, arg === undefined || arg === null ? '' : String(arg));
      if (!raw) return null;
      try { return JSON.parse(raw); } catch (e) { return null; }
    }

    function write(name, arg) {
      if (!available) return false;
      return s1.call('s1api.' + name, arg === undefined || arg === null ? '' : String(arg)) === 'ok';
    }

    function area(name, keys, extra) {
      var o = {};
      keys.forEach(function (key) {
        Object.defineProperty(o, key, {
          enumerable: true,
          get: function () { var v = read(name); return v === null || v[key] === undefined ? null : v[key]; }
        });
      });
      o.get = function () { return read(name); };
      if (extra) for (var k in extra) o[k] = extra[k];
      return o;
    }

    var listeners = {};
    var wired = {};

    function wire(topic) {
      if (wired[topic]) return true;
      if (!available || s1.call('s1api.watch', topic) !== 'ok') return false;
      wired[topic] = true;
      s1.on('s1api:' + topic, function (payload) {
        var value = null;
        if (payload) { try { value = JSON.parse(payload); } catch (e) { value = null; } }
        var list = (listeners[topic] || []).slice();
        for (var i = 0; i < list.length; i++) list[i](value);
      });
      return true;
    }

    var api = {
      available: !!available,
      version: version,

      money: area('money', ['cash', 'online', 'netWorth'], {
        change: function (amount) { return write('money.change', amount); }
      }),

      time: area('time', ['day', 'dayIndex', 'time', 'hour', 'minute', 'formatted', 'night', 'endOfDay', 'elapsedDays', 'sleeping']),

      leveling: area('leveling', ['rank', 'rankIndex', 'tier', 'xp', 'totalXp', 'xpToNextTier'], {
        addXp: function (amount) { return write('leveling.addXp', amount); }
      }),

      law: area('law', ['wantedLevel', 'wanted', 'investigated', 'curfewEnabled', 'curfewActive', 'intensity']),

      player: area('player', ['name', 'health', 'maxHealth', 'dead', 'arrested', 'inVehicle', 'region', 'property']),

      deliveries: { list: function () { return read('deliveries') || []; } },

      products: {
        list: function () { return read('products') || []; },
        listed: function () { return (read('products') || []).filter(function (p) { return p.listed; }); }
      },

      on: function (topic, fn) {
        if (typeof fn !== 'function' || !topic) return false;
        topic = String(topic);
        if (!wire(topic)) return false;
        (listeners[topic] = listeners[topic] || []).push(fn);
        return true;
      },

      off: function (topic, fn) {
        var list = listeners[String(topic)];
        if (!list) return;
        for (var i = list.length - 1; i >= 0; i--) if (!fn || list[i] === fn) list.splice(i, 1);
      },

      schema: function () { return read('schema'); }
    };

    Object.defineProperty(api.time, 'now', { enumerable: false, get: function () { return read('time'); } });
    Object.defineProperty(api.leveling, 'current', { enumerable: false, get: function () { return read('leveling'); } });

    // The generated areas. p: properties, read fresh on every access; m: reading methods; w: writes, which take
    // their arguments as a JSON array and answer true only when the app's mod allowed the scope.
    var generated = {};
    if (surface) { try { generated = JSON.parse(surface); } catch (e) { generated = {}; } }

    function prop(o, path, key) {
      Object.defineProperty(o, key, { enumerable: true, get: function () { return read(path + '.' + key); } });
    }
    function reader(o, path, key) {
      o[key] = function () { return read(path + '.' + key); };
    }
    function writer(o, path, key) {
      o[key] = function () { return write(path + '.' + key, JSON.stringify(Array.prototype.slice.call(arguments))); };
    }

    Object.keys(generated).forEach(function (name) {
      var d = generated[name] || {};
      var o = api[name];
      if (o === undefined) {
        o = api[name] = {};
        o.get = function () { return read(name); };
      } else if (o === null || typeof o !== 'object') {
        return;
      }
      (d.p || []).forEach(function (key) { if (!(key in o)) prop(o, name, key); });
      (d.m || []).forEach(function (key) { if (!(key in o)) reader(o, name, key); });
      (d.w || []).forEach(function (key) { if (!(key in o)) writer(o, name, key); });
    });

    return api;
  };
})()";
    }
}
