#!/usr/bin/env node
// Scaffolds a Sideload app: the web bundle, the mod that registers it, and a build that puts the result on the
// phone. One command, and the next thing you do is `npm run dev`.
//
//   node index.mjs my-app --template preact --deploy "F:/.../Schedule I/Mods"
//
// The templates live in this file rather than in a tree of their own on purpose. There are three of them, they
// differ by about ten lines each, and a tree makes it easy for those ten lines to drift apart unnoticed - which is
// how a scaffold ends up generating a project that does not build.

import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { join, resolve, basename } from 'node:path';

const TEMPLATES = {
  // Measured with Workspace/Tests/Sideload.Tests (FrameworkCostTests), against the engine's own script host:
  //
  //   preact      14 kB   load  37 ms   mount 100 rows 53 ms
  //   react-dom  139 kB   load 113 ms   mount 100 rows 54 ms
  //
  // The mount costs the same; the LOAD does not, and that is the number that matters here. A page build has a
  // 250 ms budget and the framework is parsed inside it on every build, because Jint parses source rather than
  // loading a compiled module. So preact is the default - not because react is unsupported, it is not, but
  // because three times the load for the same render is a poor trade on a phone in a game.
  preact: {
    deps: { preact: '^10.29.0' },
    jsx: { jsxImportSource: 'preact' },
    entry: 'app.tsx',
    source: `import { render } from 'preact';
import { useState } from 'preact/hooks';
import './app.css';

function App() {
  const [count, setCount] = useState(0);

  return (
    <div class="card">
      <span class="label">clicks: {count}</span>
      <button id="go" onClick={() => setCount(count + 1)}>count up</button>
      <ul>
        {['alpha', 'beta', 'gamma'].slice(0, 1 + (count % 3)).map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

render(<App />, document.getElementById('root')!);
`,
  },

  react: {
    deps: { react: '^18.3.1', 'react-dom': '^18.3.1' },
    // React ships no types of its own; preact does. Leaving these out means the generated project fails its
    // own `npm run check` on the first line of JSX.
    types: { '@types/react': '^18.3.0', '@types/react-dom': '^18.3.0' },
    jsx: {},
    entry: 'app.tsx',
    source: `import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import './app.css';

function App() {
  const [count, setCount] = useState(0);

  return (
    <div className="card">
      <span className="label">clicks: {count}</span>
      <button id="go" onClick={() => setCount(count + 1)}>count up</button>
      <ul>
        {['alpha', 'beta', 'gamma'].slice(0, 1 + (count % 3)).map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<App />);
`,
  },

  vanilla: {
    deps: {},
    jsx: {},
    entry: 'app.ts',
    source: `import './app.css';

let count = 0;

const label = document.getElementById('label')!;
const list = document.getElementById('list')!;

function draw() {
  label.textContent = 'clicks: ' + count;

  list.replaceChildren();
  for (const item of ['alpha', 'beta', 'gamma'].slice(0, 1 + (count % 3))) {
    const row = document.createElement('li');
    row.textContent = item;
    list.appendChild(row);
  }
}

document.getElementById('go')!.addEventListener('click', () => {
  count++;
  draw();
});

draw();
`,
  },
};

const APP_CSS = `/* The engine lays every box out as a flex COLUMN, and border-box is always on. Nothing here undoes that;
   a row says so. Preview the same file in a browser with @doodesch/sideload-preview, which restates those
   defaults so the two agree. */

body {
  padding: 12px;
  gap: 10px;
  background: #12151c;
  color: #ecedf1;
  font-family: game-ui;
  font-size: 15px;
}

.card {
  padding: 12px;
  gap: 8px;
  background: #1c2029;
  border-radius: 10px;
}

.label { font-size: 12px; color: #9aa3b2; }

button {
  padding: 8px 12px;
  background: #2f6df6;
  border-radius: 8px;
  color: #fff;
  cursor: pointer;
}

button:hover { background: #4b81f8; }

ul { gap: 4px; }
li { padding: 4px 0; }
`;

function html(template, appId) {
  const body = template === 'vanilla'
    ? `  <div class="card">
    <span class="label" id="label"></span>
    <button id="go">count up</button>
    <ul id="list"></ul>
  </div>`
    : '  <div id="root"></div>';

  // A whole document, because VITE reads this one and finds the entry point through the script tag. What
  // Sideload gets is not this file but the fragment the plugin emits from it: the body's contents, a link to
  // app.css and a script tag for app.js. Dropping the script tag here to make it look like the output would
  // leave Vite with nothing to build - which is exactly the mistake this comment exists to prevent.
  return `<!doctype html>
<html>
<head><meta charset="utf-8"><title>${appId}</title></head>
<body>
${body}
  <script type="module" src="./src/${TEMPLATES[template].entry}"></script>
</body>
</html>
`;
}

function viteConfig(appId, deploy) {
  const deployLine = deploy ? `, deploy: ${JSON.stringify(deploy)}` : '';
  return `import { defineConfig } from 'vite';
import { sideload } from '@doodesch/sideload-vite';

// appId is the id your mod registers AND the folder name under Mods/ that overrides the built bundle - the
// same string in both places, which is what makes hot reload work without any further configuration.
export default defineConfig({
  plugins: [sideload({ appId: '${appId}'${deployLine} })],
});
`;
}

function packageJson(name, template) {
  const t = TEMPLATES[template];
  return JSON.stringify({
    name,
    private: true,
    type: 'module',
    scripts: {
      dev: 'vite build --watch',
      build: 'vite build',
      check: 'tsc --noEmit',
    },
    dependencies: t.deps,
    devDependencies: {
      '@doodesch/sideload-vite': '^1.0.0',
      ...(t.types ?? {}),
      typescript: '^5.6.0',
      vite: '^6.0.0',
    },
  }, null, 2) + '\n';
}

function tsconfig(template) {
  const t = TEMPLATES[template];
  const compilerOptions = {
    target: 'ES2020',
    module: 'ESNext',
    moduleResolution: 'bundler',
    lib: ['ES2023'],
    strict: true,
    noEmit: true,
    isolatedModules: true,
    skipLibCheck: true,
    // The page API, generated from the engine's own source. Without it `document` would be the browser's, and
    // `el.insertAdjacentHTML(...)` would type-check and then do nothing in the game.
    types: ['@doodesch/sideload-vite/types'],
    ...(template === 'vanilla' ? {} : { jsx: 'react-jsx', ...t.jsx }),
  };

  return JSON.stringify({ compilerOptions, include: ['src'] }, null, 2) + '\n';
}

function modCsproj(name) {
  return `<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>${name}</RootNamespace>
    <AssemblyName>${name}</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="MelonLoader">
      <HintPath>PATH\\TO\\MelonLoader.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <!-- The modder shim as SOURCE: one file, no reference to Sideload itself, every call a no-op when Sideload
       is not installed. Copy it from the Sideload repo (Sideload.Api/Sideload.cs). -->
  <ItemGroup>
    <Compile Include="PATH\\TO\\Sideload.cs" Link="Sideload.Api.cs" />
  </ItemGroup>

  <!-- The built web bundle. This LogicalName prefix IS the bundlePrefix passed to Apps.Register. -->
  <ItemGroup>
    <EmbeddedResource Include="Assets/${name}/*">
      <LogicalName>${name}.Assets.${name}.%(Filename)%(Extension)</LogicalName>
    </EmbeddedResource>
  </ItemGroup>

</Project>
`;
}

function modCore(name, appId) {
  return `using MelonLoader;
using Sideload.Api;

[assembly: MelonInfo(typeof(${name}.Core), "${name}", "1.0.0", "you")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace ${name}
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            // Load-order proof: registering before Sideload has loaded is fine, the call replays once it appears.
            Apps.Register("${appId}", "${name}.Assets.${name}", "${name}")
                .Orientation("landscape")
                .OnCall("hello", arg => "hello " + arg);
        }
    }
}
`;
}

function readme(name, appId, template) {
  return `# ${name}

A Sideload app: HTML, CSS and TypeScript, rendered as real Unity UI on the in-game phone.

## Build it

\`\`\`
npm install
npm run dev
\`\`\`

\`dev\` rebuilds on every save. Point it at your game with \`deploy\` in \`vite.config.js\` and the files land in
\`Mods/${appId}/\`, where Sideload picks them up without a restart.

## What is where

| | |
|---|---|
| \`src/${TEMPLATES[template].entry}\` | the app |
| \`src/app.css\` | its stylesheet |
| \`index.html\` | a fragment, not a document - Sideload takes the body's contents |
| \`mod/\` | the C# mod that registers the app and answers \`s1.call\` |

## Two things that are not the web

**Every box is a flex column.** Not a block. \`display: flex; flex-direction: column; box-sizing: border-box\` is
the starting point for every element, so a row says \`flex-direction: row\`. Preview in a browser with
[@doodesch/sideload-preview](https://github.com/DooDesch-Mods/ScheduleOne-Sideload), which restates those defaults
so the browser draws what the game draws.

**Never \`await\` a promise the host settles later.** \`await fetch(...)\` deadlocks the game - the engine's
\`await\` blocks the very thread that would deliver the answer. Use \`.then()\`.

Anything the engine cannot honour is named in the game's log, once per app, rather than being dropped in silence.

## The mod half

\`mod/\` is a starting point, not a build: fill in the two \`PATH\\TO\` hints in the csproj (MelonLoader, and
\`Sideload.cs\` from the Sideload repo) and it compiles to one DLL with the bundle embedded.
`;
}

// --------------------------------------------------------------------------------------------- the command --

function parse(argv) {
  const args = argv.slice(2);
  const options = { template: 'preact' };
  const rest = [];

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--template' || args[i] === '-t') options.template = args[++i];
    else if (args[i] === '--deploy') options.deploy = args[++i];
    else if (args[i] === '--app-id') options.appId = args[++i];
    else if (args[i].startsWith('-')) throw new Error('unknown option ' + args[i]);
    else rest.push(args[i]);
  }

  options.dir = rest[0];
  return options;
}

function main() {
  let options;
  try { options = parse(process.argv); }
  catch (error) { console.error(error.message); process.exit(2); }

  if (!options.dir) {
    console.error('usage: create-sideload-app <folder> [--template preact|react|vanilla] [--app-id id]'
                  + ' [--deploy "<path to Mods>"]');
    process.exit(2);
  }

  if (!TEMPLATES[options.template]) {
    console.error(`unknown template "${options.template}" - one of ${Object.keys(TEMPLATES).join(', ')}`);
    process.exit(2);
  }

  const root = resolve(options.dir);
  const name = basename(root).replace(/[^A-Za-z0-9]+/g, '');
  const appId = options.appId ?? basename(root).toLowerCase().replace(/[^a-z0-9-]+/g, '-');

  if (existsSync(root)) { console.error(root + ' already exists'); process.exit(1); }

  const template = TEMPLATES[options.template];
  const files = {
    'package.json': packageJson(basename(root), options.template),
    'tsconfig.json': tsconfig(options.template),
    'vite.config.js': viteConfig(appId, options.deploy),
    'index.html': html(options.template, appId),
    '.gitignore': 'node_modules/\ndist/\n',
    'README.md': readme(name, appId, options.template),
    [`src/${template.entry}`]: template.source,
    'src/app.css': APP_CSS,
    [`mod/${name}.csproj`]: modCsproj(name),
    'mod/Core.cs': modCore(name, appId),
  };

  for (const [path, contents] of Object.entries(files)) {
    const full = join(root, path);
    mkdirSync(join(full, '..'), { recursive: true });
    writeFileSync(full, contents, 'utf8');
  }

  console.log(`${options.dir}: ${Object.keys(files).length} files, template "${options.template}", app id "${appId}"`);
  console.log('\nnext:');
  console.log(`  cd ${options.dir}`);
  console.log('  npm install');
  console.log('  npm run dev');
}

main();
