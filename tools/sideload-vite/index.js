// A Vite plugin that builds a Sideload app.
//
// Sideload loads a bundle, not a web page: one `index.html` fragment, one `app.css`, one `app.js`, all
// beside each other under fixed names. A default Vite build produces none of that - it hashes filenames
// for cache busting the engine has no cache for, splits CSS per chunk, emits ES modules the engine has
// no loader for, and writes a whole HTML document when the engine wants the body's contents.
//
// So this plugin is mostly a set of decisions taken FOR the author, each of which is a bug they would
// otherwise hit once and then work around by hand.
//
//   import { defineConfig } from 'vite';
//   import { sideload } from '@doodesch/sideload-vite';
//
//   export default defineConfig({
//     plugins: [sideload({ appId: 'myapp', deploy: 'F:/.../Schedule I/Mods' })],
//   });
//
// TypeScript needs nothing extra: Vite compiles it, and `types/sideload.d.ts` types the page API - so
// `el.closest('.card')` is a compile error rather than a silence at runtime.

import { existsSync, mkdirSync, copyFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { lower, STAGES } from './lowering.js';

const here = dirname(fileURLToPath(import.meta.url));

/**
 * @param {object} options
 * @param {string} options.appId          the app id Sideload registers, and the folder name under Mods/
 * @param {string} [options.deploy]       a Mods/ folder to copy the bundle into after every build
 * @param {string|false} [options.lower]  lowering stage, or false to leave the CSS alone. Default "rewrite".
 * @param {string} [options.gate]         path to Sideload.Tests, to fail the build on new unusable CSS
 * @param {string} [options.baseline]     the losses already accepted, for the gate
 * @param {string} [options.target]       esbuild target. Default "es2020"; the engine reads ES2015-ES2024.
 */
export function sideload(options = {}) {
  const appId = options.appId;
  if (!appId) throw new Error('sideload(): appId is required - it is the folder name under Mods/ as well.');

  const stage = options.lower === undefined ? 'rewrite' : options.lower;
  if (stage !== false && !STAGES.includes(stage))
    throw new Error(`sideload(): lower must be false or one of ${STAGES.join(', ')}`);

  let outDir = 'dist';

  return {
    name: 'sideload',

    config(config) {
      outDir = config.build?.outDir ?? 'dist';

      return {
        build: {
          // The engine reads ES2015 through ES2024 and transpiles nothing, so this is about what Jint
          // has rather than about browsers. es2020 is the conservative default; raise it if you want
          // `#private` fields or `at(-1)`, both of which the engine also has.
          target: options.target ?? 'es2020',

          // One stylesheet, under a name the page can link. Split CSS would arrive as files nothing
          // references, and a hash would change the name on every edit.
          cssCodeSplit: false,

          // There is no module loader and no preload scanner. A modulepreload link is a request for a
          // file the engine will not fetch, and `type="module"` on the script tag stops it running.
          modulePreload: false,

          // Readable in the log. The engine parses either, and a minified bundle turns every script
          // error's `file:line` into one useless line.
          minify: false,

          // Images stay files. A data: URI inflates app.js, and the engine resolves `src` against the
          // bundle folder perfectly well.
          assetsInlineLimit: 0,

          rollupOptions: {
            output: {
              format: 'iife',
              inlineDynamicImports: true,
              entryFileNames: 'app.js',
              assetFileNames: (asset) => (asset.name?.endsWith('.css') ? 'app.css' : '[name][extname]'),
            },
          },
        },
      };
    },

    // Vite writes a whole HTML document with a module script. Sideload wants the body's contents, with
    // the two files linked plainly - it builds the head itself and would treat an unknown script tag as
    // a bundle file it cannot find.
    transformIndexHtml: {
      order: 'post',
      handler(html) {
        const body = html.match(/<body[^>]*>([\s\S]*?)<\/body>/i);
        let fragment = body ? body[1] : html;

        fragment = fragment
          .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
          .replace(/<link\b[^>]*rel=["']?(modulepreload|stylesheet)["']?[^>]*>/gi, '')
          .trim();

        // `<meta name="sideload" ...>` lives in the head, and the head is the half of the document thrown away
        // above - so a page asking for the engine's alternative defaults was silently not asking, and had no way
        // to find that out. It is the one tag from the head the bundle needs, so it comes across.
        // Comments stripped first, and the HEAD only. Both halves are load-bearing: a page that explains in a
        // comment which tag it is NOT using would otherwise ship the tag it was talking about, and a `<meta>` in
        // the body is not where this one goes.
        const head = (html.match(/<head[^>]*>([\s\S]*?)<\/head>/i)?.[1] ?? '').replace(/<!--[\s\S]*?-->/g, '');

        const settings = [...head.matchAll(/<meta\b[^>]*\bname=["']?sideload["']?[^>]*>/gi)]
          .map((match) => match[0])
          .join('\n');

        return `<link rel="stylesheet" href="app.css">\n`
             + (settings ? `${settings}\n` : '')
             + `${fragment}\n<script src="app.js"></script>\n`;
      },
    },

    // The lowering runs on the emitted stylesheet rather than on the source, because what needs
    // rewriting is what the toolchain PRODUCED - Tailwind's utilities, Lightning's logical properties -
    // and none of it exists yet while a source file is being transformed.
    //
    // `post` is load-bearing: Vite assembles the stylesheet in its own generateBundle, so without it
    // this runs first, finds no CSS asset, and silently rewrites nothing.
    generateBundle: {
      order: 'post',
      handler(_, bundle) {
        if (stage === false) return;

        for (const [name, asset] of Object.entries(bundle)) {
          if (asset.type !== 'asset' || !name.endsWith('.css')) continue;

          try {
            asset.source = lower(asset.source.toString(), stage);
          } catch (error) {
            this.error(`sideload: ${error.message}`);
          }
        }
      },
    },

    writeBundle() {
      const dir = resolve(outDir);

      if (options.gate) gate(dir, options, (m) => this.warn(m), (m) => this.error(m));
      if (options.deploy) deploy(dir, join(options.deploy, appId), (m) => this.warn(m));
    },
  };
}

/**
 * Run the engine's own report over the built stylesheet and fail on anything new.
 *
 * It parses and cascades with the engine's code, so it cannot drift from what the game does - which is
 * the whole reason to pay a dotnet run for it. Opt-in: it needs a checkout of the Sideload test project,
 * which an author who installed this from npm does not have.
 */
function gate(dir, options, warn, fail) {
  const project = resolve(options.gate);
  if (!existsSync(project)) {
    warn(`sideload: gate skipped - no test project at ${project}`);
    return;
  }

  const args = ['run', '-v', 'q', '--nologo', '--project', project, '--',
                '--corpus', dir, '--fail-on', 'all', '--quiet'];
  if (options.baseline) args.push('--baseline', resolve(options.baseline));

  try {
    execFileSync('dotnet', args, { stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (error) {
    const said = (error.stdout?.toString() ?? '').trim();
    // Exit 2 is the gate's own "I found something new"; anything else is the tool failing to run, and
    // a build must not go green because dotnet is missing.
    if (error.status === 2 && said) fail(`sideload: new unusable CSS in this build -\n${said}`);
    else warn(`sideload: gate could not run (${error.status}) - ${error.message.split('\n')[0]}`);
  }
}

/**
 * Copy the bundle into `Mods/<appId>/`, which is the folder Sideload's hot reload watches.
 *
 * That is the whole development loop: the page rebuilds about 250 ms after the last write, with no game
 * restart and no rebuild of the mod. `vite build --watch` plus this is edit-to-on-screen.
 *
 * The folder has to exist when the page FIRST builds for the watcher to attach, so this creates it and
 * says so - an author who starts the game first gets "not watching" on the overlay and no explanation.
 */
function deploy(from, to, warn) {
  if (!existsSync(dirname(to))) {
    warn(`sideload: deploy skipped - ${dirname(to)} does not exist`);
    return;
  }

  const fresh = !existsSync(to);
  mkdirSync(to, { recursive: true });

  for (const name of readdirSync(from)) {
    const source = join(from, name);
    if (statSync(source).isDirectory()) continue;
    copyFileSync(source, join(to, name));
  }

  if (fresh) warn(`sideload: created ${to} - restart the game once so the hot reload attaches to it`);
}

export { lower, STAGES };
export default sideload;
