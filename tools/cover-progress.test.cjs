const { test } = require('node:test');
const assertion = require('node:assert/strict');
const { createController, percentage } = require('../StreamingServiceServer.API/wwwroot/js/cover-progress');
const Fixture = Object.freeze({ album: 'album-fixture', total: 4, downloaded: 1, percentage: 25, pageSize: 25, turns: 12 });
function deferred() { let resolve; let reject; const promise = new Promise((complete, fail) => { resolve = complete; reject = fail; }); return { promise, resolve, reject }; }
async function flush() { for (let turn = 0; turn < Fixture.turns; turn += 1) await Promise.resolve(); }
function environment() {
  const elements = new Map(); const requests = []; const timers = new Map(); let sequence = 0; let authorized = true;
  const selected = [];
  for (const name of ['summary','bar','caption','rows','filter','search','previous','next','page','message']) elements.set(name, {
    value: name === 'filter' ? 'All' : '', textContent: '', innerHTML: '', disabled: false, handlers: {},
    addEventListener(event, callback) { this.handlers[event] = callback; },
    replaceChildren() { this.innerHTML = ''; }, contains() { return true; },
  });
  const controller = createController({ document: { getElementById: name => elements.get(name.replace('cover-progress-', '')) },
    request(address, options) { const result = deferred(); requests.push({ address, options, result }); return result.promise; },
    isAuthorized: () => authorized, escapeHtml: value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;'),
    onAlbumSelected: identifier => selected.push(identifier),
    schedule(callback) { timers.set(++sequence, callback); return sequence; }, cancelScheduled(identifier) { timers.delete(identifier); } });
  const page = overrides => ({ summary: { totalAlbums: Fixture.total, downloadedAlbums: Fixture.downloaded, percentage: Fixture.percentage },
    albums: [{ id: Fixture.album, title: 'Album', artist: 'Artist', percentage: 100, status: 'downloaded' }],
    page: 1, pageSize: Fixture.pageSize, filteredAlbums: Fixture.total, ...overrides });
  return { controller, elements, requests, timers, page, selected, setAuthorized(value) { authorized = value; },
    async complete(index = 0, data = page()) { requests[index].result.resolve({ ok: true, json: async () => data }); await flush(); } };
}
test('progress values reject invalid input and clamp out-of-range percentages', () => {
  assertion.equal(percentage(NaN), 0); assertion.equal(percentage(-10), 0); assertion.equal(percentage(110), 100); assertion.equal(percentage(33.3), 33.3);
});
test('queue cover panel renders catalog percentage, album completion and status', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush(); await scenario.complete();
  assertion.equal(scenario.elements.get('bar').value, 25); assertion.equal(scenario.elements.get('summary').textContent, '25%');
  assertion.equal(scenario.elements.get('caption').textContent, '1 of 4 albums have downloaded covers');
  assertion.match(scenario.elements.get('rows').innerHTML, /Downloaded/); assertion.match(scenario.elements.get('rows').innerHTML, /value="100"/);
  assertion.equal(scenario.elements.get('previous').disabled, true); assertion.equal(scenario.elements.get('next').disabled, true);
});
test('album titles and artists cannot inject markup into the progress table', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush();
  await scenario.complete(0, scenario.page({ albums: [{ id: Fixture.album, title: '<img onerror="attack">', artist: '<script>attack</script>', percentage: 0, status: '<script>' }] }));
  const html = scenario.elements.get('rows').innerHTML; assertion.doesNotMatch(html, /<img|<script>/); assertion.match(html, /&lt;img/);
});
test('an empty library shows zero progress and a clear empty message', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush();
  await scenario.complete(0, scenario.page({ summary: { totalAlbums: 0, downloadedAlbums: 0, percentage: 0 }, albums: [], filteredAlbums: 0 }));
  assertion.equal(scenario.elements.get('bar').value, 0); assertion.equal(scenario.elements.get('message').textContent, 'No albums in the library yet.');
});
test('changing filters cancels the older request and ignores its late response', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush();
  scenario.elements.get('filter').value = 'Missing'; scenario.elements.get('filter').handlers.change(); await flush();
  assertion.equal(scenario.requests[0].options.signal.aborted, true); assertion.match(scenario.requests[1].address, /filter=Missing/);
  await scenario.complete(1); await scenario.complete(0, scenario.page({ summary: { percentage: 99, totalAlbums: 100, downloadedAlbums: 99 } }));
  assertion.equal(scenario.elements.get('bar').value, 25);
});
test('polling does not overlap an outstanding cover request', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush();
  scenario.controller.refresh(); scenario.controller.refresh(); await flush(); assertion.equal(scenario.requests.length, 1);
  await scenario.complete(); scenario.controller.refresh(); await flush(); assertion.equal(scenario.requests.length, 2);
});
test('leaving queue cancels the request and suppresses its late render', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush(); scenario.controller.setActive(false);
  assertion.equal(scenario.requests[0].options.signal.aborted, true); await scenario.complete(); assertion.equal(scenario.elements.get('caption').textContent, '');
});
test('logout clears cover data and login requests fresh status immediately', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush(); await scenario.complete();
  scenario.setAuthorized(false); scenario.controller.setActive(true); assertion.equal(scenario.elements.get('rows').innerHTML, '');
  assertion.match(scenario.elements.get('message').textContent, /administrator/);
  scenario.setAuthorized(true); scenario.controller.setActive(true); await flush(); assertion.equal(scenario.requests.length, 2);
});
test('unauthorized users never issue cover queries', async () => {
  const scenario = environment(); scenario.setAuthorized(false); scenario.controller.setActive(true); await scenario.controller.refresh();
  assertion.equal(scenario.requests.length, 0);
});
test('search is debounced and encoded while resetting pagination', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush(); await scenario.complete();
  scenario.elements.get('search').value = 'first'; scenario.elements.get('search').handlers.input();
  scenario.elements.get('search').value = 'Miles & Coltrane'; scenario.elements.get('search').handlers.input();
  assertion.equal(scenario.timers.size, 1); [...scenario.timers.values()][0](); await flush();
  assertion.match(scenario.requests[1].address, /search=Miles\+%26\+Coltrane/); assertion.match(scenario.requests[1].address, /page=1/);
});
test('pagination requests the next page without changing the catalog summary', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush(); await scenario.complete(0, scenario.page({ filteredAlbums: 40 }));
  scenario.elements.get('next').handlers.click(); await flush(); assertion.match(scenario.requests[1].address, /page=2/);
});
test('refresh failure keeps the last percentage and offers automatic retry', async () => {
  const scenario = environment(); scenario.controller.setActive(true); await flush(); await scenario.complete();
  scenario.controller.refresh(); await flush(); scenario.requests[1].result.reject(new Error('offline')); await flush();
  assertion.equal(scenario.elements.get('bar').value, 25); assertion.match(scenario.elements.get('message').textContent, /last result/);
});
