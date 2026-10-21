// Local replica storage: IndexedDB for the manifest, OPFS for immutable blobs.
// The blob layout mirrors the server (sha256/<first two hex>/<hex>) so the
// client can deduplicate and verify against the manifest's content hash.

const DB_NAME = 'wharf';
const DB_VERSION = 1;
const BLOB_DIR = 'wharf-blobs';

let dbPromise = null;
let blobRootPromise = null;

function openDb() {
  if (dbPromise) return dbPromise;

  dbPromise = new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);

    request.onupgradeneeded = () => {
      const db = request.result;

      if (!db.objectStoreNames.contains('entries')) {
        const store = db.createObjectStore('entries', { keyPath: 'path' });
        store.createIndex('by_blob', 'blob', { unique: false });
      }

      if (!db.objectStoreNames.contains('meta')) {
        db.createObjectStore('meta', { keyPath: 'key' });
      }
    };

    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });

  return dbPromise;
}

function requestResult(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function transactionDone(transaction) {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}

async function getAllEntries() {
  const db = await openDb();
  const store = db.transaction('entries').objectStore('entries');
  return requestResult(store.getAll());
}

async function getEntry(path) {
  const db = await openDb();
  const store = db.transaction('entries').objectStore('entries');
  return (await requestResult(store.get(path))) ?? null;
}

async function getLiveEntries() {
  const entries = await getAllEntries();
  return entries.filter((entry) => !entry.deleted && entry.type === 'file');
}

async function putEntry(entry) {
  const db = await openDb();
  const transaction = db.transaction('entries', 'readwrite');
  transaction.objectStore('entries').put(entry);
  await transactionDone(transaction);
}

async function deleteEntry(path) {
  const db = await openDb();
  const transaction = db.transaction('entries', 'readwrite');
  transaction.objectStore('entries').delete(path);
  await transactionDone(transaction);
}

async function clearEntries() {
  const db = await openDb();
  const transaction = db.transaction('entries', 'readwrite');
  transaction.objectStore('entries').clear();
  await transactionDone(transaction);
}

/** Atomically replace the whole manifest; a crash cannot leave it partial. */
async function replaceEntries(entries) {
  const db = await openDb();
  const transaction = db.transaction('entries', 'readwrite');
  const store = transaction.objectStore('entries');

  store.clear();
  for (const entry of entries) store.put(entry);

  await transactionDone(transaction);
}

async function countByBlob(blob) {
  if (!blob) return 0;

  const db = await openDb();
  const store = db.transaction('entries').objectStore('entries');
  return requestResult(store.index('by_blob').count(IDBKeyRange.only(blob)));
}

async function getMeta(key) {
  const db = await openDb();
  const store = db.transaction('meta').objectStore('meta');
  const row = await requestResult(store.get(key));
  return row?.value ?? null;
}

async function setMeta(key, value) {
  const db = await openDb();
  const transaction = db.transaction('meta', 'readwrite');
  transaction.objectStore('meta').put({ key, value });
  await transactionDone(transaction);
}

async function blobRoot() {
  if (!navigator.storage?.getDirectory) {
    throw new Error('OPFS is not available in this browser');
  }

  blobRootPromise ??= navigator.storage
    .getDirectory()
    .then((root) => root.getDirectoryHandle(BLOB_DIR, { create: true }));

  return blobRootPromise;
}

async function blobShard(hex, create) {
  const root = await blobRoot();
  const sha = await root.getDirectoryHandle('sha256', { create });
  return sha.getDirectoryHandle(hex.slice(0, 2), { create });
}

async function hasBlob(hex) {
  if (!hex) return false;

  try {
    const shard = await blobShard(hex, false);
    await shard.getFileHandle(hex, { create: false });
    return true;
  } catch {
    return false;
  }
}

async function getBlob(hex) {
  const shard = await blobShard(hex, false);
  const handle = await shard.getFileHandle(hex, { create: false });
  return handle.getFile();
}

async function putBlob(hex, source) {
  const shard = await blobShard(hex, true);
  const handle = await shard.getFileHandle(hex, { create: true });
  const writable = await handle.createWritable();

  if (source && typeof source.pipeTo === 'function') {
    await source.pipeTo(writable);
  } else {
    await writable.write(source);
    await writable.close();
  }
}

async function deleteBlob(hex) {
  try {
    const shard = await blobShard(hex, false);
    await shard.removeEntry(hex);
  } catch {
    // Already gone.
  }
}

async function gcBlobs(referenced) {
  let removed = 0;

  try {
    const root = await blobRoot();
    const sha = await root.getDirectoryHandle('sha256', { create: false });

    for await (const [, shard] of sha.entries()) {
      if (shard.kind !== 'directory') continue;

      for await (const [name, handle] of shard.entries()) {
        if (handle.kind !== 'file') continue;
        if (referenced.has(name)) continue;

        try {
          await shard.removeEntry(name);
          removed += 1;
        } catch {
          // In use; try again next sweep.
        }
      }
    }
  } catch {
    // OPFS iteration is unavailable; skip rather than delete blindly.
  }

  return removed;
}

async function requestPersistence() {
  try {
    if (navigator.storage?.persist) return await navigator.storage.persist();
  } catch {
    // Ignore.
  }
  return false;
}

async function estimate() {
  try {
    if (navigator.storage?.estimate) return await navigator.storage.estimate();
  } catch {
    // Ignore.
  }
  return { usage: 0, quota: 0 };
}

export const Store = {
  open: openDb,
  getAllEntries,
  getEntry,
  getLiveEntries,
  putEntry,
  deleteEntry,
  clearEntries,
  replaceEntries,
  countByBlob,
  getMeta,
  setMeta,
  hasBlob,
  getBlob,
  putBlob,
  deleteBlob,
  gcBlobs,
  requestPersistence,
  estimate,
};
