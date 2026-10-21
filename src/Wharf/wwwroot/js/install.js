// Installed before the Blazor runtime so beforeinstallprompt is never missed.
(() => {
  const isIos = /iphone|ipad|ipod/i.test(navigator.userAgent);
  const standalone =
    window.matchMedia('(display-mode: standalone)').matches ||
    Boolean(window.navigator.standalone);

  const state = {
    available: false,
    deferred: null,
    showIosHint: isIos && !standalone,
  };

  window.wharfInstall = state;

  window.addEventListener('beforeinstallprompt', (event) => {
    event.preventDefault();
    state.deferred = event;
    state.available = true;
  });

  window.addEventListener('appinstalled', () => {
    state.available = false;
    state.deferred = null;
  });

  window.wharfPromptInstall = async () => {
    if (!state.deferred) return false;

    state.deferred.prompt();
    const choice = await state.deferred.userChoice;
    state.deferred = null;
    state.available = false;

    return choice?.outcome === 'accepted';
  };

  if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => {
      navigator.serviceWorker.register('/service-worker.js').catch(() => {});
    });
  }
})();
