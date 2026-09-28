// GUO on the web: the worker behind "pick your UO folder" (ADR-0021, ADR-0008).
//
// The page's client reads files synchronously, but a File the player picked
// can only be read asynchronously on the main thread. A worker can read one
// synchronously (FileReaderSync), so this worker holds the picked files and
// answers reads through a SharedArrayBuffer:
//
//   ctrl  Int32Array(8): [0] state (0 idle, 1 request, 2 done, 3 error),
//         [1] file index, [2] offset low, [3] offset high, [4] length, [5] bytes read
//   data  Uint8Array(chunk): the bytes read
//
// The main thread writes a request, sets state 1 and spins until the state
// changes (Atomics.wait is not allowed on a page's main thread); this worker
// sleeps in Atomics.wait until a request arrives.
'use strict';

let files = [];
let ctrl = null;
let data = null;

self.onmessage = (e) => {
	files = e.data.files;
	ctrl = new Int32Array(e.data.ctrl);
	data = new Uint8Array(e.data.data);
	self.postMessage({ ready: true, count: files.length });
	serve();
};

function serve() {
	const reader = new FileReaderSync();
	for (;;) {
		Atomics.wait(ctrl, 0, 0);
		if (Atomics.load(ctrl, 0) !== 1) {
			continue;
		}
		try {
			const file = files[ctrl[1]];
			const offset = (ctrl[3] >>> 0) * 4294967296 + (ctrl[2] >>> 0);
			const length = Math.min(ctrl[4], data.length, Math.max(0, file.size - offset));
			const bytes = new Uint8Array(reader.readAsArrayBuffer(file.slice(offset, offset + length)));
			data.set(bytes, 0);
			ctrl[5] = bytes.length;
			Atomics.store(ctrl, 0, 2);
		} catch (err) {
			ctrl[5] = 0;
			Atomics.store(ctrl, 0, 3);
		}
		Atomics.notify(ctrl, 0);
	}
}
