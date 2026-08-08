# Sideload - Web UIs for Schedule I Mods

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/sideload](https://support.doodesch.de/sideload).

> Mod interfaces, written as `index.html` + `app.css` + `app.js` instead of hand assembled uGUI panels.
> Sideload parses the HTML, resolves the CSS, runs the JavaScript and renders the result as real Unity UI
> objects and TextMeshPro text. No browser, no native code, no subprocess. The in game phone is the first
> place it mounts, not the foundation.

![Version](https://img.shields.io/badge/version-1.10.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Type](https://img.shields.io/badge/type-framework-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)

**[Reference app (WhatsDab)](https://github.com/DooDesch-Mods/ScheduleOne-WhatsDab)** · **[Documentation](https://docs.doodesch.de/mods/sideload/)** · **[Support](https://support.doodesch.de/sideload)**

## For players

You install Sideload because another mod needs it. It is a framework: on its own it adds no gameplay, no
menu and no phone app of its own. Mods that build their interface with it list it as a dependency, and a mod
manager pulls it in for you. It does nothing until such a mod registers an app, and every developer tool in
it is off unless you switch it on. It is a single file - there is nothing else to place.

## For mod authors

- **HTML instead of nested panels, CSS instead of `sizeDelta` arithmetic.** A real flexbox implementation, a
  real cascade, real selectors. The reference app is 518 lines of HTML/CSS/JS where the hand built uGUI
  version of the same screen was 781 lines of C#.
- **A phone app in one call.** `Apps.Register(id, bundlePrefix, title)` puts an icon on the in game phone and
  renders your bundle behind it. Load order proof: register before Sideload has loaded and the call replays
  once it appears.
- **A two way bridge.** `s1.call('name', arg)` reaches a C# handler on the Unity main thread in the same
  frame and returns its string. `app.Emit('name', payload)` pushes the other way into `s1.on(...)`.
- **Chrome DevTools.** Turn on one preference and Sideload speaks the Chrome DevTools Protocol on
  `127.0.0.1`, so you attach the real inspector to a page running inside the game: console, evaluate,
  Elements tree with computed styles, and a reload that re-reads your files from disk.
- **Your files, editable after shipping.** The web bundle is embedded in your DLL, and any file of the same
  name under `Mods/<appId>/` wins over it. That is one mechanism for your dev loop and for players reskinning
  your app.
- **No hard dependency.** `Sideload.Api` is one file with zero references. It finds the host by reflection,
  so every call is a no op when Sideload is not installed and lights up when it is.
- **`fetch`, default deny.** A page reaches no host until your mod names it with `AllowHost`. The page cannot
  add to that list, because the page is data a player can edit.
- **Ships as one DLL.** Your mod references no Unity assembly, no IL2CPP interop and not Sideload itself.

## Requirements

| Component | Version / Source |
|-----------|------------------|
| Schedule I | IL2CPP (current Steam public build) |
| MelonLoader | `0.7.3+` |

Sideload has no other hard dependency. It does **not** use S1API: it patches `HomeScreen.Start` itself,
because S1API discovers phone apps by type and Sideload declares them at runtime.

## Installation

### Recommended: a Thunderstore mod manager

Install with a mod manager (r2modman / Gale) from the Schedule I community; MelonLoader is pulled in
automatically.

### Manual

1. Install **MelonLoader 0.7.3** for Schedule I.
2. Drop **`Sideload.dll`** into your Schedule I `Mods/` folder.

That is the whole install. AngleSharp, Jint and Esprima are inside `Sideload.dll`. Older versions shipped them
as loose files in `UserLibs/`; if yours are still there they are used in preference to the built-in copies and
nothing breaks, so you can delete them whenever you like.

## Configuration

Settings live in `UserData/MelonPreferences.cfg` under
`Sideload_01_Main`. Apart from `AppKeys` everything here is a developer tool and is off by default.

| Setting | Default | What it does |
|---|---|---|
| `AppKeys` | `true` | ON: an app may ask for a key that reaches it with the phone in your pocket - press it and the app comes up ready to use. Only a key the app asked for is read, only while the game would let you take your phone out anyway, and never while you are typing, paused, or in a station, shop or the console. OFF: no app gets a key and you open everything from the home screen. |
| `DevTools` | `false` | OFF: nothing listens and no page can be inspected from outside the game. ON: Sideload runs a Chrome DevTools Protocol server on `127.0.0.1` so you can attach the real DevTools UI to a mounted page - console, evaluate, Elements tree. Anything that can reach the port can run code in your pages, so leave this off unless you are building an app. |
| `DevToolsPort` | `9333` | The loopback port the devtools server listens on. Change it only if 9333 clashes with another tool. Clamped 1024-65535. |
| `DevToolsAutoOpen` | `true` | ON: once the first page is mounted, Chrome (or Edge) opens at the devtools landing page, where one click attaches the inspector. OFF: the address is only written to the log. Has no effect while `DevTools` is off. |
| `DevToolsFrontend` | `(empty)` | EMPTY: Sideload uses its own copy under `UserData/Sideload/devtools-frontend`, downloading it once if `DevToolsFetchFrontend` allows, and falls back to Google's servers otherwise. Set this to a folder holding your own copy of the frontend (for example `node_modules/@react-native/debugger-frontend/dist/third-party/front_end`) to override all of that and serve yours instead. Nothing about your page leaves the machine in any case, because the frontend is static JavaScript talking to `127.0.0.1`. |
| `DevToolsFetchFrontend` | `true` | ON: the first time you switch `DevTools` on, Sideload downloads the DevTools interface in the background (the npm package `@react-native/debugger-frontend`, about 4.5 MB over the wire and 16 MB on disk) into `UserData/Sideload/devtools-frontend`. Once per machine, never while `DevTools` is off, and never blocking the game: DevTools works from Google's servers until the copy lands and offline afterwards. OFF: nothing is downloaded and the interface comes from Google's servers every time, which needs internet. |

A self-test app, a file watcher for live reload and an F9 overlay exist only in development builds and are
not shipped in the release. On a released build the authoring loop is `Page.reload` from an attached
DevTools, which re-reads the bundle from disk.

## Build an app

The smallest thing that runs. Four files, no Unity reference, no reference to Sideload itself.

### `MyMod.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>MyMod</RootNamespace>
    <AssemblyName>MyMod</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="MelonLoader">
      <HintPath>path\to\MelonLoader.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <!-- The modder shim as source: one file, one DLL to ship, no hard dependency on Sideload. -->
  <ItemGroup>
    <Compile Include="path\to\Sideload.cs" Link="Sideload.Api.cs" />
  </ItemGroup>

  <!-- The web bundle. This LogicalName prefix IS the bundlePrefix you pass to Apps.Register. -->
  <ItemGroup>
    <EmbeddedResource Include="Assets/mystash/*">
      <LogicalName>MyMod.Assets.mystash.%(Filename)%(Extension)</LogicalName>
    </EmbeddedResource>
  </ItemGroup>

</Project>
```

The `LogicalName` is load bearing. Without it MSBuild mangles the folder names into the resource name and
`Apps.Register` finds nothing.

### `Core.cs`

```csharp
using MelonLoader;
using Sideload.Api;

[assembly: MelonInfo(typeof(MyMod.Core), "MyMod", "1.0.0", "You")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace MyMod
{
    public class Core : MelonMod
    {
        private static readonly List<string> Items = new List<string>();

        public override void OnInitializeMelon()
        {
            Apps.Register(
                    id: "mystash",                          // also the folder under Mods/ that overrides the bundle
                    bundlePrefix: "MyMod.Assets.mystash",   // the LogicalName prefix from the csproj
                    title: "Stash",
                    iconLabel: "Stash")
                .OnCall("items.list", _ => string.Join("\n", Items))
                .OnCall("items.add", text =>
                {
                    if (string.IsNullOrWhiteSpace(text)) return "error";
                    Items.Add(text.Trim());
                    return "ok";
                });
        }
    }
}
```

### `Assets/mystash/index.html`

No `<html>`, `<head>` or `<body>` boilerplate needed.

```html
<link rel="stylesheet" href="app.css">

<div class="screen">
  <header class="bar">
    <span class="title">Stash</span>
    <span class="badge" id="count">0</span>
  </header>

  <div class="list" id="items"></div>

  <div class="row">
    <input class="field" id="entry" placeholder="Add an item" maxlength="60">
    <button class="btn" id="add">Add</button>
  </div>
</div>

<script src="app.js"></script>
```

### `Assets/mystash/app.css`

```css
body {
  /* Required. The root is auto height, so a percentage below has no basis without this. */
  height: 100%;
  font-family: game-ui;
  font-size: 15px;
  color: #ECEDF1;
}

.screen { height: 100%; padding: 16px; gap: 12px; background: #101218; }

/* Every box is a flex container and the default direction is column, not row. */
.bar    { flex-direction: row; align-items: center; justify-content: space-between; }
.title  { font-size: 22px; font-weight: 700; }
.badge  { min-width: 26px; padding: 2px 8px; border-radius: 11px; background: #5E6AD2;
          font-size: 12px; text-align: center; align-items: center; }

/* min-height: 0 is what lets this shrink and scroll instead of growing the page. */
.list   { flex: 1; min-height: 0; overflow: auto; gap: 6px; }
.item   { padding: 8px 10px; border-radius: 8px; background: #161922; }

.row    { flex-direction: row; align-items: center; gap: 8px; }
.field  { flex: 1; height: 34px; padding: 0 12px; border-radius: 17px;
          background: #161922; border: 1px solid #2A2E3A; color: #ECEDF1; align-items: center; }
.btn    { width: 80px; height: 34px; border-radius: 17px; background: #5E6AD2;
          font-weight: 600; text-align: center; align-items: center; }
.btn:hover  { background: #6E79E0; }
.btn:active { background: #4A55B8; }
```

### `Assets/mystash/app.js`

```js
const $ = (id) => document.getElementById(id);

function render() {
  const items = s1.call('items.list').split('\n').filter(Boolean);
  const box = $('items');
  box.replaceChildren();

  for (const text of items) {
    const row = document.createElement('div');
    row.className = 'item';
    row.textContent = text;
    box.appendChild(row);
  }

  $('count').textContent = String(items.length);
}

$('add').addEventListener('click', () => {
  const text = $('entry').value.trim();
  if (!text || s1.call('items.add', text) !== 'ok') return;
  $('entry').value = '';
  render();
});

render();
```

### `Assets/mystash/icon.png`

Optional, and worth the five minutes. A square PNG - 256x256 is plenty - drawn like the vanilla app icons:
a rounded square in a flat colour, a white glyph on top, transparent outside the corners. Without one the
app gets a plain coloured square derived from its id, which is legible but says nothing.

Build it, drop `MyMod.dll` into `Mods/`, open the phone. Full guide, the exact CSS subset, the layout rules
that differ from a browser and the edge cases worth knowing before you hit them: the
**[wiki](https://docs.doodesch.de/mods/sideload/)**. A complete, working mod to copy from:
**[WhatsDab](https://github.com/DooDesch-Mods/ScheduleOne-WhatsDab)**.

## Sideload.Api, the modder shim

`Sideload.Api` is not a separate download and is not a Thunderstore package. It is one file,
[`Sideload.Api/Sideload.cs`](Sideload.Api/Sideload.cs), with **zero references**: no MelonLoader, no
Unity, no IL2CPP interop. Take it either way:

- **Drop the file in** and compile it into your mod, as the csproj above does. Nothing extra to ship.
- **Reference `Sideload.Api.dll`** if you prefer a binary. It behaves identically, but then it is a second
  assembly you have to install alongside your mod.

It finds the host through `Sideload.Bridge.SideloadBridge` by reflection and binds plain BCL delegates, so
the two assemblies share no type. Without Sideload installed nothing binds and every call is a no op, which
is why you can ship it with a soft dependency and only check `Apps.Available` if you want a fallback UI.

The whole surface:

```csharp
AppHandle app = Apps.Register(id, bundlePrefix, title, iconLabel, hostAssembly);

app.OnCall("name", arg => answer);   // answers s1.call('name', arg); returns the handle, so it chains
app.Emit("name", payload);           // reaches s1.on('name', payload => ...)
app.AllowHost("api.example.com");    // one host this app's fetch may reach; "*.example.com" for subdomains
app.Orientation("landscape", "portrait");  // the ways round it may be held; the first is how it opens,
                                           // naming two lets the player turn it. Default: landscape only.

app.Badge(3);                        // the unread count on the home screen icon; 0 clears it
app.Notify("Jessi Waters", "on my way");   // one of the game's own phone notifications, with your icon
app.Image("avatar/76561198", pngBytes);    // a runtime picture the page draws with src="s1://avatar/76561198"

app.NoIcon();                        // no home screen icon: your mod supplies the way in
app.Icon(consoleIsOn);               // ...or put the icon there and take it away while the game runs
app.Open(); app.Close();             // open it exactly as pressing its icon would, or close it
bool open = app.IsOpen;              // is it the app the phone currently has open

app.Show(); app.Hide();              // take the phone out AND open the app, or reverse both
PhoneScreen.Raise(); PhoneScreen.Lower();   // move the phone on its own, no app involved

app.OnKey("Enter", key => app.Show());     // a key that reaches you with the phone still in the pocket;
                                           // return false to pass it to the next app that wants it
bool up = PhoneScreen.IsRaised;      // is the phone out and on its phone screen

bool looking = app.IsOnScreen;       // is your app the one the phone is showing right now
bool here = Apps.Available;          // only needed if you want to build a fallback UI
bool newEnough = AppHandle.CanOpenProgrammatically;   // does the installed Sideload understand the three above
```

`Show()` is refused while the game is paused or the player is asleep, dead or arrested - it returns false and
your app stays shut, which is the answer a key-driven app wants. Opening an app never raises the phone by
itself, so a background update cannot yank it out of the player's pocket.

`NoIcon()` and `Open()` belong together: an app with no icon can only be reached from code, so a mod that
takes that route should check `CanOpenProgrammatically` first and refuse to register against an older host
rather than leave the player an app with no way in.

Everything is queued while the host is absent and replayed once it appears, so the load order between your
mod and Sideload never matters. Strings cross the boundary in both directions; send JSON for anything
structured. `s1.call` is synchronous and returns a string, not a Promise.

## What the engine renders

Honest limits, because a browser sets different expectations:

- **CSS:** flexbox, absolute positioning, and `position: fixed` as a top layer (measured against the
  screen, drawn over everything else, and it takes the pointer - which is how an app gets a modal), the
  box and paint properties, text properties, custom
  properties with `var()`, `transform` and `transition`, `overflow` (`hidden` clips, `auto` and `scroll`
  clip and scroll), `z-index` on a positioned box (it orders siblings inside one parent; there are no
  stacking contexts, so it cannot lift a box out of the subtree it was written in), and
  `@media (orientation: ...)`. Units are `px` and `%` only. No Grid, no `float`, no `em`/`rem`/`vh`/`vw`, no `calc()`, no `hsl()`. Anything
  unsupported is named in the log, once per app, so a rule that never took effect is findable instead
  of a mystery.
- **Text that has to line up.** `font-family: monospace` draws in a real monospaced face - the game ships none,
  so Sideload builds one from the machine's own font file (Consolas first, then Cascadia Mono, Lucida Console,
  Courier New, DejaVu Sans Mono) and the log says which it took. Nothing is shipped with the mod. Where none of
  them exists, the game's pixel face steps in, and then `white-space: pre` plus `-s1-mono-advance: 7px` are what
  keep a column straight: the first keeps the spaces you wrote, the second gives every glyph the same advance. `pre` also tells the layout that a block is text, so one built from
  nothing but coloured spans stays a single row instead of one full-width box per span.
- **Text fields.** `caret-color` and `-s1-caret-width` draw the cursor - a block cursor is two lines of CSS.
  `data-ghost="rest"` writes the rest of a suggestion behind the caret in the field's own font, styled by
  `-s1-ghost-color`; nothing has to be measured and no monospaced font is needed.
- **Selectors:** everything AngleSharp's `querySelectorAll` accepts, plus the state pseudo-classes `:hover`,
  `:active`, `:focus`, `:disabled` on the last compound. State rules repaint, they do not re-lay-out.
- **`::before` and `::after`.** A rule that sets `content` grows a real box as the element's first or last
  child - `content: ""` with a size and a background is the badge dot, the divider, the overlay; a string or
  `attr(data-x)` is text. Without `content` there is no box. Write two colons: `:before` generates nothing.
  The box is a flex item of its element rather than inline, so it stacks the way that element stacks. No
  counters, no images, and no other pseudo-element.
- **JavaScript:** ES2015 through ES2024 on Jint, including `#private` fields, optional chaining and
  generators. Globals are `document`, `s1`, `console`, `fetch`, `Promise` and the four timer functions.
  There is no `window` and no `localStorage`; `s1.storage` replaces the latter.
- **Both orientations.** Declare them with `.Orientation("landscape", "portrait")` and the **player** turns
  the phone with the game's rotate keys; Sideload explains them in the game's own key strip, not in your app.
  The viewport is 733 x 400 CSS pixels one way and 400 x 733 the other, and `@media (orientation: ...)`
  decides the layout. A turn keeps the document and the script - it is not a reload. `s1.orientation` reads
  it, `s1.setOrientation(v)` turns it, and the choice is remembered per app without you storing anything.
  A page whose SHAPE changes with the orientation also gets `orientationchange` (`e.value` is the new one),
  because which of two panes the player should land on is a question a stylesheet cannot answer.
- **Events:** `click`, `input`, `keydown`, `dragstart` / `drag` / `dragend`, `wheel`,
  `back` and `orientationchange`. Others are not dispatched, however plausible the name. Right-click and
  Escape both raise `back` at the document; `preventDefault()` keeps the app open so a page can step back
  inside itself, and not taking it closes the app. `e.source` is `"rightClick"` or `"escape"`.
- **An app can ask for keys.** Name them on a text field with `data-keys="Tab ArrowUp Ctrl+R"` and they arrive
  as `keydown` with `ctrlKey`, `shiftKey`, `altKey`, `repeat` and `hasSelection` - the last one is how a page
  tells Ctrl+C-as-copy from Ctrl+C-as-interrupt. Only the keys an app names are taken from the field, so
  Ctrl+Backspace can delete a word while plain Backspace stays the field's own. Holding a key repeats it after
  0.35 s, then every 0.06 s, dropping to 0.03 s after 1.2 s. Enter and Escape are refused: they already arrive
  as `keydown` and `back`. `data-reject-first` keeps a dead key from opening a fresh line.
- **One box can keep the keyboard.** Put `data-typing` on an `<input>` and, while that field is painted and on
  screen, the caret comes back to it whenever nothing else in the page has it. Without it a chat where the
  player has not clicked the message box is a chat where typing "hello" walks them forward, crouches them and
  swaps two inventory slots - a field only holds `GameInput.IsTyping` while it has the caret, and every other
  key is a game binding. It never reaches past its own app: a control the player clicked keeps the caret, and
  the game's console or a vanilla dialog keeps it too. Escape and right-click still leave on the first press.
- **A key can be the way IN.** `app.OnKey("Enter", key => ...)` reaches your mod with the phone still in the
  player's pocket, so one press can raise it with your app already open - what `data-keys` cannot do, because
  it needs a page that is on screen. Spelling is the same, `Escape` is refused, and Sideload only reads the
  key where the game's own phone key would work: never while typing, paused, asleep, arrested, or at a
  station, a shop or the console. When two apps want one key it goes to whichever notified last, and an app
  that is on screen keeps its keys regardless; returning `false` passes the press to the next one. The player
  can switch the whole mechanism off with `AppKeys` in `MelonPreferences.cfg`.
- **Where the pointer landed.** A `click` carries `e.offsetX` / `e.offsetY` in the element's own CSS pixels
  and `e.normX` / `e.normY` as a 0..1 fraction of its size - enough for a page that stands for a space of its
  own, a map, to answer "what did they point at". A `drag` carries `e.deltaX` / `e.deltaY` since the last
  one, and a `wheel` carries `e.wheelDelta`.
- **Dragging costs nothing extra only if you move things the fast way.** Writing `transform`, a background,
  a border colour, a corner radius or a box shadow from script repaints that one box; every other property
  rebuilds the page, which is roughly half a millisecond per box on it. A pan that sets `left`/`top` at 60 Hz
  will hitch; the same pan through `transform` will not.
- **`<img>` paints a file from your bundle**, sized by CSS alone - the layout runs without Unity and cannot
  open a PNG to learn an intrinsic size, so give it a width and a height. The aspect ratio is preserved
  inside that box, and `color` tints the image, so one white glyph works on a dark bar and a light one.
- **Never `await` a promise the host settles on a later frame.** `await fetch(...)` deadlocks the game;
  Jint's `await` blocks the very thread that would deliver the answer. Use `.then()` / `.catch()`.

## Compatibility

- IL2CPP build only (current Steam public branch).
- Runs alongside any mod. The only game code it touches is a Harmony postfix on `HomeScreen.Start`, which
  creates one panel and one icon per registered app and leaves the rest of the phone alone.
- Purely client local. Nothing it does crosses the network to other players.
- Two mods may register handlers of the same name; handlers are keyed by app id.

## Credits

- **DooDesch** - mod author.
- **[AngleSharp](https://github.com/AngleSharp/AngleSharp)** (MIT) - HTML parsing, the DOM and the selector engine.
- **[Jint](https://github.com/sebastienros/jint)** (BSD-2) and **Esprima** - the JavaScript engine and its parser.

## License

Provided as-is under the [MIT License](LICENSE.md).
