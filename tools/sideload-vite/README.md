# @doodesch/sideload-vite

```
npm install -D @doodesch/sideload-vite
```

`examples/showcase` in this repository deliberately does NOT do that: it reaches the plugin through
`file:../../tools/sideload-vite`, so it builds against the code in the tree rather than the last release. That
one costs an extra `npm install` inside this folder, because Node resolves this package's own `lightningcss`
from here rather than from the consumer's tree.


Build a Sideload app with Vite. React or plain TypeScript in, one bundle the engine reads out.

```js
import { defineConfig } from 'vite';
import { sideload } from '@doodesch/sideload-vite';

export default defineConfig({
  plugins: [sideload({ appId: 'myapp', deploy: 'F:/games/Schedule I/Mods' })],
});
```

```
vite build --watch
```

That is the loop: save a `.ts` file, and about 250 ms later the page has rebuilt in the running game. No
game restart, no mod rebuild.

## What it decides for you

Sideload loads a **bundle**, not a web page: one `index.html` fragment, one `app.css`, one `app.js`, side
by side under fixed names. A default Vite build produces none of that, and every difference fails
silently - a page that simply does not run, with nothing in the log.

| Vite's default | Why it breaks here | What the plugin does |
|---|---|---|
| `<script type="module">` | there is no module loader | `format: 'iife'`, plain `<script src>` |
| hashed filenames | nothing resolves `app.a1b2c3.js` | `app.js`, `app.css` |
| a full HTML document | the engine builds the head itself | emits the body's contents |
| CSS split per chunk | files nothing references | one stylesheet |
| `modulepreload` links | a request the engine never makes | removed |
| minified output | every script error's `file:line` becomes useless | off |
| assets inlined as data URIs | inflates `app.js` for no gain | files, resolved against the bundle |

## What it rewrites

Everything a web toolchain says that the engine cannot read, in the spelling it can. This is the same
pass the Tailwind preset uses, and it is what takes a Tailwind v4 build from **48 % to 87.6 %** of its
declarations arriving.

Logical properties become physical, `oklch()` becomes a colour the engine parses, media range syntax
becomes `min-width`, nesting is flattened, `display: flex` says its direction out loud (it starts a row
in CSS and a column here), and Tailwind's five-slot `box-shadow` chain collapses to the one layer that
gets drawn.

`lower: 'prune'` additionally deletes declarations the engine provably cannot render. It changes no
pixel - but it also removes the engine's report about them, so it is off by default. `lower: false`
leaves the stylesheet alone.

## TypeScript

Nothing to set up: Vite compiles it. What matters is `types/sideload.d.ts`, which types the page API from
`Sideload/Script/DomApi.cs` - so the editor knows `document` here has six members and an element is not a
browser `Element`.

```ts
/// <reference types="@doodesch/sideload-vite/types" />

document.getElementById('go')?.addEventListener('click', () => {
  const said: string = s1.call('app.hello');
});

document.head            // Property 'head' does not exist
el.closest('.card')      // Property 'closest' does not exist
window.alert('x')        // Cannot find name 'window'
```

Those last three are the point. Each one works perfectly in a browser and does nothing at all in the
game, with no error on either side - so the compiler is the only place they can be caught early.

The declarations are generated, never hand-edited:

```
node gen-types.mjs           rewrite them
node gen-types.mjs --check   exit 2 if they are stale
```

## Options

| Option | Default | What it is |
|---|---|---|
| `appId` | required | The id Sideload registers, and the folder name under `Mods/`. |
| `deploy` | none | A `Mods/` folder to copy the bundle into after every build. |
| `lower` | `'rewrite'` | `'lightning'`, `'rewrite'`, `'prune'`, or `false`. |
| `gate` | none | Path to `Workspace/Tests/Sideload.Tests` - fails the build on new unusable CSS. |
| `baseline` | none | The losses already accepted, for the gate. |
| `target` | `'es2020'` | esbuild target. The engine reads ES2015 through ES2024 and transpiles nothing. |

### The gate

```js
sideload({
  appId: 'myapp',
  gate: '../../Workspace/Tests/Sideload.Tests',
  baseline: './css.baseline',
})
```

It parses and cascades with the **engine's own code**, so it cannot drift from what the game does. Exit 2
means it named something the baseline does not already accept, and the build stops.

Run it once with `--update-baseline` before turning it on. Every app loses something today, and a check
that fails on its first run gets removed rather than obeyed. The baseline is the file you then delete
lines from.

## Does it still work

```
npm install
npm run check      # the types are current, and the example builds to the right shape
```

`example/verify.mjs` builds the example app and asserts the shape of what came out - no hashed name, no
module script, the lowering applied - and then hands the result to the engine's own reporter and requires
it to say nothing. Nineteen checks; every one of them is a bug that is silent without it.
