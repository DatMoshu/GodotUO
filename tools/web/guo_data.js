// GUO on the web: the client-data layer (ADR-0008, amendment 1).
//
// The UO install can never be part of the page (CLAUDE.md rule 8) and is too
// big for the wasm heap, so it is mounted into Emscripten's filesystem at
// /uo as lazy, read-only files: a file's size is known up front, and its
// bytes are fetched on first read, in 1 MiB chunks, through a "byte source".
// The client above it is unchanged; it opens /uo/<file> with File.Open like
// on the desktop (MMFileReader reads through the stream in a browser).
//
// A byte source is { name, list() -> {files:[{path,size}], ...}, read(path,
// offset, length) -> Uint8Array }, both synchronous because the client's
// reads are. Today there is one: `httpSource`, the player's own install
// served from their own PC by `tools\web\run.py serve` (HTTP Range). The
// other two sources ADR-0008 names plug in here: a folder the player picks
// (File System Access / OPFS, read in a worker and handed over through a
// SharedArrayBuffer, since a File cannot be read synchronously on the main
// thread), and a shard that hosts its own client files (httpSource again,
// pointed at the shard's URL).
//
// tools\web\run.py export copies this file next to GUO.html, loads it from
// the page's <head>, and patches the engine's JS to expose Emscripten's FS as
// Module.guoFS and to call window.guoBeforeMain(Module, args) right before
// main(). URL parameters (all optional):
//   ?data=uo/        where the install is served ('none' mounts nothing)
//   &host=ws://h     the shard's WebSocket bridge (default: the page's host)
//   &port=2594       its port (default: what the server says, else 2594)
//   &arg=--foo       any extra client argument, repeatable
(function () {
	'use strict';

	const CHUNK = 1 << 20;          // 1 MiB per range request
	const CACHE_CHUNKS = 384;       // up to 384 MiB of chunks kept in JS memory
	const params = new URLSearchParams(window.location.search);

	function log(msg) {
		console.log('[GUO-web] ' + msg);
	}

	// A synchronous binary GET. Browsers refuse responseType 'arraybuffer' on
	// a synchronous request from the main thread, so it goes the old way:
	// the body as a "user-defined" charset string, one char per byte.
	function syncGet(url, range) {
		const xhr = new XMLHttpRequest();
		xhr.open('GET', url, false);
		xhr.overrideMimeType('text/plain; charset=x-user-defined');
		if (range) {
			xhr.setRequestHeader('Range', 'bytes=' + range[0] + '-' + range[1]);
		}
		xhr.send(null);
		if (xhr.status !== 200 && xhr.status !== 206) {
			throw new Error('GET ' + url + ' -> ' + xhr.status);
		}
		return xhr.responseText;
	}

	function httpSource(base) {
		if (!base.endsWith('/')) {
			base += '/';
		}
		return {
			name: base,
			list() {
				return JSON.parse(syncGet(base + '_index.json'));
			},
			read(path, offset, length) {
				const text = syncGet(base + encodeURI(path), [offset, offset + length - 1]);
				const out = new Uint8Array(text.length);
				for (let i = 0; i < text.length; i++) {
					out[i] = text.charCodeAt(i) & 0xff;
				}
				return out;
			},
		};
	}

	// Chunks of every file, least recently used first (a Map keeps insertion
	// order; a hit is re-inserted at the end).
	function chunkCache(source) {
		const cache = new Map();
		const stats = { requests: 0, bytes: 0, hits: 0 };
		function chunk(path, size, index) {
			const key = path + '#' + index;
			let data = cache.get(key);
			if (data) {
				cache.delete(key);
				cache.set(key, data);
				stats.hits++;
				return data;
			}
			const start = index * CHUNK;
			data = source.read(path, start, Math.min(CHUNK, size - start));
			stats.requests++;
			stats.bytes += data.length;
			cache.set(key, data);
			if (cache.size > CACHE_CHUNKS) {
				cache.delete(cache.keys().next().value);
			}
			return data;
		}
		return {
			stats,
			// Copy [position, position + length) of a file into heap[offset..].
			readInto(path, size, position, length, heap, offset) {
				let done = 0;
				while (done < length) {
					const pos = position + done;
					const index = Math.floor(pos / CHUNK);
					const data = chunk(path, size, index);
					const from = pos - index * CHUNK;
					const n = Math.min(length - done, data.length - from);
					heap.set(data.subarray(from, from + n), offset + done);
					done += n;
				}
				return done;
			},
		};
	}

	function mount(FS, root, source) {
		const index = source.list();
		const cache = chunkCache(source);
		let bytes = 0;
		FS.mkdirTree(root);
		for (const f of index.files) {
			const slash = f.path.lastIndexOf('/');
			const dir = slash >= 0 ? root + '/' + f.path.slice(0, slash) : root;
			const name = slash >= 0 ? f.path.slice(slash + 1) : f.path;
			FS.mkdirTree(dir);
			const node = FS.createFile(dir, name, {}, true, false);
			const size = f.size;
			// MEMFS reports a file's size from usedBytes; this node has no
			// contents in memory, only a size and a way to read.
			node.contents = null;
			Object.defineProperty(node, 'usedBytes', { get: () => size });
			const ops = Object.assign({}, node.stream_ops);
			ops.read = function (stream, buffer, offset, length, position) {
				if (position >= size) {
					return 0;
				}
				return cache.readInto(f.path, size, position, Math.min(length, size - position), buffer, offset);
			};
			node.stream_ops = ops;
			bytes += size;
		}
		return { index, cache, count: index.files.length, bytes };
	}

	window.guoWeb = { httpSource, mount };

	// --- the player's own folder (ADR-0021: the web's first-run screen) -------
	//
	// A File can only be read asynchronously on the main thread, and the
	// client reads synchronously. So the picked files go to a worker
	// (guo_picker_worker.js) that reads them with FileReaderSync; a read is a
	// request in a SharedArrayBuffer and a spin until the worker answers
	// (Atomics.wait is not allowed on a page's main thread). The page is
	// cross-origin isolated already (the threaded engine needs it), which is
	// what makes SharedArrayBuffer available.

	const PICK_ROOT = '/uo_picked';

	// A read that takes this long has stalled, not slowed: fail it rather than hang the tab.
	const STALL_MS = 10000;

	function workerSource(entries) {
		const ctrl = new Int32Array(new SharedArrayBuffer(8 * 4));
		const data = new Uint8Array(new SharedArrayBuffer(CHUNK));
		const worker = new Worker('guo_picker_worker.js');
		const byPath = new Map(entries.map((e, i) => [e.path, i]));
		let reads = 0;
		const ready = new Promise((resolve, reject) => {
			worker.onmessage = (e) => (e.data.ready ? resolve(e.data.count) : null);
			worker.onerror = (e) => reject(new Error(e.message || 'the picker worker failed'));
		});
		worker.postMessage({ files: entries.map((e) => e.file), ctrl: ctrl.buffer, data: data.buffer });
		return {
			name: 'picked folder',
			ready,
			list() {
				return { files: entries.map((e) => ({ path: e.path, size: e.file.size })) };
			},
			read(path, offset, length) {
				const out = new Uint8Array(length);
				let done = 0;
				while (done < length) {
					ctrl[1] = byPath.get(path);
					const at = offset + done;
					ctrl[2] = at % 4294967296;
					ctrl[3] = Math.floor(at / 4294967296);
					ctrl[4] = Math.min(CHUNK, length - done);
					Atomics.store(ctrl, 0, 1);
					Atomics.notify(ctrl, 0);
					const deadline = performance.now() + STALL_MS;
					while (Atomics.load(ctrl, 0) === 1) {
						// spin: the worker answers in well under a millisecond per chunk
						if (performance.now() > deadline) {
							Atomics.store(ctrl, 0, 0);
							throw new Error('the picker worker did not answer reading ' + path + ' after '
								+ reads + ' reads; this browser cannot read a picked file while the page waits');
						}
					}
					reads++;
					if (Atomics.load(ctrl, 0) === 3 || ctrl[5] === 0) {
						Atomics.store(ctrl, 0, 0);
						throw new Error('reading ' + path + ' failed');
					}
					out.set(data.subarray(0, ctrl[5]), done);
					done += ctrl[5];
					Atomics.store(ctrl, 0, 0);
				}
				return out;
			},
		};
	}

	// The picked folder's files, relative to it. A folder <input> gives
	// "Folder/sub/file"; the directory picker gives handles walked here.
	async function entriesFromHandle(dir, prefix, out) {
		for await (const [name, handle] of dir.entries()) {
			if (handle.kind === 'file') {
				out.push({ path: prefix + name, file: await handle.getFile() });
			} else if (prefix.split('/').length < 3) {
				await entriesFromHandle(handle, prefix + name + '/', out);
			}
		}
		return out;
	}

	function entriesFromInput(files) {
		const out = [];
		for (const f of files) {
			const rel = f.webkitRelativePath || f.name;
			out.push({ path: rel.includes('/') ? rel.slice(rel.indexOf('/') + 1) : rel, file: f });
		}
		return out;
	}

	async function mountPicked(entries, label) {
		const web = window.guoWeb;
		web.pickState = 'reading';
		try {
			const source = workerSource(entries);
			await source.ready;
			const FS = web.FS;
			// A second pick replaces the first.
			try { FS.unmount(PICK_ROOT); } catch (e) { /* not a mount point */ }
			try { removeTree(FS, PICK_ROOT); } catch (e) { /* nothing there */ }
			const mounted = mount(FS, PICK_ROOT, source);
			web.picked = { label, count: mounted.count, bytes: mounted.bytes, cache: mounted.cache };
			web.pickCount = (web.pickCount || 0) + 1;
			web.pickState = 'ready';
			log('picked "' + label + '": ' + mounted.count + ' files, '
				+ (mounted.bytes / 1048576).toFixed(0) + ' MiB, at ' + PICK_ROOT + ' (read in place, never uploaded)');
		} catch (e) {
			web.pickState = 'error: ' + e.message;
			log('pick failed: ' + e.message);
		}
	}

	function removeTree(FS, path) {
		for (const name of FS.readdir(path)) {
			if (name === '.' || name === '..') { continue; }
			const p = path + '/' + name;
			if (FS.isDir(FS.stat(p).mode)) { removeTree(FS, p); } else { FS.unlink(p); }
		}
		FS.rmdir(path);
	}

	// The folder <input>: every browser has it; tests fill it directly.
	function pickerInput() {
		let input = document.getElementById('guo-picker');
		if (!input) {
			input = document.createElement('input');
			input.type = 'file';
			input.id = 'guo-picker';
			input.webkitdirectory = true;
			input.multiple = true;
			input.style.display = 'none';
			input.addEventListener('change', () => {
				if (input.files && input.files.length) {
					const first = input.files[0].webkitRelativePath || '';
					mountPicked(entriesFromInput(input.files), first.split('/')[0] || 'folder');
				} else {
					window.guoWeb.pickState = 'cancelled';
				}
			});
			document.body.appendChild(input);
		}
		return input;
	}

	/** Called by the client's first-run screen (Choose folder), inside the click's user activation. */
	function pickFolder() {
		const web = window.guoWeb;
		web.pickState = 'picking';
		if (window.showDirectoryPicker && params.get('picker') !== 'input') {
			window.showDirectoryPicker({ id: 'guo-uo-folder', mode: 'read' })
				.then(async (dir) => mountPicked(await entriesFromHandle(dir, '', []), dir.name))
				.catch((e) => { web.pickState = e && e.name === 'AbortError' ? 'cancelled' : 'error: ' + e; });
		} else {
			pickerInput().click();
		}
		return web.pickState;
	}

	window.guoWeb.pickFolder = pickFolder;
	window.guoWeb.pickRoot = PICK_ROOT;
	window.guoWeb.pickState = 'none';
	document.addEventListener('DOMContentLoaded', pickerInput);

	window.guoBeforeMain = function (Module, args) {
		const FS = Module['guoFS'];
		if (!FS) {
			throw new Error('guo_data.js: the engine does not expose FS (was GUO.js patched by tools/web/run.py export?)');
		}
		window.guoWeb.FS = FS;
		let shard = {};
		// The engine opens its pack by the path it is given, relative to the
		// working directory, and keeps reopening it for every resource. The
		// client changes directory to its home at startup (as on the
		// desktop), after which "GUO.pck" no longer resolves; make it absolute.
		const pack = args.indexOf('--main-pack');
		if (pack >= 0 && args[pack + 1] && !args[pack + 1].startsWith('/')) {
			args[pack + 1] = FS.cwd().replace(/\/$/, '') + '/' + args[pack + 1];
		}
		// The client reads Godot's user arguments, the ones after "--".
		if (!args.includes('--')) {
			args.push('--');
		}
		const data = params.get('data') || 'uo/';
		let mounted = null;
		const base = new URL(data === 'none' ? '.' : data, window.location.href).href;
		const t0 = performance.now();
		if (data !== 'none') {
			try {
				mounted = mount(FS, '/uo', httpSource(base));
			} catch (e) {
				// No install served: the client's own resolver finds no data
				// and shows the first-run screen, where the player picks theirs.
				log('no install served at ' + base + ' (' + e.message + '); the first-run screen will ask for a folder');
			}
		}
		if (mounted) {
			window.guoWeb.mounted = mounted;
			log('mounted ' + mounted.count + ' files, ' + (mounted.bytes / 1048576).toFixed(0)
				+ ' MiB, from ' + base + ' at /uo in ' + (performance.now() - t0).toFixed(0) + ' ms (read lazily)');
			args.push('--client-data', '/uo');
			if (mounted.index.client_version) {
				args.push('--client-version', mounted.index.client_version);
			}
			shard = mounted.index.shard || {};
		}
		const host = params.get('host') || shard.host || ('ws://' + window.location.hostname);
		const port = params.get('port') || String(shard.port || 2594);
		args.push('--host', host, '--port', port);
		for (const a of params.getAll('arg')) {
			args.push(a);
		}
		log('args: ' + args.join(' '));
	};
}());
