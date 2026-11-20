const { test } = require('node:test');
const assertion = require('node:assert/strict');
const { createController } = require('../StreamingServiceServer.API/wwwroot/js/lyrics-queue');
const Fixture = Object.freeze({
  prefix: 'lyrics-queue-', filter: 'filter', all: 'All', skipped: 'Skipped', pending: 'Pending', downloaded: 'Downloaded',
  summary: 'summary', rows: 'rows', message: 'message', next: 'next', previous: 'previous', page: 'page',
  firstIndex: 0, secondIndex: 1, noCalls: 0, oneCall: 1, twoCalls: 2, flushTurns: 12,
  totalRecordings: 5, totalJobs: 4, downloadedCount: 2, skippedCount: 1, pageSize: 25, firstPage: 1,
  manyJobs: 40, expectedSummary: '2 downloaded · 1 skipped · 1 awaiting discovery',
  title: 'A test song', artist: 'A test artist', reason: 'naming_issue',
  expectedReason: 'Audio analysis: name or artist mismatch', maliciousTitle: '<img onerror="attack">',
  maliciousArtist: '<script>attack</script>', unsafeMarkup: /<img|<script>/, escapedMarkup: /&lt;img/,
  offline: 'offline', changeEvent: 'change', clickEvent: 'click', stateQuery: /state=Skipped/, secondPageQuery: /page=2/,
  status: 'Downloaded', authorizationMessage: /administrator/, refreshMessage: /retrying automatically/i,
  initialMessage: /library is scanned/,
});
function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((complete, fail) => { resolve = complete; reject = fail; });
  return { promise, resolve, reject };
}
async function flush() {
  for (let turn = Fixture.noCalls; turn < Fixture.flushTurns; turn += Fixture.oneCall) await Promise.resolve();
}
function environment() {
  const elements = new Map();
  const requests = [];
  let authorized = true;
  for (const name of [Fixture.summary, Fixture.rows, Fixture.filter, Fixture.previous, Fixture.next, Fixture.page, Fixture.message]) {
    elements.set(name, {
      value: name === Fixture.filter ? Fixture.all : '', textContent: '', innerHTML: '', disabled: false, handlers: {},
      addEventListener(event, callback) { this.handlers[event] = callback; },
      replaceChildren() { this.innerHTML = ''; },
    });
  }
  const controller = createController({
    document: { getElementById: name => elements.get(name.replace(Fixture.prefix, '')) },
    request(address, options) { const result = deferred(); requests.push({ address, options, result }); return result.promise; },
    isAuthorized: () => authorized,
    escapeHtml: value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;'),
  });
  const page = overrides => ({
    totalRecordings: Fixture.totalRecordings, totalJobs: Fixture.totalJobs, filteredJobs: Fixture.totalJobs,
    counts: [{ state: Fixture.downloaded, count: Fixture.downloadedCount }, { state: Fixture.skipped, count: Fixture.skippedCount }],
    jobs: [{ title: Fixture.title, artist: Fixture.artist, state: Fixture.skipped, lastFailure: Fixture.reason }],
    page: Fixture.firstPage, pageSize: Fixture.pageSize, ...overrides,
  });
  return {
    controller, elements, requests, page, setAuthorized(value) { authorized = value; },
    async complete(index = Fixture.firstIndex, data = page()) {
      requests[index].result.resolve({ ok: true, json: async () => data });
      await flush();
    },
  };
}
test('queue displays downloaded skipped and undiscovered counts with the audio-analysis exclusion reason', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  await scenario.complete();
  assertion.equal(scenario.elements.get(Fixture.summary).textContent, Fixture.expectedSummary);
  assertion.ok(scenario.elements.get(Fixture.rows).innerHTML.includes(Fixture.expectedReason));
});
test('track names and artists are escaped before rendering', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  await scenario.complete(Fixture.firstIndex, scenario.page({ jobs: [{
    title: Fixture.maliciousTitle, artist: Fixture.maliciousArtist, state: Fixture.downloaded, lastFailure: '',
  }] }));
  assertion.doesNotMatch(scenario.elements.get(Fixture.rows).innerHTML, Fixture.unsafeMarkup);
  assertion.match(scenario.elements.get(Fixture.rows).innerHTML, Fixture.escapedMarkup);
});
test('changing state cancels an older request and ignores its late response', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  scenario.elements.get(Fixture.filter).value = Fixture.skipped;
  scenario.elements.get(Fixture.filter).handlers[Fixture.changeEvent]();
  await flush();
  assertion.equal(scenario.requests[Fixture.firstIndex].options.signal.aborted, true);
  assertion.match(scenario.requests[Fixture.secondIndex].address, Fixture.stateQuery);
  await scenario.complete(Fixture.secondIndex);
  await scenario.complete(Fixture.firstIndex, scenario.page({ jobs: [] }));
  assertion.ok(scenario.elements.get(Fixture.rows).innerHTML.includes(Fixture.title));
});
test('polling shares the pending request', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  scenario.controller.refresh();
  scenario.controller.refresh();
  await flush();
  assertion.equal(scenario.requests.length, Fixture.oneCall);
});
test('leaving the queue aborts the request and prevents late rendering', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  scenario.controller.setActive(false);
  await scenario.complete();
  assertion.equal(scenario.requests[Fixture.firstIndex].options.signal.aborted, true);
  assertion.equal(scenario.elements.get(Fixture.rows).innerHTML, '');
});
test('logout clears earlier account data and login fetches fresh queue data', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  await scenario.complete();
  scenario.setAuthorized(false);
  scenario.controller.setActive(true);
  assertion.equal(scenario.elements.get(Fixture.rows).innerHTML, '');
  assertion.match(scenario.elements.get(Fixture.message).textContent, Fixture.authorizationMessage);
  scenario.setAuthorized(true);
  scenario.controller.setActive(true);
  await flush();
  assertion.equal(scenario.requests.length, Fixture.twoCalls);
});
test('unauthorized users cannot issue queue queries', async () => {
  const scenario = environment();
  scenario.setAuthorized(false);
  scenario.controller.setActive(true);
  await scenario.controller.refresh();
  assertion.equal(scenario.requests.length, Fixture.noCalls);
});
test('paging loads the next page and disables previous on the first page', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  await scenario.complete(Fixture.firstIndex, scenario.page({ filteredJobs: Fixture.manyJobs }));
  assertion.equal(scenario.elements.get(Fixture.previous).disabled, true);
  scenario.elements.get(Fixture.next).handlers[Fixture.clickEvent]();
  await flush();
  assertion.match(scenario.requests[Fixture.secondIndex].address, Fixture.secondPageQuery);
});
test('refresh failure preserves the last visible data and reports automatic retry', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  await scenario.complete();
  scenario.controller.refresh();
  await flush();
  scenario.requests[Fixture.secondIndex].result.reject(new Error(Fixture.offline));
  await flush();
  assertion.equal(scenario.elements.get(Fixture.summary).textContent, Fixture.expectedSummary);
  assertion.match(scenario.elements.get(Fixture.message).textContent, Fixture.refreshMessage);
});
test('a library not yet discovered explains why its queue is empty', async () => {
  const scenario = environment();
  scenario.controller.setActive(true);
  await flush();
  await scenario.complete(Fixture.firstIndex, scenario.page({ totalJobs: Fixture.noCalls, counts: [], jobs: [] }));
  assertion.match(scenario.elements.get(Fixture.message).textContent, Fixture.initialMessage);
});
