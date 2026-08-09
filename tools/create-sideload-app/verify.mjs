// Scaffolds every template into a temporary folder, builds it for real, and checks that what comes out is a
// bundle Sideload can read.
//
// A scaffolder is the one kind of tool where "it produced files" says nothing: the files are the product, and
// the only question worth asking is whether they build. So this runs `npm install` and `vite build` against a
// local file: dependency on the plugin, then reads the output.
//
//   node verify.mjs            all three templates
//   node verify.mjs preact     one of them

import { mkdtempSync, rmSync, existsSync, readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const plugin = resolve(here, '..', 'sideload-vite');

const only = process.argv[2];
const templates = only ? [only] : ['preact', 'react', 'vanilla'];

let failures = 0;

function check(name, condition, detail = '') {
  if (condition) console.log(`  ok    ${name}`);
  else { console.log(`  FAIL  ${name} ${detail}`); failures++; }
}

function run(command, args, cwd) {
  return execFileSync(command, args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], shell: true });
}

for (const template of templates) {
  console.log(`\n${template}`);

  const root = mkdtempSync(join(tmpdir(), `sideload-${template}-`));
  const app = join(root, 'demo-app');

  try {
    run('node', [JSON.stringify(join(here, 'index.mjs')), 'demo-app', '--template', template], root);

    check('scaffolded', existsSync(join(app, 'package.json')));
    check('mod half is there', existsSync(join(app, 'mod', 'Core.cs')));

    // The plugin is not published yet, so the generated dependency is pointed at the checkout. Everything else
    // about the project stays exactly as the scaffolder wrote it.
    const manifest = JSON.parse(readFileSync(join(app, 'package.json'), 'utf8'));
    manifest.devDependencies['@doodesch/sideload-vite'] = 'file:' + plugin.replaceAll('\\', '/');
    writeFileSync(join(app, 'package.json'), JSON.stringify(manifest, null, 2));

    run('npm', ['install', '--silent', '--no-audit', '--no-fund'], app);
    run('npx', ['vite', 'build', '--logLevel', 'error'], app);

    const dist = join(app, 'dist');
    const files = existsSync(dist) ? readdirSync(dist) : [];

    check('bundle has the three files', ['index.html', 'app.css', 'app.js'].every((f) => files.includes(f)),
          `got ${files.join(', ')}`);

    if (files.includes('app.js')) {
      const js = readFileSync(join(dist, 'app.js'), 'utf8');
      check('one script, no modules', !/^\s*(import|export)\s/m.test(js));
      check('nothing left to fetch', !js.includes('import('));
      // A string every template puts in its SCRIPT - `count up` is markup in the vanilla one, so checking
      // for that would pass for two templates and fail the third for no reason.
      check('the app is in it', js.includes('clicks: '));
    }

    if (files.includes('index.html')) {
      const html = readFileSync(join(dist, 'index.html'), 'utf8');
      check('a fragment, not a document', !/<html|<head/i.test(html));
      check('names the bundle files', html.includes('app.js') && html.includes('app.css'));
    }

    if (files.includes('app.css')) {
      const css = readFileSync(join(dist, 'app.css'), 'utf8');
      check('the stylesheet came through', css.includes('.card'));
    }

    // TypeScript has to be happy with the page API, or the types are decoration.
    run('npx', ['tsc', '--noEmit'], app);
    check('typechecks against the page API', true);
  } catch (error) {
    console.log(`  FAIL  ${template} threw`);
    console.log(String(error.stdout || '').slice(-1500));
    console.log(String(error.stderr || '').slice(-1500));
    failures++;
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

console.log(failures === 0 ? '\nall templates build.' : `\n${failures} failure(s).`);
process.exit(failures === 0 ? 0 : 1);
