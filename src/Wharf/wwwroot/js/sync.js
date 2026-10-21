// Sync engine for the client replica.
//
// The client stores every manifest entry in IndexedDB and every file body in
// OPFS. It detects a server reset (instance id change, or a cursor ahead of the
// server's sequence) and, because it holds the bytes, automatically uploads the
// local replica back before resuming normal sync.

import { Store } from './store.js';

const API = '/api/v1';
const PAGE = 500;
const DOWNLOAD_WORKERS = 3;

let token = localStorage.getItem('wharf.token') || '';
let running = false;
let statusListener = () => {};

class CodedError extends Error {
  constructor(code, message = code) {
    super(message);
    this.code = code;
  }
}

export function setToken(value) {
  token = value || '';
  if (token) localStorage.setItem('wharf.token', token);
  else localStorage.removeItem('wharf.token');
}

export function getToken() {
  return token;
}

export function onStatus(listener) {
  statusListener = listener;
}

function status(state, detail) {
  statusListener(state, detail);
}

function encodePath(path) {
  return path.split('/').map(encodeURIComponent).join('/');
}

function hexToBase64(hex) {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < bytes.length; i += 1) {
    bytes[i] = Number.parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  }

  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const response = await fetch(API + path, { ...options, headers });
  if (response.status === 401) throw new CodedError('unauthorized');
  return response;
}

function putFile(path, file, mtime, contentType, digestHex, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', `${API}/files/${encodePath(path)}?on_conflict=rename`);

    if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`);
    xhr.setRequestHeader('Content-Type', contentType || 'application/octet-stream');
    xhr.setRequestHeader('X-File-Mtime', String(mtime || Date.now()));
    if (digestHex) xhr.setRequestHeader('Content-Digest', `sha-256=:${hexToBase64(digestHex)}:`);

    if (onProgress) {
      xhr.upload.onprogress = (event) => {
        if (event.lengthComputable) onProgress(event.loaded / event.total);
      };
    }

    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText).entry);
        } catch {
          resolve(null);
        }
      } else {
        reject(new CodedError(`upload_${xhr.status}`));
      }
    };

    xhr.onerror = () => reject(new CodedError('upload_network'));
    xhr.send(file);
  });
}

async function applyEntry(entry) {
  if (entry.deleted) {
    const existing = await Store.getEntry(entry.path);
    await Store.deleteEntry(entry.path);

    if (existing?.blob) {
      const references = await Store.countByBlob(existing.blob);
      if (references === 0) await Store.deleteBlob(existing.blob);
    }

    return;
  }

  await Store.putEntry(entry);
}

async function applyDelta(first) {
  const instance = first.instance_id;
  let page = first;

  for (;;) {
    for (const entry of page.entries) await applyEntry(entry);
    await Store.setMeta('cursor', String(page.next));

    if (!page.has_more) break;

    const response = await api(`/manifest?since=${page.next}&limit=${PAGE}`);
    if (response.status === 410) throw new CodedError('cursor_expired');
    if (!response.ok) throw new CodedError(`manifest_${response.status}`);

    page = await response.json();
    if (page.instance_id !== instance) throw new CodedError('instance_changed');
  }

  await Store.setMeta('instance', instance);
  return instance;
}

async function applySnapshot(first) {
  const instance = first.instance_id;
  const view = new Map();
  let page = first;
  let next = first.next;

  for (;;) {
    for (const entry of page.entries) {
      if (entry.deleted) view.delete(entry.path);
      else view.set(entry.path, entry);
    }
    next = page.next;

    if (!page.has_more) break;

    const response = await api(`/manifest?since=${page.next}&limit=${PAGE}`);
    if (response.status === 410) throw new CodedError('cursor_expired');
    if (!response.ok) throw new CodedError(`manifest_${response.status}`);

    page = await response.json();
    if (page.instance_id !== instance) throw new CodedError('instance_changed');
  }

  await Store.replaceEntries([...view.values()]);
  await Store.setMeta('instance', instance);
  await Store.setMeta('cursor', String(next));
  return instance;
}

async function downloadBlob(entry) {
  const response = await api(`/blobs/sha256/${entry.blob}`);
  if (!response.ok) throw new Error(`blob ${response.status}`);

  if (response.body) await Store.putBlob(entry.blob, response.body);
  else await Store.putBlob(entry.blob, await response.blob());

  // A truncated body would otherwise be mistaken for a complete replica.
  const file = await Store.getBlob(entry.blob);
  if (entry.size != null && file.size !== entry.size) {
    await Store.deleteBlob(entry.blob);
    throw new Error('blob size mismatch');
  }
}

async function downloadMissing() {
  const entries = await Store.getLiveEntries();
  const missing = [];

  for (const entry of entries) {
    if (entry.blob && !(await Store.hasBlob(entry.blob))) missing.push(entry);
  }

  if (missing.length === 0) return;

  let done = 0;
  status('downloading', { done, total: missing.length });

  const queue = [...missing];
  const workers = Array.from({ length: Math.min(DOWNLOAD_WORKERS, queue.length) }, async () => {
    while (queue.length > 0) {
      const entry = queue.shift();
      try {
        await downloadBlob(entry);
      } catch {
        // Leave the entry in the manifest; the next pass retries it.
      }

      done += 1;
      status('downloading', { done, total: missing.length });
    }
  });

  await Promise.all(workers);
}

let lastGc = 0;

async function gcBlobs(force = false) {
  const now = Date.now();
  if (!force && now - lastGc < 60 * 60 * 1000) return;
  lastGc = now;

  const entries = await Store.getAllEntries();
  const referenced = new Set(entries.filter((entry) => entry.blob).map((entry) => entry.blob));
  await Store.gcBlobs(referenced);
}

async function fullSync() {
  const view = new Map();
  let instance = null;
  let since = 0;

  for (;;) {
    const response = await api(`/manifest?since=${since}&limit=${PAGE}`);
    if (!response.ok) throw new Error(`manifest ${response.status}`);

    const page = await response.json();
    if (instance && page.instance_id !== instance)
      throw new Error('instance changed during full sync');

    instance = page.instance_id;
    for (const entry of page.entries) {
      if (entry.deleted) view.delete(entry.path);
      else view.set(entry.path, entry);
    }

    since = page.next;
    if (!page.has_more) break;
  }

  await Store.replaceEntries([...view.values()]);
  await Store.setMeta('instance', instance);
  await Store.setMeta('cursor', String(since));

  await downloadMissing();
  await gcBlobs(true);
  await Store.setMeta('last_sync', String(Date.now()));
}

async function reseed() {
  const serverEntries = new Map();
  let instance = null;
  let since = 0;

  // 1. Learn what the new server already has.
  for (;;) {
    const response = await api(`/manifest?since=${since}&limit=${PAGE}`);
    if (!response.ok) throw new Error(`manifest ${response.status}`);

    const page = await response.json();
    if (instance && page.instance_id !== instance)
      throw new Error('instance changed during reseed');

    instance = page.instance_id;
    for (const entry of page.entries) {
      if (!entry.deleted) serverEntries.set(entry.path, entry);
    }

    since = page.next;
    if (!page.has_more) break;
  }

  const local = await Store.getAllEntries();
  const failures = [];

  // 2. Restore empty directories that the server no longer has.
  for (const directory of local.filter((entry) => entry.type === 'directory')) {
    if (serverEntries.has(directory.path)) continue;

    try {
      await api(`/directories/${encodePath(directory.path)}`, { method: 'PUT' });
    } catch {
      // A file may occupy the path; leave it for the file pass.
    }
  }

  // 3. Restore files. Content addressing makes this a set difference, and
  //    on_conflict=rename keeps both sides when the server has different
  //    content at the same path.
  const files = local.filter((entry) => entry.type === 'file');
  let done = 0;

  for (const entry of files) {
    const server = serverEntries.get(entry.path);
    if (server && server.blob === entry.blob) {
      done += 1;
      continue;
    }

    let file;
    try {
      file = await Store.getBlob(entry.blob);
    } catch {
      failures.push(entry.path);
      done += 1;
      continue;
    }

    try {
      await putFile(entry.path, file, entry.mtime, entry.content_type, entry.blob);
    } catch {
      failures.push(entry.path);
    }

    done += 1;
    status('reseeding', { done, total: files.length });
  }

  return failures;
}

async function handleReset(localInstance) {
  const entries = await Store.getAllEntries();
  const hasLocal = entries.length > 0;

  if (!localInstance || !hasLocal) {
    await fullSync();
    status('ready');
    return;
  }

  const files = entries.filter((entry) => entry.type === 'file');
  status('reseeding', { done: 0, total: files.length });

  const failures = await reseed();
  await fullSync();
  status(failures.length > 0 ? 'replica-incomplete' : 'ready', { failures });
}

export async function run() {
  if (running) return;
  running = true;

  try {
    const localInstance = await Store.getMeta('instance');
    const cursor = Number((await Store.getMeta('cursor')) || 0);

    status('checking');

    const firstResponse = await api(`/manifest?since=${cursor}&limit=${PAGE}`);
    if (firstResponse.status === 410) {
      await handleReset(localInstance);
      return;
    }
    if (!firstResponse.ok) throw new Error(`manifest ${firstResponse.status}`);

    const first = await firstResponse.json();

    if (localInstance && first.instance_id !== localInstance) {
      await handleReset(localInstance);
      return;
    }

    try {
      if (cursor === 0) await applySnapshot(first);
      else await applyDelta(first);
    } catch (error) {
      if (error.code === 'cursor_expired' || error.code === 'instance_changed') {
        await handleReset(localInstance);
        return;
      }
      throw error;
    }

    await downloadMissing();
    await gcBlobs();
    await Store.setMeta('last_sync', String(Date.now()));
    status('ready');
  } catch (error) {
    if (error.code === 'unauthorized') status('unauthorized');
    else status('offline', error.message);
  } finally {
    running = false;
  }
}

export async function uploadFile(file, onProgress) {
  const entry = await putFile(
    `inbox/${file.name}`,
    file,
    file.lastModified || Date.now(),
    file.type || 'application/octet-stream',
    null,
    onProgress,
  );

  if (entry?.blob) {
    await Store.putBlob(entry.blob, file);
    await Store.putEntry(entry);
  }

  await run();
  return entry;
}

export async function remove(entry) {
  const response = await api(`/entries/${encodePath(entry.path)}`, { method: 'DELETE' });
  if (!response.ok && response.status !== 404) throw new Error(`delete ${response.status}`);

  await Store.deleteEntry(entry.path);

  if (entry.blob) {
    const references = await Store.countByBlob(entry.blob);
    if (references === 0) await Store.deleteBlob(entry.blob);
  }

  await run();
}

export async function ensureLocal(entry) {
  if (await Store.hasBlob(entry.blob)) return Store.getBlob(entry.blob);

  await downloadBlob(entry);
  return Store.getBlob(entry.blob);
}
