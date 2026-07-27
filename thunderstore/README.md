# Sideload - Web UIs for Schedule I Mods

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/sideload](https://support.doodesch.de/sideload).

> **A framework, not a feature.** Sideload renders mod interfaces written as `index.html` + `app.css` +
> `app.js` into real Unity UI and TextMeshPro. No browser, no native code, no subprocess. Install it because
> a mod you want depends on it.

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Type](https://img.shields.io/badge/type-framework-orange)

**[Source + docs](https://github.com/DooDesch-Mods/ScheduleOne-Sideload)** · **[Reference app (WhatsDab)](https://github.com/DooDesch-Mods/ScheduleOne-WhatsDab)** · **[Support](https://support.doodesch.de/sideload)**

## Players

Sideload adds no gameplay, no menu and no phone app of its own. Mods that build their interface with it list
it as a dependency and your mod manager installs it for you. It sits idle until one of those mods registers
an app, and every developer tool inside it is off until you switch it on.

Installed by hand? `Sideload.dll` goes in `Mods/`, and `AngleSharp.dll` + `Jint.dll` + `Esprima.dll` go in
`UserLibs/` (the mod also finds them next to itself in `Mods/`). Without them no page will build.

## Mod authors

- **HTML instead of nested panels, CSS instead of `sizeDelta` arithmetic.** Real flexbox, a real cascade,
  real selectors, TextMeshPro text.
- **One call for a phone app.** `Apps.Register(id, bundlePrefix, title)` and your bundle renders behind an
  icon on the in game phone. Registering before Sideload loads is fine; the call replays.
- **A two way bridge.** `s1.call('name', arg)` reaches a C# handler in the same frame and returns its string;
  `app.Emit('name', payload)` pushes back into `s1.on(...)`.
- **Chrome DevTools.** One preference and the real inspector attaches to a page running inside the game:
  console, evaluate, Elements tree, reload from disk.
- **No hard dependency.** The `Sideload.Api` shim is one file with zero references. It is a no op when
  Sideload is absent and lights up when it is there, so your mod ships one DLL either way.
- **`fetch` is default deny.** A page reaches no host until your mod names it with `AllowHost`.

Start here: **[WhatsDab](https://github.com/DooDesch-Mods/ScheduleOne-WhatsDab)** is a complete
chat app for the in game phone, written as three web files, that you can install, read and copy. The
step by step guide, the exact CSS subset and the layout rules that differ from a browser are in the
**[wiki](https://github.com/DooDesch-Mods/ScheduleOne-Sideload/wiki)**.

## Requirements

**Schedule I** (IL2CPP) with **MelonLoader 0.7.3+**. No S1API. Optional: **Mod Manager & Phone App** for the
in-game settings UI.

## Settings

Under `Sideload_01_Main` in `UserData/MelonPreferences.cfg`, or in the Mod Manager & Phone App UI. All of it
is developer tooling and all of it is off by default.

`DevTools` (`false`) turns on the Chrome DevTools Protocol server on `127.0.0.1`; anything that can reach
that port can run code in your pages, so leave it off unless you are building an app. `DevToolsPort`
(`9333`) is the loopback port. `DevToolsAutoOpen` (`true`) opens the browser at the landing page once the
first page mounts. `DevToolsFetchFrontend` (`true`) lets Sideload download the DevTools interface once into
`UserData/`, so the inspector works offline afterwards. `DevToolsFrontend` (empty) overrides that with a
folder holding your own copy.

## License

MIT. See the included LICENSE.md. Bundles AngleSharp (MIT) and Jint with Esprima (BSD-2).
