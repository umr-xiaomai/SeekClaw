(function () {
  var STORAGE_KEY = 'seekclaw-theme';
  var LIGHT_HLJS = 'https://cdn.jsdelivr.net/npm/highlight.js@11/styles/github.min.css';
  var DARK_HLJS = 'https://cdn.jsdelivr.net/npm/highlight.js@11/styles/github-dark.min.css';

  function applyTheme(theme) {
    if (theme !== 'light' && theme !== 'dark') theme = 'light';
    document.documentElement.setAttribute('data-theme', theme);
    document.documentElement.setAttribute('data-bs-theme', theme);

    var link = document.getElementById('hljs-theme');
    if (link) {
      link.href = theme === 'light' ? LIGHT_HLJS : DARK_HLJS;
    }
  }

  function readTheme() {
    try {
      return localStorage.getItem(STORAGE_KEY) || 'light';
    } catch (error) {
      return 'dark';
    }
  }

  function writeTheme(theme) {
    try {
      localStorage.setItem(STORAGE_KEY, theme);
    } catch (error) {
      // ignore private mode
    }
    applyTheme(theme);
    return theme;
  }

  function toggleTheme() {
    var next = readTheme() === 'light' ? 'dark' : 'light';
    return writeTheme(next);
  }

  function enhanceMarkdown() {
    document.querySelectorAll('pre code').forEach(function (block) {
      if (window.hljs && !block.dataset.highlighted) {
        window.hljs.highlightElement(block);
        block.dataset.highlighted = 'true';
      }
    });

    if (window.mermaid) {
      window.mermaid.initialize({
        startOnLoad: false,
        theme: readTheme() === 'dark' ? 'dark' : 'default',
        securityLevel: 'loose'
      });

      document.querySelectorAll('.language-mermaid').forEach(function (block) {
        if (!block.dataset.processed) {
          block.dataset.processed = 'true';
          window.mermaid.run({ nodes: [block] }).catch(function (error) {
            console.warn('Mermaid render failed', error);
          });
        }
      });
    }
  }

  applyTheme(readTheme());

  window.SeekClaw = {
    enhanceMarkdown: enhanceMarkdown,
    applyTheme: applyTheme,
    getTheme: readTheme,
    toggleTheme: toggleTheme
  };
})();


