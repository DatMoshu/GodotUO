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

	window.guoBeforeMain = function (Module, args) {
		const FS = Module['guoFS'];
		if (!FS) {
			throw new Error('guo_data.js: the engine does not expose FS (was GUO.js patched by tools/web/run.py export?)');
		}
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
		if (data !== 'none') {
			const base = new URL(data, window.location.href).href;
			const t0 = performance.now();
			const mounted = mount(FS, '/uo', httpSource(base));
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
