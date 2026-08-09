# Changelog

All notable changes to Sideload are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.28.0] - 2026-08-09

### Fixed

- Boxes have their borders, corners and shadows back next to a mod that draws in the main menu. Only the fill was
  drawn, and no stylesheet could bring the rest back.

## [1.27.0] - 2026-08-09

### Fixed

- A styled `<span>` inside a sentence keeps its background, padding and corners. The whole sentence used to
  collapse into one run of text, and a badge in the middle of it came out as coloured words.
- The space before a bold or coloured word is there. "A paragraph with" and a bold "bold" were printed as
  "withbold", because the piece that carried the space was measured without it.

## [1.26.0] - 2026-08-09

### Added

- `::placeholder` styles the hint text in a field. Without a rule it stays the field's own type, faded, exactly as
  before.
- The `lh` and `rlh` units, which is one line box tall - `min-height: 1lh` is how a stock Tailwind build sizes a
  one-line control.

## [1.25.0] - 2026-08-09

### Added

- `text-decoration` draws an underline or a strike, the `font` shorthand sets the whole type in one line, and
  `inherit` works on every property the cascade can carry down.
- A font stack is read the way a browser reads it: `Inter, game-comic` reaches the comic face instead of falling
  back to the UI font on the first name it does not have.
- An input's `type` is read at last: a checkbox and a radio are a box with a state, `hidden` is no box at all, and
  `button` draws its `value` as a label.
- Clicking a checkbox flips it, clears its radio group and raises `input` and `change` - which is what React
  listens for, so a controlled checkbox finally moves. Clicking its label works too.
- `sideloadwheel [notches] [appId]` turns the wheel over a page through the real raycast and names what took the
  notch. A screenshot cannot tell scrolling from cropping. Debug builds only.

### Changed

- The log names only what is lost. A React and Tailwind build reported 128 dead declarations and now reports 28,
  and most of the difference was the report being wrong.

### Fixed

- A page taller than the phone scrolls instead of drawing past it. The lower half used to be painted outside the
  phone, over the game world, out of reach.
- A page whose script listens for the wheel still scrolls. React registers one on its root container, which took
  scrolling away from every React app.
- A text field is as tall as a line of its own text. It used to be padding around one pixel of nothing, with the
  placeholder hanging out of the bottom.
- A page that says `overflow-x: hidden` still scrolls downwards. It used to be cropped at the first screenful,
  with everything below it reachable by nothing.
- `scrollToEnd()` works on a box that already had a scroll area. The position restore that follows a rebuild put it
  straight back where it was.

## [1.19.0] - 2026-08-09

### Added

- `align-items: baseline` works. It parsed and then did nothing, so a 13px name and a 12px amount in one row sat
  visibly off each other with no rule anywhere looking wrong.
- `overflow-wrap`, `word-wrap` and `word-break` are implemented. `break-all` and `anywhere` bring back the old
  behaviour of cutting a long word to fit.

### Fixed

- A word too long for its box hangs out of it instead of being cut in half. A stock count of 1073904864 printed as
  10739048 over 64, which reads as two numbers.
- A row that does not fit holds each label at its longest word instead of squeezing every box toward nothing.
- An invalid `flex` value is named in the log. It used to vanish in silence, leaving the box at its default with
  nothing pointing at the line that caused it.

## [1.24.0] - 2026-08-09

### Added

- `align-content` moves the wrapped lines: `flex-end`, `center`, `space-between`, `space-around`, `space-evenly`.
- `object-fit: fill` stretches the picture to its box, and `scale-down` leaves one alone that is already smaller.

### Changed

- `align-content: flex-start` and `object-fit: contain` are taken in silence. Both are what the engine already
  did, and every use of them in the shipped apps was reported as unimplemented.

## [1.23.0] - 2026-08-09

### Changed

- The report about unusable CSS only names what is actually lost. `will-change`, `touch-action`, `user-select`
  and the other pure browser hints are accepted in silence.
- A breakpoint wider than the phone now says so and names the screen, rather than reading as a missing feature.

### Fixed

- `border-style: none` removes the border. It used to leave it drawn, and every value including `solid` was
  reported as doing nothing.

## [1.22.0] - 2026-08-09

### Added

- `line-height` reaches the text. It was read and then used only to size an empty box, so nine of the fourteen
  shipped apps set a line height that never appeared.
- `pointer-events: none` lets the pointer through to whatever is behind. A scrim that only dims, a label over a
  map. Inherited, so one rule covers a whole overlay.
- `text-transform` with `uppercase`, `lowercase` and `capitalize`.
- `transform-origin`, in the keyword and length forms.

### Fixed

- `scale` and `rotate` turn a box around its centre. They turned it around its top-left corner, which is not what
  any browser does and not what any page that scales was drawn against.

## [1.21.0] - 2026-08-09

### Changed

- A page that redraws is twice as fast again. The same 206-box React page went from 128 ms to 56 ms, and a state
  update from 116 ms to 48 ms.
- The layout stopped asking for the same measurement over and over: 73600 requests per render became 20100, and
  the layout phase 95 ms became 19 ms.

### Fixed

- A deeply nested box no longer costs exponentially more than a shallow one. One line of text eight levels down
  took 40963 measurements; it takes 154.

## [1.20.0] - 2026-08-09

### Added

- Eleven more events fire: `mousedown`, `mouseup`, `mouseover`, `mouseout`, `dblclick`, `contextmenu`, `focus`,
  `blur`, `focusin`, `focusout` and `change`.
- `change` fires when a field gives the caret back with a different value than it took, so a page can save once
  instead of on every keystroke.
- An event carries `clientX`, `clientY`, `button` and `detail` - which button, how many clicks, and where in the
  page it landed.

### Changed

- A right click raises `contextmenu` instead of `click`. It still raises `back` at the document as well.
- `mouseenter` and `mouseleave` do not bubble and `mouseover` and `mouseout` do, as in a browser. One rule, one
  place.

## [1.18.0] - 2026-08-09

### Changed

- A page that redraws is about six times faster. A 206-box page built by React went from 754 ms to 128 ms, and a
  state update from the same to 116 ms.
- Text measurements are cached across renders. One render was asking TextMeshPro the same 599 questions 74000
  times, and that was 720 of those 754 ms.
- Every page build logs where its time went - `cascade / layout / paint / wire`, plus how many text measurements
  were taken and how many came from the cache. Turn it on with `LogPageBuilds`.

## [1.17.0] - 2026-08-09

### Added

- React runs. `createRoot(...).render(...)` draws, `onClick` fires, `useState` updates and a keyed list reorders -
  react-dom 18 unchanged.
- A click reports the element it landed on, not the box that caught it. That is what lets a page put one listener
  on its root and read `e.target`.
- DOM constructors: `Node`, `Element`, `HTMLElement`, `HTMLInputElement` and the rest, so `instanceof` answers.
- `new Event(type)` and `new CustomEvent(type, { detail })`, which `el.dispatchEvent(evt)` takes.

### Fixed

- react-dom drew an empty container and reported nothing. One missing global, `HTMLIFrameElement`, threw inside
  its scheduler where nothing was listening.

## [1.16.0] - 2026-08-09

### Added

- Preact runs. A component mounts, updates, reorders a keyed list and answers a click, all inside the game.
- Text nodes: `createTextNode`, `createComment`, and writing `node.data` to change one word without rebuilding
  the markup around it.
- Node navigation: `parentNode`, `firstChild`, `nextSibling`, `childNodes`, `nodeType`, `nodeName`, `ownerDocument`.
- Capture phase: `addEventListener(type, fn, true)` and `{ capture: true }` both work, and
  `stopImmediatePropagation` now differs from `stopPropagation`.
- `window` is the global object, with `innerWidth`, `innerHeight`, `addEventListener` and
  `requestAnimationFrame`.

### Changed

- A page can hang its own properties on a node. `el.whatever = x` used to throw, which is why no virtual-DOM
  renderer could mount.
- `el.tagName` is upper case, as a browser reports it. `el.localName` is the lower-case spelling.
- Properties that spell their attribute differently work: `htmlFor`, `readOnly`, `tabIndex`, `checked`,
  `selected`, plus `onclick` and every other `on...` handler.
- `style.setProperty` reaches custom properties, so `--brand` can be written from script.
- `getBoundingClientRect()` carries `top`, `left`, `right` and `bottom` next to the four `rect()` already had.

## [1.15.0] - 2026-08-08

### Added

- CSS Grid. `display: grid`, tracks in `fr`, `repeat()`, `minmax()` and `auto-fit`, `gap`, `span` and line numbers.
- `::before` and `::after` draw. `content` takes a string, `attr()` or `none`, and no `content` still means no box.
- `z-index` decides what covers what. A positioned box with a level paints above its siblings whatever the markup says.
- `margin: 0 auto` centres and `margin-left: auto` pushes to the end, on both axes. They take the free space before
  `justify-content` gets it, the way flexbox says.
- `position: relative` moves a box and leaves its space behind, so the relative-plus-absolute pattern works.

### Changed

- A Tailwind v4 build lands 93% of its declarations, up from 88%. Add `sideload-preview.css` and the Vite plugin's
  lowering pass to get there.

## [1.14.5] - 2026-08-08

### Fixed

- The "Rotate Phone" hint is back in the key strip, next to Tab and Escape, with whatever keys you have rotate
  bound to. It stopped appearing in 1.14.2 and did not come back on its own.

## [1.14.4] - 2026-08-08

### Fixed

- The report about unusable CSS is in English. It was written in German, which is no use to anyone reading
  the log of a mod written in English.

## [1.14.3] - 2026-08-08

### Fixed

- The rotate keys turn the phone again. Schedule I's 0.4.6f11 input rework moved `RotateLeft`/`RotateRight` into
  the new input system, and Sideload was still reading the button code they used to have.

## [1.14.2] - 2026-08-08

### Fixed

- Apps no longer stutter while open. The turn hint swept the whole scene four times a second looking for
  a prompt strip this build does not have, and cost 22 ms of every frame.

### Changed

- The rotate keys and their hint switch off after a few empty searches, with one line in the log saying so.
  An app can still turn the phone itself.

## [1.14.1] - 2026-08-08

### Fixed

- Closing a page you had typed in gives the keyboard back. It used to keep it, so the player could not move and Escape did nothing for the rest of the session.

### Added

- `element.blur()` hands the keyboard back from script, the counterpart to `element.focus()`.

## [1.14.0] - 2026-08-08

### Added

- Sizes in `rem`, `em`, `vh`, `vw`, `ch` and the print units, plus `calc()`, `min()`, `max()` and `clamp()`.
  One rem is 15px; write `html { font-size: 16px }` for the browser's.
- Colours in `oklch`, `oklab`, `lab`, `lch`, `hsl`, `hwb` and `color-mix`, plus `currentColor` and all 148
  named colours instead of 17.
- `@layer`, `@property`, `@supports`, width breakpoints and nested rules. Layers sort above specificity, so a
  one-class rule can beat a three-class one.
- `<meta name="sideload" content="web-defaults">` lays an undeclared box out as a row, the way a browser does.
  Pages without it keep the column.
- A Tailwind v4 build went from producing no rules at all to 268, and a v3 build from losing two thirds of its
  declarations to 44 percent.

### Fixed

- Stacked shadows appear. The first layer was drawn, and anything that stacks shadows puts a transparent one
  first, so the visible shadow was skipped.
- A selector list keeps its rule. `.a:not(.b), .c` was split at every bracket into fragments that are not
  selectors, and the whole rule was dropped.
- An element given a click handler after the page is drawn responds. It used to stay dead until something
  unrelated redrew the page.
- Reopening a page no longer reparses its script. A large bundle spends most of its startup being parsed, and
  that now happens once.

## [1.13.2] - 2026-08-08

### Fixed

- Log spam gone: every page build wrote four lines (timings, viewport, scroll areas, wiring), once a second in an
  app that redraws. `LogPageBuilds` turns them back on.

## [1.13.1] - 2026-08-08

### Fixed

- The report about unusable rules stops repeating itself. A property that does nothing whatever you set it to -
  `line-height`, `border-style`, `transition-property` - is named once instead of once per value, so a stylesheet
  with eight line heights no longer costs eight identical lines. One app's report went from ten lines to four,
  and the four that are left each say something different.

## [1.13.0] - 2026-08-08

### Added

- A mod can put a page anywhere now, not only on the phone: `Surfaces.Mount(rect, id, bundlePrefix)` renders a
  bundle into any panel it owns, with the same `s1.call` and `s1.on` channel an app has. Side Hustle's main-menu
  column is the first one.
  - The page can be written for a fixed width and scale with the panel, the way a phone app does, or work in the
    panel's own pixels. That is the `designShortSide` argument.

### Fixed

- Colours outside the phone come out as the colours you wrote. A page mounted on a screen overlay had every
  fill converted a second time, so `#808080` arrived as `#383838` and dark surfaces disappeared into whatever
  was behind them while the text stayed correct. Phone apps were never affected.

## [1.12.0] - 2026-08-08

### Added

- A page can tell when the pointer arrives on something and when it leaves, through `mouseenter` and
  `mouseleave`. They do not bubble, the same way a browser handles this pair.
- `element.rect()` says where a box ended up: `x`, `y`, `width` and `height` in css pixels, measured from the
  top left of the screen. Together with the two events above, that is what a hover tooltip needs - a floating
  label that appears over the page instead of pushing the row it belongs to out of shape.
- Every rule the engine cannot use is named in the log, with the value that got dropped. Until now only an
  unknown property name was reported and everything else was dropped without a word.
  - That covers a length it cannot read such as `1rem` or `calc(100% - 8px)`, a colour such as `oklch(...)`
    or `hsl(...)`, a value it reads and then ignores such as `align-items: baseline` or `position: relative`,
    a selector it had to reject, a skipped `@media (min-width: ...)` or `@keyframes` block, and a listener on
    an event this engine never delivers. Each one is named once per app.

### Fixed

- Thin borders show up. A one-pixel border was drawn across 1.64 device pixels and washed out into the surface
  behind it, so an outlined button could end up as a label with nothing around it. Border widths now land on
  whole screen pixels.
- A pane that swapped its whole contents opens at the top instead of halfway down itself. Scroll positions are
  still kept across a redraw, but only where the content is recognisably the same.
- The warning about `await fetch(...)` never appeared. Its check could not match anything, so the one mistake
  that freezes the game for good went unmentioned.

## [1.11.0] - 2026-08-07

### Changed

- Sideload is one file. AngleSharp, Jint and Esprima now live inside `Sideload.dll` instead of as loose
  DLLs in `UserLibs/`, so a hand install is one drop into `Mods/` and there is nothing left over when you
  remove the mod. Copies still in `UserLibs/` keep being used, so nothing breaks on an existing install.
  Delete them whenever you like.
- Matching a host's mods in Side Hustle no longer has to fetch three support libraries alongside
  Sideload, which is where that sync used to stall.

## [1.10.0] - 2026-08-07

### Added

- An app can answer a key while your phone is in your pocket. Press it and the app is up and ready to
  use, with no home screen in between. Sideload only reads keys an app asked for, and only where the
  game would let you take your phone out anyway: never while you are typing, paused, asleep, arrested,
  or standing at a station, a shop or the console.
- Two apps that want the same key do not fight over it. The key goes to whichever one notified you
  most recently, so with two messengers installed it answers whichever conversation is waiting. An app
  you are looking at keeps its own keys either way.
- An app can keep the keyboard in one text box while it is on screen, so typing a message does not also
  walk your character forward and swap two inventory slots. It only holds the box the app named, only
  while that box is visible, and it lets go the moment you click somewhere else that takes typing.
- `AppKeys` in `MelonPreferences.cfg` turns every app key off in one place. On by default.

### Fixed

- Escape and right-click leave an app on the first press while a text box has the cursor. It used to
  take two, and the first one looked like nothing happened - the game stops delivering the press at all
  while you are typing, so Sideload now delivers it itself.

## [1.9.0] - 2026-08-07

### Added

- Lists glide when you scroll them instead of jumping a notch at a time, and the wheel now works over
  the empty parts of a list as well as over its rows. An app that wants the old behaviour back sets
  `-s1-scroll: instant`.
- An app can put a dialog over the whole screen. `position: fixed` is measured against the phone screen
  and drawn over everything else; while it is up it takes the clicks and the wheel, so a mod can ask
  before doing something you cannot undo. Write it anywhere in the page - inside a scrolling list is
  fine, it still covers the screen.
- Sideload names every CSS property it does not implement, once per app, in the log. A rule it could not
  read used to be dropped in silence, which is a long afternoon for whoever wrote it.
- The Debug build now ships on the GitHub release next to the Release one, so building an app against
  Sideload no longer starts with compiling Sideload.

### Fixed

- A button inside another clickable box can be pressed. The invisible hit area sat over its own
  contents, so anything nested inside something clickable was unreachable.
- A row of boxes set to `flex: 0` keeps its size instead of collapsing to an unreadable smear. `flex: 1 0`
  and `flex: 20px` now parse the way a browser parses them.

## [1.8.2] - 2026-08-05

### Fixed

- An app you reopen has its caret back in the text field. Closing one releases the keyboard, and
  nothing handed it back unless the app happened to ask - so a terminal or search box came up looking
  ready, ate nothing, and every key you typed went to the game and started opening things instead.

## [1.8.1] - 2026-08-05

### Fixed

- Sideload reports its own version correctly again. Since 1.6.0 it announced itself as "1.5.0" to
  MelonLoader, so the console, mod managers and update checks all showed you an ancient build and
  kept telling you to update one you already had.

## [1.8.0] - 2026-08-05

### Changed

- Opening an app for the first time takes about a fifth of the time it did. Measured on the same app in the same
  session: 674 ms before, 159 ms after. Almost none of that was ever the app - it was the HTML parser, the script
  engine and TextMeshPro all being used for the first time, and whoever opened the first app paid for all of it.
  Sideload now does that work on a throwaway page while the scene is still loading.
- A page fades in over a fifth of a second the first time its app is opened, instead of appearing at the end of
  the build all at once. Later opens are unchanged - there is nothing to cover once the page exists.

## [1.7.0] - 2026-08-04

### Added

- `font-family: monospace` draws in a real monospaced face. The game ships none - Open Sans, a pixel face and
  three decorative ones - so a page that wanted a terminal or a table had nothing honest to render in. Sideload
  now builds the font from the machine's own file, trying Consolas, Cascadia Mono, Lucida Console, Courier New
  and DejaVu Sans Mono in that order, and says in the log which one it took. Nothing is shipped with the mod;
  it is the player's own font. Where none of them exists, the pixel face still steps in.

## [1.6.0] - 2026-08-04

### Added

- `app.Icon(true)` and `app.Icon(false)` put an app's home-screen square there or take it away while the game
  runs. For an app that is opened by a key and only makes sense sometimes: hash shows its square exactly while
  the game's console is switched on.
- A text field can show what accepting a suggestion would type. Put the remainder in `data-ghost` and it is
  drawn behind the caret in the field's own font; `-s1-ghost-color` styles it. Nothing to measure on your side,
  and it does not need a monospaced font.
- `caret-color` and `-s1-caret-width` let a field draw a block cursor instead of the thin line the game uses.
- `keydown` carries `hasSelection`, so a page can tell Ctrl+C-as-copy from Ctrl+C-as-interrupt.
  `data-reject-first` stops a dead key from opening a fresh line.

### Fixed

- Closing an app while its text field still had focus left the game convinced you were typing: no movement, no
  Escape, no phone key, and nothing on screen to click. The keyboard is let go when an app hides and when the
  phone goes down.
- Ctrl+Backspace deletes a word. It deleted one character, because key suppression ignored the modifier keys.
  Shift+Up still selects.
- A row built from coloured spans stays one row. `white-space: pre` now also tells the layout "this is text", so
  a block holding nothing but spans becomes a single text object. Each span used to turn into its own
  full-width box, which stacked them down the page and cost a rebuild apiece.
- Focus asked for before the first render is applied after it instead of being dropped.

## [1.5.0] - 2026-08-04

### Added

- An app can ask for keys. Name them on a text field with `data-keys="Tab ArrowUp Ctrl+R"` and they arrive as
  `keydown`, with `ctrlKey`, `shiftKey`, `altKey` and `repeat`. Only the keys an app names are taken from the
  field, so a page that names none behaves exactly as before. Letters, digits, the editing keys and F1 to F12;
  Enter and Escape are refused because they already arrive as `keydown` and `back`.
- Holding a key repeats it: 0.35 seconds before the first repeat, then one every 0.06, dropping to 0.03 after
  1.2 seconds so a long list stays reachable. `e.repeat` tells a page which is which.
- `white-space: pre` and `pre-wrap` keep the spaces a page was written with. Until now every run of whitespace
  collapsed to one, which made a column padded to twelve characters arrive as a single space.
- `-s1-mono-advance: 7px` gives every glyph the same width, so a column of values lines up under its heading.
  None of the game's fonts are monospaced, and this is the only way to get an aligned table out of them.
- `app.Show()` takes the phone out of the player's pocket and opens the app; `app.Hide()` reverses both, and
  `PhoneScreen.Raise()` and `.Lower()` move the phone on its own. Refused while the game is paused or the player
  is asleep, dead or arrested. Opening an app still does not raise the phone by itself, so a background update
  cannot yank it up.

## [1.4.2] - 2026-08-02

### Fixed

- A slow mod no longer fails the page it is answering. The time budget a page handler runs under was counting
  the mod's own work in `s1.call`, so an app whose script did almost nothing still died with "The operation has
  timed out" when the mod took a moment. The clock restarts once the mod answers; a script that will not stop is
  still caught, by the statement limit.

## [1.4.1] - 2026-08-02

### Fixed

- The seconds an app passes to `app.Notify` are used. In 1.4.0 the API took them and the notification still
  ran at the default length: the shim never looked the new host method up, so every call fell through to the
  old one. Nothing else changes, and an app that leaves the seconds off behaves as it always did.

## [1.4.0] - 2026-08-02

### Added

- An app decides how long its own notification stays up: `app.Notify(title, subtitle, seconds)`. Leave the
  seconds off and Sideload picks the timing, as before. Between 2 and 30 - the slide-in has no dismiss button,
  so an app does not get to hold a corner of the screen.

## [1.3.1] - 2026-08-02

### Fixed

- App notifications no longer cut their text off mid-word. The box sizes itself to what the app sent, so a
  headline plus a sentence fits, and a short line gets a small box instead of a mostly empty one.

### Changed

- An app notification stays up for 9 seconds instead of 5. It cannot be clicked and it does not come back, so
  it has to still be there when you look up.

## [1.3.0] - 2026-08-01

### Fixed

- Sideload works on Schedule I 0.4.6f11. That update reworked the phone's input handling and the key-hint strip
  at the bottom of the screen.
- Turning the phone uses your rotate keys again, whatever you have them bound to. Those keys moved into the
  game's new input system, so Sideload now reads the same binding the build ghost reads: rebind once, in the
  game's own options, and both pick it up.
- The "Rotate Phone" hint is back next to Tab and Escape. The game rebuilt that strip, and the line survives the
  game swapping the whole panel out mid-session.
- Right-click steps back inside an app again. The game collapsed its exit types, and right-click is the secondary
  one now.
- An open app costs 4.4 ms a frame. Finding the game's hint strip on 0.4.6f11 means searching the scene, and
  searching it every frame measured 81 ms, 62% of the whole frame. The strip is remembered while it stays on
  screen and looked up again four times a second at most.

## [1.2.0] - 2026-07-31

### Fixed

- **A scrolled list still swallowed clicks meant for whatever sat above it.** Scroll a list, then press a button
  in a bar fixed above it, and a list row took the press instead - the row was off screen, but only visually.

  Clipping in this engine is a rendering instruction and nothing else: uGUI decides what a click hit by raycasting
  Graphics, and the only thing that filters that is a component implementing `ICanvasRaycastFilter`, which is what
  `RectMask2D` is. This engine cannot use `RectMask2D` (it collapses on a rotated panel) and cannot implement the
  interface either (a Unity interface on a managed type is the unreliable virtual-override path a custom `Graphic`
  was already ruled out for). So every hit target stayed live at wherever its rect had scrolled to.

  Hit targets are now shrunk to the visible part of their element whenever the list moves, and switched off
  entirely when none of it is in view. No interface needed: the hit target is a child stretched over its element,
  so insetting it is enough, and the work happens in the content's own local space, which is rotation-free by
  construction.

  Until a page put something FIXED above a scrolling list, this only ever let a click land on a row that happened
  to be off screen - unwanted, but invisible.

- **A repaint escaped its scroll area.** Hovering a list row that had scrolled out of its viewport drew the row's
  background across whatever sat above the list - a sticky bar, a header - hiding it completely, and with no text,
  which made it look like the bar had gone blank rather than been covered.

  Clipping lives on the CanvasRenderer and is taken from a static that only holds a value while the paint walk is
  inside a scroll area. A repaint happens long after that walk - a hover, a transition frame - with the static back
  at null, so the box was redrawn with clipping switched off and reappeared wherever its rect happened to be. Each
  painted box now remembers the clip it was built under, and both repaint paths restore it.

  It needed a page with something FIXED above a scrolling list to be visible at all; until one existed the escaped
  box drew over other list rows, where it was indistinguishable from a hover.

- **A border on a box with no background drew nothing at all.** An outlined chip - `border: 1px solid`, no
  fill - was invisible, while the same border on a filled box was fine. A uniform border is drawn as the
  shader's rounded ring inside the fill's own quad, so with a transparent fill there was nothing for it to
  modulate. A uniform border over a transparent fill with square corners is now drawn as four strips instead,
  which is what a single-sided border already did. Rounded boxes keep the ring.

  It shipped invisibly because every border anyone had drawn until now was either single-sided (a list row's
  hairline) or sat on a fill (an input, a button). An app whose design language is outlined chips had no
  outlines anywhere and nothing in any log said so.
- **`border: 1px dashed <colour>` lost its colour.** The shorthand parser recognised only `solid`, `none` and
  `hidden`, so every other line style fell through to the colour parser and consumed the declaration's actual
  colour. All ten CSS line styles are now recognised; the engine still draws them solid, because it has no
  dash pattern.

## [1.1.0] - 2026-07-28

### Added

- **Paint-only styles no longer rebuild the page.** Writing `transform`, a background, a border colour, a
  corner radius or a box shadow from script now repaints just that box - the same path `:hover` has always
  taken. Everything else still rebuilds, and so does `cssText`, which can contain anything. This is the
  difference between an animation being possible and not: a rebuild destroys and recreates every GameObject
  on the page, measured at roughly half a millisecond per box, so a 200-box page cost ~100ms per frame to
  move something one pixel.

  `color` and `opacity` are deliberately NOT on the fast path even though they only affect appearance: both
  are inherited, and a repaint redraws one box, so descendants would keep the old value. They rebuild, where
  they are correct.
- **`e.offsetX` / `e.offsetY` / `e.normX` / `e.normY` on a click.** Where inside the element the pointer
  landed, in the element's own CSS pixels and as a 0..1 fraction of its size. A page that stands for
  something with its own coordinate space - a map - can finally answer "where did they point at".
- **Apps without a home-screen icon**, via `.NoIcon()`, for an app whose way in already exists somewhere
  else. With no icon, `.Open()` is the only route in.
- **`.Open()`, `.Close()` and `.IsOpen`** on an app handle: open an app from code exactly as pressing its icon
  would, closing whatever else is open and turning the phone. `AppHandle.CanOpenProgrammatically` reports
  whether the installed host understands any of this, so a mod that supplies its own entry point can refuse
  to set up against an older Sideload rather than leaving the player an app they cannot reach.
- **A companion seam** on the bridge: the app list, bundle files, framework assets and runtime images can be
  read, a handler can be invoked without a page, and host events, badges and notifications can be tapped.
  For serving the same bundle to a second screen. It grants no capability a page does not already have.

### Fixed

- **`overflow: hidden` now clips.** It was parsed and then ignored: only `auto` and `scroll` ever produced a
  clip rectangle, and only once the content was tall enough to need scrolling. A box meant as a window onto
  something bigger - a map, a graph, anything panned by a transform - let its contents draw across the rest
  of the screen and off the phone entirely.
- **A clip now follows the transforms above it.** The rectangle was derived from the layout, which by design
  knows nothing about a `transform` on an ancestor - so inside a panned or zoomed window the clip sat where
  the boxes would have been rather than where they are, and everything in it vanished.

All of it is additive - the bridge ABI stays at 1, and an older shim binds exactly as before.

## [1.0.1] - 2026-07-27

### Fixed

- A mod asking `IsOnScreen` got "yes" while the phone was closed. Vanilla's `SetIsOpen(false)` leaves the app
  panel active and still registered as the phone's `ActiveApp`, so the check that is supposed to mean "the
  player is looking at this" stayed true with the phone in their pocket - and an app that politely asks before
  raising a notification stayed silent in exactly the case the notification exists for. `IsOnScreen` now also
  requires the phone itself to be open.

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
