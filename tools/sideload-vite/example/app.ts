/// <reference path="../types/sideload.d.ts" />

// The stylesheet is imported, not linked: that is how a Vite project wires CSS, and it is what lets the
// plugin lower the sheet the toolchain produced rather than the one you wrote.
import './app.css';

// Plain TypeScript against the engine's own API. The types come from DomApi.cs, so the editor knows
// that `document` here has six members and an element is not a browser Element.

const answer = document.getElementById('answer');
const go = document.getElementById('go');

go?.addEventListener('click', () => {
  const said: string = s1.call('example.hello', 'from the page');
  if (answer) answer.textContent = said || 'the mod said nothing';
});

s1.on('example.changed', (payload: string) => {
  if (answer) answer.textContent = payload;
});
