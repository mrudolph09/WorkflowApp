(function () {
  'use strict';

  var encoder = new TextEncoder();
  var decoder = new TextDecoder();

  var term = new Terminal({
    allowProposedApi: true,
    convertEol: false,
    cursorBlink: true,
    fontFamily: 'Cascadia Mono, Consolas, monospace',
    fontSize: 13,
    scrollback: 5000,
    theme: {
      background: '#1e1e1e',
      foreground: '#e0e0e0',
      cursor: '#7986cb',
      selectionBackground: '#3949ab'
    }
  });

  var fitAddon = new FitAddon.FitAddon();
  term.loadAddon(fitAddon);
  term.open(document.getElementById('terminal'));

  function post(message) {
    window.chrome.webview.postMessage(JSON.stringify(message));
  }

  function toBase64(bytes) {
    var binary = '';
    for (var i = 0; i < bytes.length; i++) {
      binary += String.fromCharCode(bytes[i]);
    }
    return window.btoa(binary);
  }

  function fromBase64(b64) {
    var binary = window.atob(b64);
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  }

  // Keystrokes travel as UTF-8 bytes so the C# side never has to guess an encoding.
  term.onData(function (data) {
    post({ type: 'in', b64: toBase64(encoder.encode(data)) });
  });

  term.onResize(function (size) {
    post({ type: 'resize', cols: size.cols, rows: size.rows });
  });

  var resizeTimer = null;
  function scheduleFit() {
    if (resizeTimer !== null) {
      window.clearTimeout(resizeTimer);
    }
    // Debounced: the pty must be able to answer one resize before the next arrives.
    resizeTimer = window.setTimeout(function () {
      resizeTimer = null;
      try {
        fitAddon.fit();
      } catch (e) {
        // The element is not laid out yet; the next resize will retry.
      }
    }, 80);
  }

  window.addEventListener('resize', scheduleFit);

  window.chrome.webview.addEventListener('message', function (event) {
    var message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;

    if (message.type === 'out') {
      term.write(fromBase64(message.b64));
    } else if (message.type === 'clear') {
      term.reset();
    }
  });

  // Reads the rendered screen, not the raw byte stream: the auto-answer rules must match what a
  // human sees, not a soup of escape sequences.
  window.wfSnapshot = function (n) {
    var buffer = term.buffer.active;
    var start = Math.max(0, buffer.length - n);
    var lines = [];
    for (var i = start; i < buffer.length; i++) {
      var line = buffer.getLine(i);
      if (line) {
        lines.push(line.translateToString(true));
      }
    }
    return lines.join('\n');
  };

  scheduleFit();
  post({ type: 'ready' });
}());
