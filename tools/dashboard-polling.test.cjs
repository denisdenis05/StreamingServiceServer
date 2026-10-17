const test = require('node:test');
const assert = require('node:assert/strict');
const { createController } = require('../StreamingServiceServer.API/wwwroot/js/dashboard-polling.js');

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((complete, fail) => { resolve = complete; reject = fail; });
  return { promise, resolve, reject };
}
async function settle() {
  for (let turn = 0; turn < 12; turn += 1) await Promise.resolve();
}
function fixture() {
  const timers = new Map();
  const calls = [];
  const publications = [];
  const failures = [];
  let identifier = 0;
  const resources = ['provider', 'covers'].map(name => ({ name, views: ['queue'], load(signal) {
    const completion = deferred();
    calls.push({ name, signal, ...completion });
    return completion.promise;
  }, publish(result) { publications.push({ name, result }); } }));
  const controller = createController({ resources, schedule(callback) { timers.set(++identifier, callback); return identifier; },
    cancelScheduled(timer) { timers.delete(timer); }, onError(name, failure) { failures.push({ name, failure }); } });
  const context = { view: 'queue', visible: true, authorized: true, identity: 'first-account' };
  function tick() { const pending = [...timers.values()]; timers.clear(); pending.forEach(callback => callback()); }
  return { controller, context, calls, timers, publications, failures, tick };
}

test('blocked provider does not delay cover publication or its next refresh', async () => {
  const value = fixture(); value.controller.setContext(value.context); await settle();
  value.calls.find(call => call.name === 'covers').resolve('ready'); await settle();
  assert.deepEqual(value.publications, [{ name: 'covers', result: 'ready' }]);
  value.tick(); await settle();
  assert.deepEqual(value.calls.map(call => call.name), ['provider', 'covers', 'covers']);
});

test('manual refresh coalesces every resource already in flight', async () => {
  const value = fixture(); value.controller.setContext(value.context); await settle();
  void value.controller.refresh(); void value.controller.refresh('provider'); await settle();
  assert.equal(value.calls.length, 2);
});

for (const [reason, changes] of Object.entries({ hidden: { visible: false }, offview: { view: 'settings' }, logout: { authorized: false } })) {
  test(reason + ' aborts active requests and ignores late results', async () => {
    const value = fixture(); value.controller.setContext(value.context); await settle();
    value.controller.setContext({ ...value.context, ...changes });
    assert.ok(value.calls.every(call => call.signal.aborted));
    value.calls.forEach(call => call.resolve('stale')); await settle();
    assert.deepEqual(value.publications, []); assert.equal(value.timers.size, 0);
    await value.controller.refresh(); assert.equal(value.calls.length, 2);
  });
  test('return after ' + reason + ' refreshes immediately', async () => {
    const value = fixture(); value.controller.setContext(value.context); await settle();
    value.controller.setContext({ ...value.context, ...changes });
    value.controller.setContext(value.context); await settle();
    assert.equal(value.calls.length, 4); assert.ok(!value.calls[2].signal.aborted);
  });
}

test('repeated unchanged context does not restart requests', async () => {
  const value = fixture(); value.controller.setContext(value.context); await settle();
  value.controller.setContext({ ...value.context }); await settle(); assert.equal(value.calls.length, 2);
});

test('account switch prevents old results from overwriting the new account', async () => {
  const value = fixture(); value.controller.setContext(value.context); await settle();
  value.controller.setContext({ ...value.context, identity: 'second-account' }); await settle();
  value.calls[2].resolve('new'); value.calls[0].resolve('old'); await settle();
  assert.deepEqual(value.publications, [{ name: 'provider', result: 'new' }]);
});

test('a failed resource retries independently and retains other resource work', async () => {
  const value = fixture(); value.controller.setContext(value.context); await settle();
  value.calls[0].reject(new Error('offline')); await settle();
  assert.equal(value.failures.length, 1); value.tick(); await settle();
  assert.deepEqual(value.calls.map(call => call.name), ['provider', 'covers', 'provider']);
});

test('dispose cancels scheduled polls and aborts active resources', async () => {
  const value = fixture(); value.controller.setContext(value.context); await settle();
  value.calls[0].resolve('ready'); await settle(); assert.equal(value.timers.size, 1);
  value.controller.dispose(); assert.equal(value.timers.size, 0); assert.ok(value.calls[1].signal.aborted);
  value.calls[1].resolve('late'); await settle(); assert.equal(value.publications.length, 1);
});

const filesystem = require('node:fs');
const virtualMachine = require('node:vm');
function dashboardFixture(view = '#queue') {
  const requests = [];
  const coverCalls = [];
  const activity = [];
  const browserDocument = { visibilityState: 'visible', addEventListener() {} };
  const sandbox = { document: browserDocument, window: { location: { hash: view } },
    localStorage: { getItem() { return ''; } }, URLSearchParams, AbortController, DOMException,
    console: { error() {} }, fetch(url, options) {
      const completion = deferred(); requests.push({ url, options, ...completion }); return completion.promise;
    }, DashboardPolling: { createController(options) {
      return createController({ ...options, schedule() { return 1; }, cancelScheduled() {} });
    } } };
  virtualMachine.createContext(sandbox);
  virtualMachine.runInContext(filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/js/dashboard.js'), 'utf8'), sandbox);
  sandbox.coverCalls = coverCalls; sandbox.activity = activity;
  virtualMachine.runInContext(`
    state.authToken = 'administrator'; state.userRole = 'Admin';
    coverDownloadProgress = { setActive(value) { activity.push(value); }, refresh() { coverCalls.push('refresh'); return Promise.resolve(); } };
    renderStats = renderQueuedCarousel = renderQueueTable = renderDownloadedCarousel = renderDownloadedGrid = renderFailedTable = () => {};
    setupDashboardPolling(); synchronizeDashboardPolling();
  `, sandbox);
  return { sandbox, requests, coverCalls, activity, run(expression) { return virtualMachine.runInContext(expression, sandbox); } };
}

test('queue wiring refreshes covers without waiting for the pending audio queue and never polls overview stats', async () => {
  const value = dashboardFixture(); await settle();
  assert.deepEqual(value.requests.map(request => request.url), ['/api/dashboard/queued']);
  assert.deepEqual(value.coverCalls, ['refresh']);
});

test('overview wiring starts stats, queue and downloaded resources independently', async () => {
  const value = dashboardFixture('#overview'); await settle();
  assert.deepEqual(value.requests.map(request => request.url), ['/api/dashboard/stats', '/api/dashboard/queued', '/api/dashboard/downloaded?search=']);
  assert.equal(value.coverCalls.length, 0);
  value.requests[1].resolve({ ok: true, status: 200, json: async () => ['album'] }); await settle();
  assert.equal(value.run('state.queued[0]'), 'album');
});

test('dashboard visibility changes deactivate covers and abort its view resources', async () => {
  const value = dashboardFixture(); await settle();
  value.sandbox.document.visibilityState = 'hidden'; value.run('synchronizeDashboardPolling()');
  assert.equal(value.activity.at(-1), false); assert.ok(value.requests[0].options.signal.aborted);
  value.sandbox.document.visibilityState = 'visible'; value.run('synchronizeDashboardPolling()'); await settle();
  assert.equal(value.coverCalls.length, 2); assert.equal(value.requests.length, 2);
});

test('logout wiring aborts existing requests and prevents late state publication', async () => {
  const value = dashboardFixture(); await settle();
  value.run("state.authToken = ''; synchronizeDashboardPolling()");
  assert.ok(value.requests[0].options.signal.aborted);
  value.requests[0].resolve({ ok: true, status: 200, json: async () => ['stale'] }); await settle();
  assert.equal(value.run('state.queued.length'), 0);
});

test('download search supersedes prior requests and URL encodes the replacement', async () => {
  const value = dashboardFixture('#downloaded'); await settle();
  value.run("void fetchDownloaded('A & B')"); await settle();
  assert.ok(value.requests[0].options.signal.aborted);
  assert.equal(value.requests[1].url, '/api/dashboard/downloaded?search=A%20%26%20B');
  value.requests[1].resolve({ ok: true, status: 200, json: async () => ['current'] });
  value.requests[0].resolve({ ok: true, status: 200, json: async () => ['stale'] }); await settle();
  assert.equal(value.run('state.downloaded[0]'), 'current');
});
