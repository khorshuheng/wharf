// Bridge between the Blazor components and the replica engine. Metadata
// crosses the .NET/JS boundary; file bytes stay in OPFS and are only handled
// here.

import { Store } from './store.js';
import * as Sync from './sync.js';

let dotNet = null;
let polling = false;
let installAnnounced = false;

export function init(reference) {
  dotNet = reference;

  Sync.onStatus((state, detail) => {
    const text = describe(state, detail);
    dotNet?.invokeMethodAsync('OnSyncStatus', state, text).catch(() => {});
  });

  startPolling();
  watchInstallPrompt();
}

function describe(state, detail) {
  switch (state) {
    case 'downloading':
      return `downloading ${detail?.done ?? 0}/${detail?.total ?? 0}`;
    case 'reseeding':
      return `restoring ${detail?.done ?? 0}/${detail?.total ?? 0}`;
    case 'replica-incomplete':
      return `${detail?.failures?.length ?? 0} file(s) not restored`;
    case 'offline':
      return detail || 'server unreachable';
    default:
      return '';
  }
}

function startPolling() {
  if (polling) return;
  polling = true;

  setInterval(() => {
    if (document.visibilityState === 'visible') Sync.run();
  }, 10_000);

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') Sync.run();
  });

  window.addEventListener('online', () => Sync.run());
}

// beforeinstallprompt can fire after Blazor has already hydrated.
function watchInstallPrompt() {
  setInterval(() => {
    if (installAnnounced) return;
    if (!window.wharfInstall?.available) return;

    installAnnounced = true;
    dotNet?.invokeMethodAsync('OnInstallAvailable').catch(() => {});
  }, 2000);
}

export async function list() {
  const entries = await Store.getLiveEntries();
  return entries.map((entry) => ({
    path: entry.path,
    size: entry.size,
    mtime: entry.mtime,
    blob: entry.blob,
  }));
}

export async function storage() {
  const estimate = await Store.estimate();
  return { usage: estimate.usage || 0, quota: estimate.quota || 0 };
}

export async function syncNow() {
  await Sync.run();
}

export async function requestPersistence() {
  await Store.requestPersistence();
}

export async function openPicker(inputId) {
  document.getElementById(inputId)?.click();
}

export async function download(path) {
  const entries = await Store.getLiveEntries();
  const entry = entries.find((candidate) => candidate.path === path);
  if (!entry) return;

  const blob = await Sync.ensureLocal(entry);
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = path.split('/').pop();
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

export async function remove(path) {
  const entries = await Store.getLiveEntries();
  const entry = entries.find((candidate) => candidate.path === path);
  if (!entry) return;

  await Sync.remove(entry);
}

export function installAvailable() {
  return Boolean(window.wharfInstall?.available);
}

export function showIosHint() {
  return Boolean(window.wharfInstall?.showIosHint);
}

export function promptInstall() {
  return window.wharfPromptInstall ? window.wharfPromptInstall() : Promise.resolve(false);
}

export function bindDropZone(zoneId, inputId, uploadsId, reference) {
  const zone = document.getElementById(zoneId);
  const input = document.getElementById(inputId);
  const uploads = document.getElementById(uploadsId);
  if (!zone || !input || !uploads) return;

  for (const type of ['dragenter', 'dragover']) {
    zone.addEventListener(type, (event) => {
      event.preventDefault();
      zone.classList.add('over');
    });
  }

  for (const type of ['dragleave', 'drop']) {
    zone.addEventListener(type, (event) => {
      event.preventDefault();
      zone.classList.remove('over');
    });
  }

  zone.addEventListener('drop', (event) => {
    const files = [...(event.dataTransfer?.files || [])];
    if (files.length) handleFiles(files, uploads, reference);
  });

  input.addEventListener('change', () => {
    const files = [...input.files];
    input.value = '';
    if (files.length) handleFiles(files, uploads, reference);
  });
}

async function handleFiles(files, container, reference) {
  for (const file of files) {
    const row = addProgress(container, file.name);
    try {
      await Sync.uploadFile(file, (ratio) => row.update(ratio));
      row.done();
    } catch {
      row.fail('error');
    }
  }

  await reference.invokeMethodAsync('OnFilesChanged');
}

function addProgress(container, name) {
  const element = document.createElement('div');
  element.className = 'upload';
  element.innerHTML = `
    <span class="upload-name"></span>
    <div class="bar"><div></div></div>
    <span class="upload-state"></span>`;

  element.querySelector('.upload-name').textContent = name;
  const bar = element.querySelector('.bar > div');
  const state = element.querySelector('.upload-state');
  container.appendChild(element);

  return {
    update(ratio) {
      const percent = Math.round(ratio * 100);
      bar.style.width = `${percent}%`;
      state.textContent = `${percent}%`;
    },
    done() {
      bar.style.width = '100%';
      state.textContent = 'saved';
      setTimeout(() => element.remove(), 1500);
    },
    fail(reason) {
      element.classList.add('failed');
      state.textContent = `failed (${reason})`;
    },
  };
}
