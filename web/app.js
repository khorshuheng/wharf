import { Store } from './store.js';
import * as Sync from './sync.js';

const POLL_MS = 10_000;

let entries = [];

const $ = (id) => document.getElementById(id);

function fmtSize(bytes) {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(unit === 0 || value >= 10 ? 0 : 1)} ${units[unit]}`;
}

function fmtDate(ms) {
  return new Date(ms).toLocaleString();
}

function setStatus(tone, text) {
  $('status').textContent = text;
  $('status-dot').className = 'dot ' + (tone || '');
  $('sync-detail').textContent = text === 'in sync' ? '' : text;
}

function showAuth() {
  $('auth').classList.remove('hidden');
  if (Sync.getToken()) $('token').value = Sync.getToken();
  $('token').focus();
}

function hideAuth() {
  $('auth').classList.add('hidden');
}

async function loadEntries() {
  entries = await Store.getLiveEntries();
  render();
  updateStorage();
}

function render() {
  const list = $('files');
  const files = entries
    .filter((entry) => entry.type === 'file')
    .sort((a, b) => a.path.localeCompare(b.path));

  $('empty').classList.toggle('hidden', files.length > 0);
  list.replaceChildren();

  for (const entry of files) {
    const node = $('file-template').content.firstElementChild.cloneNode(true);
    const name = node.querySelector('.file-name');
    name.textContent = entry.path.replace(/^inbox\//, '');
    name.href = '#';
    name.addEventListener('click', (event) => {
      event.preventDefault();
      download(entry);
    });

    node.querySelector('.file-meta').textContent =
      `${fmtSize(entry.size)} · ${fmtDate(entry.mtime)}`;
    node.querySelector('.download').addEventListener('click', () => download(entry));
    node.querySelector('.delete').addEventListener('click', () => remove(entry));

    list.appendChild(node);
  }
}

async function updateStorage() {
  const { usage, quota } = await Store.estimate();
  const files = entries.filter((entry) => entry.type === 'file');
  const total = files.reduce((sum, entry) => sum + (entry.size || 0), 0);
  const parts = [`${files.length} replicated`, fmtSize(total)];

  if (usage) parts.push(`${fmtSize(usage)} on disk`);
  if (quota) parts.push(`${fmtSize(quota)} quota`);

  $('storage').textContent = parts.join(' · ');
}

async function download(entry) {
  try {
    const blob = await Sync.ensureLocal(entry);
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = entry.path.split('/').pop();
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  } catch {
    alert('This file is not available locally and the server could not be reached.');
  }
}

async function remove(entry) {
  if (!confirm(`Delete ${entry.path}?`)) return;

  try {
    await Sync.remove(entry);
    await loadEntries();
  } catch {
    alert('Delete failed.');
  }
}

function addProgress(name) {
  const element = document.createElement('div');
  element.className = 'upload';
  element.innerHTML = `
    <span class="upload-name"></span>
    <div class="bar"><div></div></div>
    <span class="upload-state"></span>`;

  element.querySelector('.upload-name').textContent = name;
  const bar = element.querySelector('.bar > div');
  const state = element.querySelector('.upload-state');
  $('progress').appendChild(element);

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

async function handleFiles(files) {
  for (const file of files) {
    const row = addProgress(file.name);
    try {
      await Sync.uploadFile(file);
      row.done();
    } catch {
      row.fail('error');
    }
  }

  await loadEntries();
}

function handleStatus(state, detail) {
  switch (state) {
    case 'unauthorized':
      setStatus('bad', 'locked');
      showAuth();
      break;

    case 'checking':
      setStatus('', 'checking');
      break;

    case 'downloading':
      setStatus('', `downloading ${detail.done}/${detail.total}`);
      break;

    case 'reseeding':
      setStatus('', `restoring ${detail.done}/${detail.total}`);
      break;

    case 'replica-incomplete':
      setStatus('bad', `${detail.failures.length} file(s) not restored`);
      loadEntries();
      break;

    case 'offline':
      setStatus('bad', 'offline');
      loadEntries();
      break;

    case 'ready':
    default:
      setStatus('ok', 'in sync');
      hideAuth();
      loadEntries();
      break;
  }
}

function startPolling() {
  setInterval(() => {
    if (document.visibilityState === 'visible') Sync.run();
  }, POLL_MS);

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') Sync.run();
  });

  window.addEventListener('online', () => Sync.run());
}

async function boot() {
  await Store.open();
  await loadEntries();

  const persisted = await Store.requestPersistence();
  if (!persisted) {
    $('persist-note').textContent =
      'Storage is not marked persistent; the browser may evict the replica.';
    $('persist-note').classList.remove('hidden');
  }

  Sync.onStatus(handleStatus);
  await Sync.run();
}

$('auth-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  Sync.setToken($('token').value.trim());
  hideAuth();
  await Sync.run();
});

const drop = $('drop');
for (const type of ['dragenter', 'dragover']) {
  drop.addEventListener(type, (event) => {
    event.preventDefault();
    drop.classList.add('over');
  });
}
for (const type of ['dragleave', 'drop']) {
  drop.addEventListener(type, (event) => {
    event.preventDefault();
    drop.classList.remove('over');
  });
}
drop.addEventListener('drop', (event) => {
  const files = [...(event.dataTransfer?.files || [])];
  if (files.length) handleFiles(files);
});

$('browse').addEventListener('click', () => $('file-input').click());
$('file-input').addEventListener('change', (event) => {
  const files = [...event.target.files];
  event.target.value = '';
  if (files.length) handleFiles(files);
});

$('refresh').addEventListener('click', () => Sync.run());

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}

boot().catch(() => setStatus('bad', 'startup failed'));
