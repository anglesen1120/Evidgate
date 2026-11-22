// Runs synchronously from <head> (the CSP allows 'self' scripts only, so it cannot be inline) and
// mirrors ThemeService: an explicit stored choice sets data-theme before first paint; no choice
// leaves it unset so the CSS follows prefers-color-scheme.
(function () {
  try {
    var stored = window.localStorage.getItem('evidgate-theme');
    if (stored === 'light' || stored === 'dark') {
      document.documentElement.setAttribute('data-theme', stored);
    }
  } catch (_) {
    // Storage can be denied; the CSS then follows the OS preference.
  }
})();
