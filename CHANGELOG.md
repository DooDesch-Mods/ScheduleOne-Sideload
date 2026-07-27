# Changelog

All notable changes to Sideload are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-07-27

First release. Sideload turns a folder of `index.html` / `app.css` / `app.js` into real Unity UI, so a mod
can write its interface the way the web writes interfaces instead of assembling panels by hand. The in game
phone is the first host; the core mounts into any RectTransform.

### Added

- HTML rendering through AngleSharp: the parser, the DOM and the selector engine are the real thing, so
  `querySelectorAll` semantics are correct rather than approximated.
- A CSS cascade with custom properties, `var()`, `!important`, inline styles and specificity ordering.
  Supported: flexbox and absolute positioning, the box model, backgrounds and two stop linear gradients,
  per side borders and per corner radii, outer box shadows, opacity, the text properties, `transform`,
  `transition` and `@media (orientation: ...)`. Units are `px` and `%`.
- A flexbox implementation with the parts that usually get skipped: the automatic content based minimum from
  Flexbox 4.5, the iterative grow/shrink resolution from 9.7, and flex basis measured at the stretched size.
  Border box sizing everywhere, and width never depends on height, which is what keeps a layout pass finite.
- Painting through one UI shader and one shared material: radius, border, shadow and gradient travel in
  vertex channels. Meshes are written straight into a `CanvasRenderer` and converted to linear colour on a
  linear rendering build. Nodes come from a pool.
- Text as TextMeshPro. An element with direct text compiles to a single text leaf, with `b i strong em span`
  and friends turned into TMP rich text inside it, so a sentence with inline markup is one draw and keeps
  its spaces.
- `<input>` and `<textarea>` become real `TMP_InputField` controls, and focus sets the game's `IsTyping` so
  typing in an app does not drive the player.
- JavaScript on Jint 3.1.5 with `ExperimentalFeature.All`: ES2015 through ES2024, a DOM wrapper API,
  `console`, timers driven by the mod's update loop rather than by threads, and a 250 ms budget per handler
  so a runaway loop is one hitched frame instead of a hung game.
- The `s1` bridge. `s1.call(name, arg)` runs a C# handler on the main thread in the same frame and returns
  its string; `app.Emit(name, payload)` reaches `s1.on(...)`. Handlers are keyed per app, so two mods may
  both own a call named `list`.
- `s1.storage`: string key/value per app in `UserData/Sideload/<appId>.json`. Deliberately not the game
  save, so UI state never travels with a save or diverges between co-op peers.
- `fetch` with a default deny allowlist that belongs to the mod, not to the page. Exact host or one `*.`
  wildcard, https only outside loopback, every redirect hop rechecked, 10 s timeout, 4 MB response cap, 8
  requests in flight. A blocked request rejects with the exact `AllowHost` call that is missing.
- `Sideload.Api`, the modder shim: one file, zero references, found by reflection. Every call is a no op
  without the host and is replayed once it appears, so registration is load order proof and a mod that uses
  it needs no hard dependency.
- Bundle resolution with a file override: an embedded resource in the mod's own DLL is the shipped default,
  and a file of the same name under `Mods/<appId>/` wins over it. One mechanism for authoring and for
  players reskinning an app.
- A Chrome DevTools Protocol server on `127.0.0.1`, off by default behind the `DevTools` preference. Attach
  the real DevTools to a page inside the game for console, evaluate, the Elements tree with computed styles,
  and `Page.reload` re-reading the bundle from disk. Optionally served from a local frontend copy so it
  works offline.
- Own Harmony postfix on `HomeScreen.Start` for the phone integration: one panel and one icon per registered
  app, no S1API dependency. The panel is built rather than cloned from a vanilla app, and the icon comes
  from the game's own icon prefab, so neither carries a vanilla app's components onto the phone.
- `icon.png` in an app's bundle becomes its home-screen icon. An app without one gets a flat coloured
  square derived from its id.
- Both phone orientations. An app names the ones it supports in preference order -
  `.Orientation("landscape", "portrait")` - and the first is what it opens in. Naming two is what lets the
  **player** turn the phone with the game's own rotate keys (Q and E out of the box, gamepad triggers too,
  rebindable in the game's own controls). The viewport is re-measured, `@media (orientation: ...)`
  re-evaluated and the page laid out again with its document and script intact. Landscape is 733 x 400 CSS
  pixels, portrait 400 x 733. Naming one locks the app, which is also what saying nothing gets you.
- A "Rotate Phone" line in the game's own key-hint strip while a turnable app is open, built from the game's prompt
  component so it shows the key that is really bound. Nothing is drawn inside the app: that screen belongs
  to whoever wrote it.
- The player's choice is remembered per app in `UserData/Sideload/orientation.json`, and dropped silently if
  a later version of that app no longer supports it.
- `s1.setOrientation(...)` still turns the phone from the page, now refused with a log line when the app
  never declared that orientation. `s1.orientation` reads it back.
- A cancellable `back` event. Right-click and Escape - which the game raises through one chain - reach the
  page first: `document.addEventListener('back', e => e.preventDefault())` keeps the app open so it can step
  back inside itself, exactly as right-click leaves a conversation in the vanilla Messages app. A page that
  does not listen closes, as before. `e.source` is `"rightClick"` or `"escape"`.
- An `orientationchange` event, raised after the page has been laid out at its new shape, with `e.value`
  set to `"portrait"` or `"landscape"`. A layout that changes SHAPE also has state to decide - which of two
  panes to land on after a turn - and a stylesheet cannot decide that.
- Clipping that survives a rotated panel. Scroll areas and form controls used Unity's `RectMask2D`, which
  builds its clip rectangle from world corners in fixed order and so inverts under a rotated ancestor -
  culling every masked child. Text now clips through the same sorted-corner rectangle the box meshes use.
- `<img src="...">` paints a file from the app's bundle. Sized by CSS alone, because the layout runs without
  Unity and cannot open a PNG to learn an intrinsic size; the aspect ratio is preserved inside the box you
  give it, and `color` tints it, so one white glyph serves a dark bar and a light one. Sprites are cached per
  app and dropped on reload.
- `app.Badge(count)` puts an unread count on the app's home-screen icon - the same badge the vanilla apps
  use - and it survives the phone being rebuilt. `app.Notify(title, subtitle)` raises one of the game's own
  phone notifications carrying the app's icon, and `app.IsOnScreen` answers the question that has to come
  first: whether the player is already looking.
- `s1.css`, the game's own design tokens as CSS variables, embedded in Sideload so
  `<link rel="stylesheet" href="s1.css">` resolves for every app of every mod.
- Fail soft error handling: broken HTML, CSS or JavaScript produces a visible error page plus a log entry
  with `file:line`, and a throwing handler kills only that handler. A page is never laid out while its panel
  is hidden either: text is measured by a TextMeshPro probe, TMP initialises in `Awake`, and `Awake` never
  runs on an inactive object - so a page rebuilt off screen used to come back at one character per line.
