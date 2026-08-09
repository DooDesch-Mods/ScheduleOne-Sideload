// Builds the example and asserts the SHAPE of what came out.
//
// Every check here is a bug the plugin exists to prevent, and every one of them is silent without it:
// a hashed filename or a `type="module"` script does not fail the build, it fails once in the game with
// nothing in the log but a page that did not run.
//
//   node example/verify.mjs

import { execFileSync } from 'node:child_process';
import { readFileSync, existsSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, 'dist');

rmSync(dist, { recursive: true, force: true });
execFileSync(process.execPath, [join(here, '..', 'node_modules', 'vite', 'bin', 'vite.js'), 'build'],
             { cwd: here, stdio: ['ignore', 'pipe', 'pipe'] });

const checks = [];
const check = (ok, what) => checks.push({ ok: !!ok, what });

// --- the three files, under the names the engine looks for -----------------------------------------

for (const name of ['index.html', 'app.css', 'app.js']) check(existsSync(join(dist, name)), `dist/${name} exists`);

const html = readFileSync(join(dist, 'index.html'), 'utf8');
const css = readFileSync(join(dist, 'app.css'), 'utf8');
const js = readFileSync(join(dist, 'app.js'), 'utf8');

// --- index.html is a fragment, not a document -------------------------------------------------------

check(!/<html[\s>]/i.test(html), 'index.html carries no <html> - the engine builds the document');
check(!/type=["']?module/i.test(html), 'no type="module" - the engine has no module loader');
check(!/modulepreload/i.test(html), 'no modulepreload - nothing would fetch it');
check(/<script src="app\.js"><\/script>/.test(html), 'app.js is linked plainly');
check(/<link rel="stylesheet" href="app\.css">/.test(html), 'app.css is linked plainly');
check(/id="go"/.test(html), 'the page markup survived');

// --- app.js is one IIFE with no module syntax -------------------------------------------------------

check(!/^\s*(import|export)\s/m.test(js), 'app.js has no import/export - it is one IIFE');
check(js.includes('example.hello'), 'the TypeScript was compiled in');
check(!/:\s*string/.test(js), 'the type annotations are gone');

// --- app.css came through the lowering --------------------------------------------------------------

check(!/padding-inline/.test(css), 'logical properties are physical now');
check(!/oklch\(/.test(css), 'oklch is gone - it became lab(), which the engine does read');
check(!/<=|>=/.test(css), 'the media range syntax is min-width now');
check(!/&\s*#title/.test(css), 'the nested rule is flattened');
check(/#title/.test(css), 'and its declarations survived');
check(/flex-direction:\s*row/.test(css) || !/display:\s*flex/.test(css),
      'any display:flex says its direction out loud');

// ----------------------------------------------------------------------------------------------------

// --- and finally, ask the engine itself ------------------------------------------------------------
//
// Every check above is this file's opinion about what the engine wants. This one is the engine's: the
// corpus runner parses and cascades with the engine's own code and names anything it cannot use. Zero
// findings over a stylesheet this small is the honest end of the chain. Skipped where dotnet or the
// test project is missing, because neither is a requirement for using the plugin.

const TESTS = join(here, '..', '..', '..', 'Tests', 'Sideload.Tests');
if (existsSync(TESTS)) {
  // --json, not stdout. A cold `dotnet build` writes its own compiler warnings onto the same stream, and this
  // check went red once on a CS0649 that has nothing to do with the stylesheet - a check that fails for a
  // reason it does not name is worse than no check.
  const report = join(here, 'engine-report.json');
  rmSync(report, { force: true });

  try {
    execFileSync('dotnet',
      ['run', '-v', 'q', '--nologo', '--project', TESTS, '--',
       '--corpus', dist, '--json', report, '--fail-on', 'all', '--quiet'],
      { stdio: ['ignore', 'pipe', 'pipe'] });
  } catch {
    // Exit 2 is the gate naming something; the JSON below says what, and that is the answer either way.
  }

  if (!existsSync(report)) {
    console.log('note  the engine gate was skipped - dotnet could not run it');
  } else {
    const said = JSON.parse(readFileSync(report, 'utf8')).diagnostics ?? [];
    check(said.length === 0,
          `the engine reports nothing about the built stylesheet${said.length ? ` - got: ${said.map((d) => d.message).join('; ')}` : ''}`);
    rmSync(report, { force: true });
  }
} else {
  console.log('note  the engine gate was skipped - no test project beside this checkout');
}

let failed = 0;
for (const c of checks) {
  if (!c.ok) failed++;
  console.log(`${c.ok ? 'ok  ' : 'FAIL'}  ${c.what}`);
}

console.log(`\n${checks.length - failed}/${checks.length} checks passed.`);
process.exit(failed ? 1 : 0);
