(function () {
  var STORAGE_KEY = 'seekclaw-theme';
  var LIGHT_HLJS = 'https://cdn.jsdelivr.net/npm/highlight.js@11/styles/github.min.css';
  var DARK_HLJS = 'https://cdn.jsdelivr.net/npm/highlight.js@11/styles/github-dark.min.css';

  function applyTheme(theme) {
    if (theme !== 'light' && theme !== 'dark') theme = 'light';
    document.documentElement.setAttribute('data-theme', theme);
    document.documentElement.setAttribute('data-bs-theme', theme);
    if (theme === 'dark') {
      document.documentElement.classList.add('dark');
    } else {
      document.documentElement.classList.remove('dark');
    }

    var link = document.getElementById('hljs-theme');
    if (link) {
      link.href = theme === 'light' ? LIGHT_HLJS : DARK_HLJS;
    }
  }

  function readTheme() {
    try {
      return localStorage.getItem(STORAGE_KEY) || 'light';
    } catch (error) {
      return 'light';
    }
  }

  function writeTheme(theme) {
    try {
      localStorage.setItem(STORAGE_KEY, theme);
    } catch (error) {
      // ignore
    }
    applyTheme(theme);
    return theme;
  }

  function toggleTheme() {
    var next = readTheme() === 'light' ? 'dark' : 'light';
    return writeTheme(next);
  }

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text);
    } else {
      var textArea = document.createElement('textarea');
      textArea.value = text;
      textArea.style.position = 'fixed';
      textArea.style.left = '-999999px';
      document.body.appendChild(textArea);
      textArea.focus();
      textArea.select();
      return new Promise(function (resolve, reject) {
        document.execCommand('copy') ? resolve() : reject();
        textArea.remove();
      });
    }
  }

  async function login(username, password) {
    try {
      var res = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: username, password: password })
      });
      return await res.json();
    } catch (e) {
      return { success: false, error: '网络通信异常，请稍后重试。' };
    }
  }

  async function register(username, password) {
    try {
      var res = await fetch('/api/auth/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: username, password: password })
      });
      return await res.json();
    } catch (e) {
      return { success: false, error: '网络通信异常，请稍后重试。' };
    }
  }

  async function logout() {
    try {
      await fetch('/api/auth/logout', { method: 'POST' });
    } catch (e) {
      // ignore
    }
    window.location.href = '/';
  }

  async function testDatabase() {
    try {
      var res = await fetch('/api/setup/test-db');
      return await res.json();
    } catch (e) {
      return { connected: false, errorMessage: e.message || '网络连接失败' };
    }
  }

  async function initializeSystem(setupData) {
    try {
      var res = await fetch('/api/setup/initialize', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(setupData)
      });
      return await res.json();
    } catch (e) {
      return { success: false, error: '初始化请求失败，请检查网络或服务器日志。' };
    }
  }

  function setupCodeBlockCopy() {
    document.querySelectorAll('.markdown-body pre').forEach(function (pre) {
      if (pre.querySelector('.vp-copy-btn')) return;

      var code = pre.querySelector('code');
      if (!code) return;

      var btn = document.createElement('button');
      btn.className = 'vp-copy-btn';
      btn.type = 'button';
      btn.setAttribute('aria-label', '复制代码');
      btn.innerHTML = '<i class="bi bi-clipboard"></i>';

      btn.addEventListener('click', function () {
        var text = code.innerText;
        copyText(text).then(function () {
          btn.innerHTML = '<i class="bi bi-check2 text-success"></i>';
          btn.classList.add('copied');
          setTimeout(function () {
            btn.innerHTML = '<i class="bi bi-clipboard"></i>';
            btn.classList.remove('copied');
          }, 2000);
        });
      });

      pre.style.position = 'relative';
      pre.appendChild(btn);
    });
  }

  var tocObserver = null;
  function setupTocObserver() {
    var links = Array.from(document.querySelectorAll('.doc-toc a'));
    if (links.length === 0) return;

    var headings = [];
    links.forEach(function (link) {
      var href = link.getAttribute('href') || '';
      var hashIndex = href.indexOf('#');
      if (hashIndex >= 0) {
        var id = decodeURIComponent(href.substring(hashIndex + 1));
        var el = document.getElementById(id);
        if (el) {
          headings.push({ id: id, el: el, link: link });
        }
      }

      if (!link.dataset.clickBound) {
        link.dataset.clickBound = 'true';
        link.addEventListener('click', function (e) {
          e.preventDefault();
          var targetHref = link.getAttribute('href') || '';
          var idx = targetHref.indexOf('#');
          if (idx >= 0) {
            var targetId = decodeURIComponent(targetHref.substring(idx + 1));
            var targetEl = document.getElementById(targetId);
            if (targetEl) {
              var navOffset = 80;
              var top = targetEl.getBoundingClientRect().top + window.scrollY - navOffset;
              window.scrollTo({ top: top, behavior: 'smooth' });
              try {
                history.replaceState(null, '', targetHref);
              } catch (err) { }
            }
          }
        });
      }
    });

    if (headings.length === 0) return;

    function onScroll() {
      var scrollY = window.scrollY + 100;
      var activeHeading = headings[0];
      for (var i = 0; i < headings.length; i++) {
        if (headings[i].el.getBoundingClientRect().top + window.scrollY <= scrollY) {
          activeHeading = headings[i];
        } else {
          break;
        }
      }

      links.forEach(function (l) { l.classList.remove('active'); });
      if (activeHeading && activeHeading.link) {
        activeHeading.link.classList.add('active');
      }
    }

    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  function setupImageZoom() {
    var lightbox = document.getElementById('vp-img-lightbox');
    if (!lightbox) {
      lightbox = document.createElement('div');
      lightbox.id = 'vp-img-lightbox';
      lightbox.className = 'vp-img-lightbox';
      var img = document.createElement('img');
      lightbox.appendChild(img);
      lightbox.addEventListener('click', function () {
        lightbox.classList.remove('active');
      });
      document.body.appendChild(lightbox);
    }

    document.querySelectorAll('.markdown-body img').forEach(function (imgEl) {
      if (imgEl.dataset.zoomBound) return;
      imgEl.dataset.zoomBound = 'true';
      imgEl.addEventListener('click', function () {
        var lbImg = lightbox.querySelector('img');
        if (lbImg) {
          lbImg.src = imgEl.src;
          lbImg.alt = imgEl.alt || '';
          lightbox.classList.add('active');
        }
      });
    });
  }

  function enhanceMarkdown() {
    document.querySelectorAll('pre code').forEach(function (block) {
      if (window.hljs && !block.dataset.highlighted) {
        window.hljs.highlightElement(block);
        block.dataset.highlighted = 'true';
      }
    });

    setupCodeBlockCopy();
    setupImageZoom();

    if (window.mermaid) {
      var isDark = readTheme() === 'dark';
      window.mermaid.initialize({
        startOnLoad: false,
        theme: isDark ? 'dark' : 'default',
        themeVariables: {
          primaryColor: isDark ? '#10b981' : '#059669',
          primaryTextColor: isDark ? '#f0f6fc' : '#0f172a',
          lineColor: isDark ? '#30363d' : '#cbd5e1'
        },
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

    setupTocObserver();
  }

  applyTheme(readTheme());

  window.SeekClaw = {
    enhanceMarkdown: enhanceMarkdown,
    applyTheme: applyTheme,
    getTheme: readTheme,
    toggleTheme: toggleTheme,
    copyText: copyText,
    login: login,
    register: register,
    logout: logout,
    testDatabase: testDatabase,
    initializeSystem: initializeSystem
  };
})();


