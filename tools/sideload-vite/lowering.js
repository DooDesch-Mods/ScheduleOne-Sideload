// The lowering pass: everything a web build says that this engine cannot read, rewritten into a
// spelling it can. It lives here rather than beside the Tailwind preset because a mod author outside
// this monorepo has to be able to install it, and `sideload-tailwind/lower.mjs` is now the CLI in
// front of it.
//
// Three cumulative stages, each a separate cut so its contribution can be measured instead of argued
// about:
//
//   lightning  Lightning CSS with the transforms forced on: media range syntax to min-width, logical
//              properties to physical, nesting flattened, modern colour functions to sRGB. This is
//              what any browser-targeting tool gives you and no more. Nothing is dropped.
//
//   rewrite    (default) + the rewrites no browser target performs, because no browser needs them.
//              Still nothing is dropped: every declaration that goes in comes out, in a spelling the
//              engine reads.
//
//   prune      + declarations and rules the engine provably cannot render are deleted. This changes
//              no pixel, but it also removes the engine's report about them, so it is opt-in and
//              limited to declarations an author could do nothing with. See DEAD_PROPERTIES.
//
// Lightning CSS is pinned to 1.33.0 on purpose. Two behaviours below are version-specific: a visitor
// that RETURNS its declaration object dies with "failed to deserialize; expected an object-like struct
// named Specifier" on the first Tailwind sheet it sees (return undefined to keep it, [] to drop it),
// and a logical SHORTHAND whose value contains var() is left untouched because the expansion width is
// unknowable at build time. The second one is why the theme variables are inlined before the second
// Lightning pass rather than after it.

import { transform, Features } from 'lightningcss';

export const STAGES = ['lightning', 'rewrite', 'prune'];

/**
 * Lower one stylesheet. Returns the rewritten CSS; throws if the result no longer parses.
 *
 * @param {string} css      the stylesheet a web toolchain produced
 * @param {string} [stage]  one of STAGES, default "rewrite"
 */
export function lower(css, stage = 'rewrite') {
  if (!STAGES.includes(stage)) throw new Error(`unknown lowering stage: ${stage}`);

  // `include` forces a transform on regardless of browser targets, which is what a non-browser target
  // needs: there is no browserslist entry for a flexbox engine inside a Unity game.
  const LIGHTNING = {
    minify: false,
    include:
      Features.LogicalProperties |
      Features.MediaRangeSyntax |
      Features.MediaIntervalSyntax |
      Features.Nesting |
      Features.Colors |
      Features.OklabColors |
      Features.LabColors |
      Features.ColorFunction |
      Features.SpaceSeparatedColorNotation |
      Features.ClampFunction |
      Features.DoublePositionGradients,
    exclude: Features.VendorPrefixes,
  };

  const lightning = (code) =>
    transform({ filename: 'app.css', code: Buffer.from(code), ...LIGHTNING }).code.toString();


  // ----------------------------------------------------------------------- 1. Lightning CSS --
  css = lightning(css);

  if (stage === 'lightning') return validated(css);

  // ------------------------------------------------- 2a. inline the static theme variables --
  //
  // Every --color-*, --spacing, --radius-* and friend is a constant decided at build time. Resolving
  // them here costs nothing at runtime and, more usefully, hands Lightning a value it will act on.
  // The --tw-* family is deliberately left alone: those are mutated per utility and per state, and
  // freezing one into a constant would break the utility that writes it.

  const theme = new Map();
  for (const m of css.matchAll(/(--[a-zA-Z][a-zA-Z0-9-]*)\s*:\s*([^;}]+)[;}]/g)) {
    const [, name, value] = m;
    if (name.startsWith('--tw-')) continue;
    if (value.includes('var(')) continue;
    theme.set(name, value.trim());
  }

  const inlineVars = (code) => {
    // Repeated, because a theme value may itself name another one.
    for (let pass = 0; pass < 4; pass++) {
      const before = code;
      code = code.replace(
        /var\((--[a-zA-Z][a-zA-Z0-9-]*)\s*(?:,\s*([^()]*(?:\([^()]*\)[^()]*)*))?\)/g,
        (all, name) => (theme.has(name) ? theme.get(name) : all),
      );
      if (code === before) break;
    }
    return code;
  };

  // Substituted everywhere, including inside the value of another custom property: Tailwind stores
  // `--tw-translate-y: calc(var(--spacing) * -.5)`, and leaving that one alone would leave the calc
  // unfoldable for the second Lightning pass.
  css = inlineVars(css);

  // Second Lightning pass. With the vars gone, `padding-inline: calc(4px * 2)` is a length it will
  // both fold and lower into physical longhands.
  css = lightning(css);

  // ------------------------------------------------------------------------- 2b. rewrites --

  // Lightning turns a logical LONGHAND into a `:dir(ltr)` / `:dir(rtl)` pair, because only the document
  // can say which one applies. This engine has no writing direction at all (LAYOUT-036) and lays out
  // left to right, so ltr is the answer and rtl is a rule that can never match.
  css = css.replace(/([^\n{}]*?):dir\(\s*rtl\s*\)([^\n{}]*)\{[^{}]*\}/g, '');
  css = css.replace(/:dir\(\s*ltr\s*\)/g, '');

  // Lightning writes ::before and ::after in the old single-colon form, which is valid CSS and the
  // worst possible spelling here: AngleSharp ACCEPTS `:before`, so the rule matches the element itself
  // and a `before:absolute` utility positions the element rather than a pseudo-element that does not
  // exist (CSS-034). Restoring the double colon gets the rule rejected, which is the truthful outcome -
  // nothing is drawn either way, and this way the engine says so.
  css = css.replace(/(?<!:):(before|after)\b/g, '::$1');

  // `:host` addresses a shadow root. There is no shadow DOM here and the selector is rejected, taking
  // the theme rule's second half with it. Tailwind writes `:root, :host` on every theme block.
  css = css.replace(/:root,\s*:host\b/g, ':root');

  // Tailwind ships every --tw-* default twice: once as @property, and once inside a @layer properties
  // block gated on a @supports condition written to sniff Safari. The engine implements @property and
  // seeds the initial values at the root (CSS-022), so the first copy is honoured and the second is the
  // same values again behind a condition it cannot evaluate. Dropping the duplicate takes the whole
  // `*, ::before, ::after, ::backdrop` selector with it, which is a selector this DOM has no node for.
  //
  // Counted rather than matched: the block nests three deep, and a regex that stops at the first
  // `}\n}` leaves the outermost brace behind. A stray brace does not fail loudly - the next `@layer`
  // line is swallowed into the selector of the rule after it, and what the engine then reports is a
  // rejected selector reading `@layer theme :root`.
  css = removeBlock(css, '@layer properties');

  // `infinity` is not a length here. On a 733px screen 9999px is the same pill. Lightning folds the
  // calc to 2147483647px on the way through, so both spellings are matched.
  css = css.replace(/calc\(infinity\s*\*\s*1px\)|2147483647px/g, '9999px');

  // opacity takes a number in this engine; Tailwind writes the percentage form.
  css = css.replace(/(\bopacity\s*:\s*)(\d+(?:\.\d+)?)%/g, (all, head, n) => head + +(n / 100).toFixed(4));

  // A gradient's first argument. Two things are wrong with Tailwind's form: the interpolation colour
  // space is a blending hint the value reader does not know, and `to <side>` is not the `<angle>deg`
  // form it does read. Both live inside --tw-gradient-position, so this has to reach the custom
  // property and not only the linear-gradient() call.
  const TO_ANGLE = {
    'to top': '0deg', 'to right': '90deg', 'to bottom': '180deg', 'to left': '270deg',
    'to top right': '45deg', 'to right top': '45deg',
    'to bottom right': '135deg', 'to right bottom': '135deg',
    'to bottom left': '225deg', 'to left bottom': '225deg',
    'to top left': '315deg', 'to left top': '315deg',
  };
  css = css.replace(/\s+in\s+(?:ok)?(?:lab|lch|srgb|hsl)\b(\s+(?:shorter|longer|increasing|decreasing)\s+hue)?/g, '');
  css = css.replace(/\bto (?:top|bottom|left|right)(?: (?:top|bottom|left|right))?/g, (all) => TO_ANGLE[all] ?? all);

  // LAYOUT-002. `display: flex` starts a ROW everywhere in CSS and a COLUMN here, so Tailwind's bare
  // `.flex` comes out stacked. The page can also fix this for itself with
  // `<meta name="sideload" content="web-defaults">`, which is the better answer because it covers boxes
  // with no `display` at all; saying the direction out loud as well costs one declaration and is
  // correct either way. `inline-flex` keeps its own name so the engine goes on reporting that there is
  // no inline box (LAYOUT-005) - only the direction is added.
  css = css.replace(/(\n\s*)display\s*:\s*(inline-flex|flex)\s*;/g, '$1display: $2;$1flex-direction: row;');

  // The box-shadow chain. Tailwind composes five slots - inset shadow, inset ring, ring offset, ring,
  // drop shadow - into one comma list. This engine draws exactly one OUTER shadow (PAINT-007): it walks
  // the layers, skips the inset ones and the fully transparent ones, and paints the first that is left.
  // The drop-shadow slot therefore already arrives - but a ring is a spread-only shadow, `0 0 0 2px`,
  // and spread has no channel (PAINT-008), so a ring layer resolves to an opaque shadow with no offset
  // and no blur, wins the walk, and suppresses the real shadow behind it. Collapsing the chain to the
  // drop-shadow slot is what makes shadow-* and ring-* compose the way they look like they do.
  //
  // The cost, named rather than hidden: after this, ring-* paints nothing AND says nothing. It painted
  // nothing before as well - PAINT-007 and PAINT-008 are its register entries - so what is lost is the
  // engine's line about it, not a ring.
  css = css.replace(/box-shadow\s*:\s*var\(--tw-inset-shadow\)[^;]*var\(--tw-shadow\)\s*;/g,
    'box-shadow: var(--tw-shadow);');

  // v4 uses the individual `scale` and `translate` properties. This engine reads `transform`, which it
  // does implement, so this is a spelling difference and not a missing feature.
  css = css.replace(/(--tw-scale-[xyz]\s*:\s*)(-?[\d.]+)%/g, (all, head, n) => head + +(n / 100).toFixed(4));
  css = css.replace(/\bscale\s*:\s*var\(--tw-scale-x\)\s+var\(--tw-scale-y\)\s*;/g,
    'transform: scale(var(--tw-scale-x), var(--tw-scale-y));');
  css = css.replace(/\btranslate\s*:\s*var\(--tw-translate-x\)\s+var\(--tw-translate-y\)\s*;/g,
    'transform: translate(var(--tw-translate-x), var(--tw-translate-y));');

  // Rules the deletions above emptied out.
  css = dropEmptyRules(css);

  if (stage === 'rewrite') return validated(css);

  // ---------------------------------------------------------------------------- 3. prune --
  //
  // The test for this list is one question: could the author DO anything with the report? For
  // `-webkit-font-smoothing` the answer is no - there is no anti-aliasing control to reach for, and the
  // line is pure noise. For `text-transform` the answer is yes - write the text in capitals - so it
  // stays in and goes on being reported.
  //
  // What is NOT here matters as much as what is. grid, z-index, auto margins, position:relative,
  // align-items:baseline, overflow-x, object-fit, filter, cursor, text-decoration and ::placeholder all
  // survive this stage, because each is a place where the author asked for something real and is owed
  // an answer rather than silence.

  const DEAD_PROPERTIES = new Set([
    // No analogue in this renderer and nothing to write instead.
    '-webkit-font-smoothing', '-moz-osx-font-smoothing', '-webkit-text-size-adjust',
    '-webkit-tap-highlight-color', '-webkit-appearance', 'appearance',
    '-webkit-backdrop-filter', 'font-variant-numeric', 'font-feature-settings',
    'font-variation-settings', 'resize', 'tab-size', 'touch-action', 'user-select',
    'will-change', 'isolation', 'scroll-snap-type', 'scroll-snap-align',

    // Every box is already border-box (LAYOUT-032), and there are no table boxes to collapse.
    'box-sizing', 'border-collapse',

    // Read and then discarded on purpose - see Sideload/Css/DeadValues.cs. The engine names each of
    // these once per stylesheet however many times it appears, so deleting them removes a line that
    // has already been said rather than a line that has not.
    'transition-property', 'line-height',
    'border-style', 'border-top-style', 'border-right-style', 'border-bottom-style', 'border-left-style',

    // There is no pseudo-element box for `content` to fill (CSS-034), and the rules it lives in are
    // removed below anyway.
    'content',

    // Superseded by the transform rewrite above; leaving them in would be the same value twice.
    'scale', 'rotate', 'translate', '-webkit-text-decoration',
  ]);

  // A whole rule goes only when its selector cannot match anything in this DOM. ::before and ::after
  // are here because DomBuilder creates no pseudo-element node at all (CSS-034): after the rewrite
  // stage the selector is well-formed, AngleSharp accepts it, and it then matches nothing at all - so
  // the rule is the one kind of loss the report cannot show, and deleting it is what makes the
  // remaining count honest.
  //
  // ::placeholder is NOT here: the engine paints placeholder text, so a rule that styles it is a
  // reasonable thing to have written and CSS-035 is worth hearing about.
  const DEAD_SELECTOR =
    /::?(before|after|backdrop|selection|marker|first-line|first-letter|file-selector-button|-webkit-[a-z-]*|-moz-[a-z-]*)\b|:host\b|:-moz-/;

  css = css
    .split('\n')
    .filter((line) => {
      const decl = line.match(/^\s*([-a-zA-Z]+)\s*:/);
      return !(decl && DEAD_PROPERTIES.has(decl[1]));
    })
    .join('\n');

  css = css.replace(/(^|\n)([^\n{}]*\{)([^{}]*)\}/g, (all, lead, head) => (DEAD_SELECTOR.test(head) ? lead : all));
  css = dropEmptyRules(css);

  return validated(css);
}

/**
 * Hands the result back to the parser before anyone gets to keep it.
 *
 * Every step above is a string edit, and a string edit can produce CSS that still looks like CSS. A
 * lost brace does not fail here - it fails silently in the engine, where the next `@layer` line is
 * swallowed into the selector of the rule after it. Parsing the output once is the cheapest way to
 * find that at build time instead of on a phone screen.
 */
function validated(code) {
  try {
    transform({ filename: 'app.css', code: Buffer.from(code), minify: false, errorRecovery: false });
  } catch (error) {
    throw new Error(`the lowering pass produced CSS that no longer parses: ${error.message}`);
  }
  return code;
}

/** Rules the deletions above emptied out. Repeated, because emptying one can empty its wrapper. */
function dropEmptyRules(code) {
  for (let i = 0; i < 3; i++) code = code.replace(/(^|\n)[^\n{}@]*\{\s*\}/g, '$1');
  return code;
}

/** The at-rule starting with `prelude` and everything inside its braces, matched by counting them. */
function removeBlock(code, prelude) {
  const start = code.indexOf(prelude);
  if (start < 0) return code;

  let depth = 0;
  for (let i = code.indexOf('{', start); i >= 0 && i < code.length; i++) {
    if (code[i] === '{') depth++;
    else if (code[i] === '}' && --depth === 0) return code.slice(0, start) + code.slice(i + 1);
  }
  return code;
}
