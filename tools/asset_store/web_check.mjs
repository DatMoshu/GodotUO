// Headless contract check: Node built-ins only; deliberately not a layout engine.
import assert from 'node:assert/strict';
import vm from 'node:vm';

const base = new URL(process.argv[2]);
const response = await fetch(base);
assert.equal(response.status, 200, 'Store page must be served over HTTP');
const html = await response.text();
const scripts = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)].map(m => m[1]);
assert.ok(scripts.length, 'No inline catalogue script');
for (const source of scripts) {
  assert.doesNotMatch(source, /\b(?:innerHTML|outerHTML|insertAdjacentHTML|document\.write)\b/, 'Metadata must use textContent/text nodes, never HTML sinks');
  new vm.Script(source, { filename: 'store-index.html' });
}
const indexResponse = await fetch(new URL('index.json', base));
assert.equal(indexResponse.status, 200);
const index = await indexResponse.json();
// A signed (v2) index nests each manifest; the checks below read the v1 shape.
index.packs = index.packs.map(p => p.manifest ? { ...p.manifest, sha256: p.sha256, size: p.size, url: p.urls[0], preview_url: p.preview_url } : p);
assert.ok(index.packs.length >= 5, 'Fixture must exercise every pack kind');

class Element {
  constructor(tag) {
    this.tagName = tag.toLowerCase(); this.children = []; this.attrs = {};
    this.dataset = {}; this.listeners = new Map(); this.hidden = false; this.open = false;
    this.classList = { add() {}, remove() {} };
  }
  get textContent() { return this.tagName === '#text' ? this.value : this.children.map(c => c.textContent).join(''); }
  set textContent(value) { this.children = [text(String(value))]; }
  append(...nodes) { this.children.push(...nodes.map(n => typeof n === 'string' ? text(n) : n)); }
  replaceChildren(...nodes) { this.children = []; this.append(...nodes); }
  setAttribute(key, value) {
    assert.ok(!key.toLowerCase().startsWith('on'), 'Inline event-handler attributes are forbidden');
    this.attrs[key] = String(value);
  }
  getAttribute(key) { return this.attrs[key]; }
  addEventListener(type, callback) {
    if (!this.listeners.has(type)) this.listeners.set(type, []);
    this.listeners.get(type).push(callback);
  }
  dispatch(type) { for (const callback of this.listeners.get(type) || []) callback({ target: this, currentTarget: this }); }
  showModal() { this.open = true; }
  close() { this.open = false; }
  set innerHTML(_) { throw Error('Unsafe HTML assignment'); }
  set outerHTML(_) { throw Error('Unsafe HTML assignment'); }
  insertAdjacentHTML() { throw Error('Unsafe HTML insertion'); }
}
function text(value) { const node = new Element('#text'); node.value = value; return node; }
function descendants(node, tag) { return node.children.flatMap(c => [...(c.tagName === tag ? [c] : []), ...descendants(c, tag)]); }

async function boot(mode = 'normal') {
  const ids = new Map();
  for (const match of html.matchAll(/<([a-z][\w-]*)\b[^>]*\bid=["']([^"']+)["'][^>]*>/gi)) {
    assert.ok(!ids.has(match[2]), `Duplicate HTML id: ${match[2]}`);
    ids.set(match[2], new Element(match[1]));
  }
  const errors = [], timers = [];
  const document = {
    createElement: tag => new Element(tag), createTextNode: text,
    querySelector(selector) { assert.match(selector, /^#[\w-]+$/); assert.ok(ids.has(selector.slice(1)), `Missing page node ${selector}`); return ids.get(selector.slice(1)); },
    querySelectorAll(selector) {
      const match = /^(#[\w-]+) ([a-z]+)$/.exec(selector);
      assert.ok(match, `Unsupported harness selector: ${selector}`);
      return descendants(this.querySelector(match[1]), match[2]);
    },
    write() { throw Error('Unsafe document.write'); },
  };
  const context = vm.createContext({
    document, URL, location: { href: base.href, origin: base.origin },
    console: { log() {}, warn: (...args) => errors.push(args.join(' ')), error: (...args) => errors.push(args.join(' ')) },
    matchMedia: () => ({ matches: true }),
    setTimeout: callback => { timers.push(callback); return timers.length; },
    fetch: async path => {
      const url = new URL(path, base);
      assert.equal(url.origin, base.origin, 'Unexpected external request');
      if (mode === 'unavailable') return { ok: false, status: 503 };
      if (mode === 'empty') return { ok: true, json: async () => ({ schema: 'guo/store-index@1', packs: [] }) };
      if (mode === 'unsupported') return { ok: true, json: async () => ({ schema: 'wrong', packs: [] }) };
      return fetch(url);
    },
  });
  for (const source of scripts) await new vm.Script(source, { filename: 'store-index.html' }).runInContext(context, { timeout: 2000 });
  for (const callback of timers) callback();
  assert.deepEqual(errors, [], 'Page emitted console warnings/errors');
  return ids;
}

const page = await boot();
const allCards = () => descendants(page.get('shelves'), 'article');
assert.equal(allCards().length, index.packs.length, 'Every fixture pack must render');
assert.equal(page.get('sample-note').hidden, false, 'Samples must be labelled');
assert.equal(descendants(page.get('tabs'), 'button').length, 7, 'All plus six populated kind filters');
const featured = index.packs.find(p => p.id === 'moongate-shimmer');
assert.equal(page.get('feature-title').textContent, featured.title, 'Metadata must remain literal text');
assert.equal(page.get('feature-img').src, new URL(featured.preview_url, base).href);
assert.equal(page.get('feature-download').href, new URL(featured.url, base).href);
page.get('feature-open').dispatch('click');
assert.equal(page.get('detail').open, true);
assert.equal(page.get('detail-title').textContent, featured.title);
assert.ok(page.get('detail-meta').textContent.includes(featured.author), 'Creator text lost');
assert.equal(descendants(page.get('detail-title'), 'img').length, 0, 'HTML-like title became markup');
page.get('detail-close').dispatch('click');
assert.equal(page.get('detail').open, false);
descendants(page.get('tabs'), 'button').find(b => b.textContent.startsWith('Themes')).dispatch('click');
assert.equal(allCards().length, 1, 'Theme filter failed');
assert.ok(allCards()[0].textContent.includes('Sample'), 'Sample card badge missing');
descendants(page.get('tabs'), 'button').find(b => b.textContent.startsWith('Razor scripts')).dispatch('click');
assert.equal(allCards().length, 1, 'Razor script filter failed');
descendants(allCards()[0], 'button')[0].dispatch('click');
assert.ok(page.get('detail-note').textContent.includes('personal copies'), 'Script install guidance missing');
assert.ok(page.get('detail-note').textContent.includes('not Razor Enhanced'), 'Script dialect boundary missing');
page.get('detail-close').dispatch('click');
page.get('search').value = 'no-such-pack'; page.get('search').dispatch('input');
assert.equal(allCards().length, 0); assert.equal(page.get('empty').hidden, false);
page.get('search').value = ''; page.get('search').dispatch('input');
descendants(page.get('tabs'), 'button').find(b => b.textContent.startsWith('All')).dispatch('click');
assert.equal(allCards().length, index.packs.length, 'Reset filters failed');

assert.equal((await boot('empty')).get('feature-title').textContent, 'No packs yet');
for (const mode of ['unavailable', 'unsupported']) {
  assert.equal((await boot(mode)).get('feature-title').textContent, 'The store could not be read');
}
console.log('PASS: JS parse, HTTP index, all shelves, literal metadata, sample labels, detail dialog, filters, empty and error states; no console errors');
