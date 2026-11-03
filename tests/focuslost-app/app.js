// Replaces the row around the focused field, the way a list renderer redraws one item after a commit.
// tests/focus-lost.ps1 calls redraw() through eval while the field has the caret.
var redraws = 0;

function redraw() {
  var list = document.getElementById('list');
  var old = list.firstElementChild;
  var value = old.querySelector('input').value;

  redraws++;
  var row = document.createElement('div');
  row.className = 'row';
  row.id = 'row-' + redraws;
  var input = document.createElement('input');
  input.id = 'qty-' + redraws;
  input.value = value;
  wire(input);
  row.appendChild(input);

  list.replaceChild(row, old);
  document.getElementById('status').textContent = 'redraws: ' + redraws;
  return 'replaced row-' + (redraws - 1) + ' with row-' + redraws;
}

function wire(input) {
  input.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') redraw();
  });
}

wire(document.getElementById('qty-0'));
