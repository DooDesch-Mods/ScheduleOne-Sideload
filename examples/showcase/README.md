# Sideload showcase

React 19, Tailwind v4, Vite and TypeScript, rendered as real Unity UI on the in-game phone. No browser, no
WebView, no subprocess.

This is the reference app. Copy the folder, rename it, start deleting - every decision in here is one you would
otherwise have to make yourself, and most of them are written down beside the line that makes them.

```
npm install
npm run dev
```

`dev` rebuilds on every save and copies the bundle into `Mods/showcase/`, where the running game picks it up
about 250 ms later. No restart, no rebuild of the mod.

**`package.json` reaches the plugin at `../../tools/sideload-vite`, not through npm.** On purpose: this example
ships with the engine, so it has to build against the plugin in the tree rather than against the last release -
otherwise a change to either could break the other and the example would still be green. Your own app installs
`@doodesch/sideload-vite` normally.

The one cost is a `npm install` inside `tools/sideload-vite` once per checkout, because Node resolves that
package's `lightningcss` from its own folder.

## What it shows

| Tab | What it exercises |
|---|---|
| Layout | Block flow, a flex row, wrapping, grid, `mx-auto` centring |
| Parts | Buttons, a controlled text field, a checkbox, a segmented control, a progress bar |
| Data | A filtered and sorted list that re-renders on every keystroke |
| Bridge | `s1.call` to the mod, per-app storage, a timer, rotating the phone |

## Two lines that are not the web, and one tag you do not need

**The box model comes out right without asking.** The engine stacks an undeclared box downwards, which is block
flow, and the Vite plugin lowers Tailwind's `.flex` to `display: flex; flex-direction: row`, so a row is a row.
There is a `<meta name="sideload" content="web-defaults">` for pages that skip the toolchain; using it here would
make every undeclared box a row as well, which is not what a browser does with a `<div>`.

**`#root { height: 100% }`** in `src/index.css`. `h-full` inside the mount point resolves against `auto`
otherwise, the app grows past the screen, and the whole document scrolls instead of the header staying put. A
browser does exactly the same thing - this is the line every React app forgets once.

**Never `await` a promise the host settles later.** `await fetch(...)` deadlocks the game: the engine's `await`
blocks the very thread that would deliver the answer. Use `.then()`. `s1.call` has no such problem, because it is
synchronous by design - the return value IS the answer.

## The colour that ate a heading

`@theme` in `src/index.css` names a colour `--color-canvas`, not `--color-base`. Tailwind builds a `text-<name>`
utility for every colour it is given, and `text-base` is already the font-size utility. Name a colour `base` and
`text-base` becomes a colour: every heading carrying it comes out near-black, in the game and in a browser
alike. Nothing warns you.

## The build fails on new unusable CSS

`vite.config.js` passes `gate` and `baseline` to the Sideload plugin. After every build the engine's own report
runs over the emitted stylesheet - the same parser and cascade the game uses, so it cannot drift - and anything
that is not already in `showcase.baseline` fails the build.

That file is the honest list of what this app gives up. It is eighteen lines today. Deleting one and rebuilding
tells you whether the gap came back.

```
npm run build      # gate included
```

The gate needs a checkout of `Workspace/Tests/Sideload.Tests`. Drop `gate` from the config if you installed the
plugin from npm and do not have one; the build then just skips it.

## The mod half

`mod/` is forty lines and no uGUI:

```csharp
Apps.Register(id: "showcase", bundlePrefix: "Showcase.Assets.showcase", title: "Showcase")
    .Orientation("landscape", "portrait")
    .OnCall("hello", arg => "hello " + arg);
```

`Sideload.cs` is compiled in as SOURCE rather than referenced. It is one file of pure reflection and every call
is a no-op when Sideload is not installed, so the DLL loads and does nothing on a machine without the framework
instead of throwing a TypeLoadException. That is also why a host mod never has to check whether Sideload is
there.

The bundle prefix in `Core.cs` and the `LogicalName` in the csproj are one string written twice. A typo shows up
as an app that registers and then opens an empty panel.

```
cd mod
dotnet build -c Release
```

## What still does not arrive

28 of this app's 309 declarations, measured by the same gate:
`vertical-align: middle`, `appearance: button`, `display: list-item`, `resize: vertical`,
`font-variant-numeric: tabular-nums`, `outline: auto`, `text-decoration: dotted`, `border-collapse`, `tab-size`,
`::placeholder` and `min-height: 1lh`. All of them are in the gap register under
`Workspace/docs/Sideload/gaps/`, and the full report is `measured/SHOWCASE.md`.

Everything else in a stock Tailwind v4 build lands, including `@layer`, `@theme`, `@property`, `oklch()`,
`calc()`, `rem`, grid and the width breakpoints.

## Preview it in a browser

[`@doodesch/sideload-preview`](../../../Workspace/tools/sideload-preview) restates the engine's own defaults as
CSS, so a browser draws what the game draws. Fix it in the browser first, then check it in the game - a page that
looks wrong in both is a page bug, and a page that looks right in one is an engine gap worth reporting.
