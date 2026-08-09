# create-sideload-app

One command to a Sideload app that builds, typechecks and lands on the phone.

```
node index.mjs my-app --template preact --deploy "F:/.../Schedule I/Mods"
cd my-app
npm install
npm run dev
```

`dev` rebuilds on save and copies the bundle into `Mods/<appId>/`, where Sideload picks it up without a restart.

## Templates

| | bundle | load | mount 100 rows |
|---|---|---|---|
| `preact` (default) | 14 kB | 37 ms | 53 ms |
| `react` | 139 kB | 113 ms | 54 ms |
| `vanilla` | 0 | 0 | - |

Measured by `FrameworkCostTests` in `Workspace/Tests/Sideload.Tests`, against the engine's own script host.

**Preact is the default because of the load column, not the render one.** Jint parses source on every page build,
inside a 250 ms budget, so a framework's size is a load-time cost here rather than a download cost - the opposite
of the web. React is fully supported and 113 ms is affordable; it just buys nothing the 37 ms does not.

## What it generates

```
my-app/
  index.html          a document - Vite reads it and finds the entry through the script tag
  src/app.tsx         the app
  src/app.css         its stylesheet
  vite.config.js      the sideload plugin, with your appId
  tsconfig.json       strict, with the page API types
  mod/                the C# mod that registers the app
```

The mod half is a starting point rather than a build: fill in the two `PATH\TO` hints in the csproj (MelonLoader,
and `Sideload.cs` from the Sideload repo) and it compiles to one DLL with the bundle embedded.

## Two things about the generated `index.html`

It is a whole document, and what Sideload receives is not. Vite needs the `<script type="module">` tag to find
the entry point; the plugin then emits the fragment the engine wants - the body's contents, a link to `app.css`
and a script tag for `app.js`. Editing this file to look like the output is the one change that breaks the build,
which is why there is a comment saying so.

## Verify

```
node verify.mjs            all three templates
node verify.mjs preact     one of them
```

Scaffolds into a temporary folder, runs `npm install`, `vite build` and `tsc --noEmit` for real, and reads the
output: three files under fixed names, one script with no imports left, a fragment rather than a document, and the
stylesheet present. A scaffolder is the one kind of tool where "it produced files" proves nothing.
