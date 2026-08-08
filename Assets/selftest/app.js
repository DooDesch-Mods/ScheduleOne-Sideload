// Sideload self-test. Everything below the header is built and driven from here, so if this page behaves the
// engine's script path works end to end: queries, listeners, element creation, classes, inline styles, input,
// timers and the host bridge.
//
// Written as ordinary modern JavaScript on purpose - it doubles as proof of what the engine accepts.

const $ = (id) => document.getElementById(id);

class SelfTest {
  #tasks = JSON.parse(s1.storage.get('tasks', '[]'));
  #accent = s1.storage.get('accent', 'off') === 'on';

  #list = $('tasks');
  #entry = $('entry');
  #status = $('status');
  #clock = $('clock');
  #keys = $('keys');
  #mono = $('mono');
  #keyCount = 0;

  start() {
    $('add').addEventListener('click', () => this.#add());

    $('clear').addEventListener('click', () => {
      this.#tasks = [];
      this.#save();
      this.#render();
    });

    $('theme').addEventListener('click', () => {
      this.#accent = !this.#accent;
      document.body.classList.toggle('accent-mode');
      this.#save();
      this.#render();
    });

    this.#entry.addEventListener('input', ({ value }) => {
      this.#status.textContent = value ? `Typing: ${value.length} char(s)` : 'Ready.';
    });

    // The keyboard channel. Only the keys named in data-keys arrive here; everything else types normally, and the
    // caret must NOT move when the arrows are pressed - that is what the guard is for.
    this.#entry.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { this.#add(); return; }

      const mods = [e.ctrlKey && 'Ctrl', e.shiftKey && 'Shift', e.altKey && 'Alt'].filter(Boolean).join('+');
      const name = mods ? `${mods}+${e.key}` : e.key;
      this.#keys.textContent = `keys: ${name}${e.repeat ? '  (held)' : ''}  x${++this.#keyCount}`;
    });

    this.#renderMono();

    // A repeating timer, driven by the mod's update loop rather than by a thread.
    setInterval(() => { this.#clock.textContent = s1.call('host.clock'); }, 1000);

    if (this.#accent) document.body.classList.add('accent-mode');

    this.#clock.textContent = s1.call('host.clock');
    console.log('self-test ready:', s1.call('host.info'));
    this.#render();
  }

  #save() {
    s1.storage.set('tasks', JSON.stringify(this.#tasks));
    s1.storage.set('accent', this.#accent ? 'on' : 'off');
  }

  // Three columns padded with spaces alone. They only line up if every glyph gets the same advance, so this is the
  // whole test: read it, and either the pipes form a straight edge or -s1-mono-advance is not reaching the text.
  //
  // <br> rather than "\n": a newline inside a text node is whitespace, and whitespace collapses to a single space
  // exactly as `white-space: normal` says it should. Which means the text has to be escaped first, because "<item>"
  // would otherwise be parsed as a tag and vanish.
  #renderMono() {
    const rows = [['give', '<item> [qty]', 'Vanilla'],
                  ['ogkush', 'strain', 'Vanilla'],
                  ['brickpress', 'item', 'Litterally'],
                  ['i', 'narrow glyph', 'Vanilla'],
                  ['WWWWWWWWWW', 'wide glyphs', 'Vanilla']];

    const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    this.#mono.innerHTML = rows
      .map(([a, b, c]) => esc(`${a.padEnd(12)}|${b.padEnd(14)}|${c}`))
      .join('<br>');
  }

  #add() {
    const text = this.#entry.value?.trim();
    if (!text) {
      this.#status.textContent = 'Type something first.';
      return;
    }

    this.#tasks.push({ text, done: false });
    this.#entry.value = '';
    this.#save();
    this.#render();
  }

  #render() {
    this.#list.replaceChildren();

    if (this.#tasks.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'item muted';
      empty.textContent = 'No tasks yet - type one and press Add.';
      this.#list.appendChild(empty);
    }

    for (const [index, { text, done }] of this.#tasks.entries()) {
      const row = document.createElement('div');
      row.className = done ? 'item done' : 'item';
      row.textContent = `${done ? '[x]' : '[ ]'} ${text}`;

      // Inline styles reach the cascade as a style attribute, so this is a normal high-priority declaration.
      if (this.#accent) row.style.borderColor = '#5E6AD2';

      // `index` is bound per iteration, so no closure workaround is needed.
      row.addEventListener('click', () => {
        this.#tasks[index].done = !this.#tasks[index].done;
        this.#save();
        this.#render();
      });

      this.#list.appendChild(row);
    }

    const open = this.#tasks.filter((t) => !t.done).length;
    this.#status.textContent = `${this.#tasks.length} task(s), ${open} open`;
  }
}

new SelfTest().start();

// Ein Listener auf ein Ereignis, das diese Engine nie zustellt - die fuenfte stille Klasse.
// Erwartet: eine Zeile im Log, nicht ein Handler, der stumm nie laeuft. Siehe app.css unten.
document.getElementById("clock").addEventListener("change", () => {});
