const test = require('node:test');
const assert = require('node:assert/strict');
const filesystem = require('node:fs');
const { createController } = require('../StreamingServiceServer.API/wwwroot/js/audio-analysis-settings.js');
const Policy = Object.freeze({ revision: 'test-revision', endpoint: '/api/dashboard/settings/audio-analysis', empty: '', conflict: 409,
  controls: ['enabled', 'endpoint', 'token', 'clear-token', 'token-status', 'save', 'refresh', 'test', 'status',
    'pending', 'processing', 'completed', 'unavailable', 'failed'] });
function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((complete, fail) => { resolve = complete; reject = fail; });
  return { promise, resolve, reject };
}
function response(settings, status = 200) { return { ok: status < 400, status, json: async () => settings }; }
function settings(changes = {}) {
  return { enabled: false, endpointUrl: 'http://localhost:8000', profile: 'musicnn-v1', revision: Policy.revision,
    apiTokenConfigured: false, counts: { pending: 10, processing: 1, completed: 20, unavailable: 2, failed: 3 }, ...changes };
}
function fixture(authorized = true) {
  const elements = Object.fromEntries(Policy.controls.map(name => [name, { checked: false, value: Policy.empty,
    disabled: false, textContent: Policy.empty, addEventListener(event, callback) { this.callback = callback; } }]));
  const requests = [];
  const state = { authorized };
  const controller = createController({ document: { getElementById(identifier) { return elements[identifier.replace('audio-analysis-', Policy.empty)]; } },
    endpointBase: '/api/dashboard', isAuthorized: () => state.authorized, request(url, options) {
      const completion = deferred();
      requests.push({ url, options, ...completion });
      return completion.promise;
    } });
  return { elements, requests, state, controller, async load(result = settings()) {
    const pending = controller.load();
    requests.at(-1).resolve(response(result));
    await pending;
  } };
}
test('processing defaults off and saving stays disabled until settings load', () => {
  const value = fixture();
  assert.equal(value.elements.enabled.checked, false);
  assert.equal(value.elements.enabled.disabled, true);
  assert.equal(value.elements.save.disabled, true);
});
test('non-admin cannot load, save or test local analysis settings', async () => {
  const value = fixture(false);
  await value.controller.load(); await value.controller.save(); await value.controller.test();
  assert.equal(value.requests.length, 0);
  assert.equal(value.elements.refresh.disabled, true);
  assert.match(value.elements.status.textContent, /admin/);
});
test('loading publishes all counters and configured-token presence without reading a secret', async () => {
  const value = fixture();
  await value.load(settings({ apiTokenConfigured: true, apiToken: 'must-never-be-displayed' }));
  assert.equal(value.requests[0].url, Policy.endpoint);
  assert.equal(value.elements.token.value, Policy.empty);
  assert.match(value.elements['token-status'].textContent, /token is saved/);
  for (const name of Object.keys(settings().counts)) assert.equal(value.elements[name].textContent, String(settings().counts[name]));
  assert.equal(value.elements.save.disabled, false);
});
test('save submits revision and token once then clears it after confirmed success', async () => {
  const value = fixture(); await value.load();
  value.elements.enabled.checked = true; value.elements.token.value = 'new-secret';
  const pending = value.controller.save();
  assert.equal(value.elements.save.disabled, true);
  assert.equal(value.elements.refresh.disabled, true);
  await value.controller.save();
  assert.equal(value.requests.length, 2);
  assert.deepEqual(JSON.parse(value.requests[1].options.body), { enabled: true, endpointUrl: settings().endpointUrl,
    profile: 'musicnn-v1', expectedRevision: Policy.revision, apiToken: 'new-secret', clearApiToken: false });
  value.requests[1].resolve(response(settings({ enabled: true, apiTokenConfigured: true })));
  await pending;
  assert.equal(value.elements.token.value, Policy.empty);
  assert.equal(value.elements.save.disabled, false);
});
test('blank token keeps the saved token and explicit removal has a separate flag', async () => {
  const value = fixture(); await value.load();
  value.elements['clear-token'].checked = true;
  const pending = value.controller.save();
  const payload = JSON.parse(value.requests[1].options.body);
  assert.equal(payload.apiToken, null); assert.equal(payload.clearApiToken, true);
  value.requests[1].resolve(response(settings())); await pending;
  assert.equal(value.elements['clear-token'].checked, false);
});
test('conflict preserves draft and secret while blocking stale saves until explicit refresh', async () => {
  const value = fixture(); await value.load();
  value.elements.endpoint.value = 'http://localhost:9000'; value.elements.token.value = 'draft-secret';
  const pending = value.controller.save(); value.requests[1].resolve(response({}, Policy.conflict)); await pending;
  assert.equal(value.elements.endpoint.value, 'http://localhost:9000');
  assert.equal(value.elements.token.value, 'draft-secret');
  assert.equal(value.elements.save.disabled, true); assert.equal(value.elements.refresh.disabled, false);
  assert.match(value.elements.status.textContent, /changed elsewhere/);
  await value.controller.save(); assert.equal(value.requests.length, 2);
  await value.load(settings({ revision: 'replacement-revision' }));
  assert.equal(value.elements.token.value, Policy.empty); assert.equal(value.elements.save.disabled, false);
});
test('failed save keeps draft without displaying returned error bodies or secrets', async () => {
  const value = fixture(); await value.load(); value.elements.token.value = 'secret';
  const pending = value.controller.save(); value.requests[1].reject(new Error('private server details secret')); await pending;
  assert.equal(value.elements.token.value, 'secret');
  assert.match(value.elements.status.textContent, /Could not save/);
  assert.doesNotMatch(value.elements.status.textContent, /private|secret/);
  assert.equal(value.elements.save.disabled, false);
});
test('malformed response does not overwrite loaded settings or reveal server fields', async () => {
  const value = fixture(); await value.load(); value.elements.endpoint.value = 'http://localhost:9000';
  await value.load(settings({ counts: { ...settings().counts, failed: -1 }, endpointUrl: 'private-response-url' }));
  assert.equal(value.elements.endpoint.value, 'http://localhost:9000');
  assert.match(value.elements.status.textContent, /Could not load/);
});
test('connection test uses saved configuration and never submits draft URL or token', async () => {
  const value = fixture(); await value.load(); value.elements.token.value = 'unsaved-secret';
  const pending = value.controller.test();
  assert.equal(value.requests[1].url, Policy.endpoint + '/test');
  assert.equal(value.requests[1].options.method, 'POST');
  assert.equal(value.requests[1].options.body, undefined);
  value.requests[1].resolve(response({ ready: true })); await pending;
  assert.match(value.elements.status.textContent, /connection is ready/);
  assert.equal(value.elements.token.value, 'unsaved-secret');
});
test('connection failure and malformed success produce safe failure status', async () => {
  const value = fixture(); await value.load();
  const pending = value.controller.test(); value.requests[1].resolve(response({ ready: false })); await pending;
  assert.match(value.elements.status.textContent, /could not be verified/);
});
test('logout clears token and account state, aborts pending work and fences late success', async () => {
  const value = fixture(); await value.load(); value.elements.token.value = 'draft-secret';
  const pending = value.controller.save(); value.state.authorized = false; value.controller.authenticationChanged();
  assert.equal(value.requests[1].options.signal.aborted, true);
  value.requests[1].resolve(response(settings({ enabled: true, apiTokenConfigured: true }))); await pending;
  assert.equal(value.elements.token.value, Policy.empty); assert.equal(value.elements.endpoint.value, Policy.empty);
  assert.equal(value.elements.enabled.checked, false); assert.equal(value.elements.refresh.disabled, true);
  assert.match(value.elements.status.textContent, /admin/);
});
test('module and markup keep bearer tokens out of storage and HTML interpolation', () => {
  const script = filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/js/audio-analysis-settings.js'), 'utf8');
  assert.doesNotMatch(script, /localStorage|sessionStorage|innerHTML|console\./);
  const markup = filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/index.html'), 'utf8');
  assert.match(markup, /id="audio-analysis-token" class="form-input" type="password" autocomplete="new-password" disabled/);
  assert.match(markup, /audio-analysis-settings\.js\?v=/);
  assert.ok(markup.indexOf('/js/audio-analysis-settings.js') < markup.indexOf('/js/dashboard.js'));
});
const virtualMachine = require('node:vm');
function dashboardFixture() {
  const activity = [];
  const element = { classList: { add() {}, remove() {} } };
  const sandbox = { activity, document: { addEventListener() {}, querySelectorAll() { return []; },
    getElementById() { return element; }, querySelector() { return element; } },
    window: { location: { hash: '#settings' }, scrollTo() {} },
    localStorage: { getItem() { return Policy.empty; }, setItem() {}, removeItem() {} }, URLSearchParams };
  virtualMachine.createContext(sandbox);
  virtualMachine.runInContext(filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/js/dashboard.js'), 'utf8'), sandbox);
  virtualMachine.runInContext(`
    updateAuthUI = synchronizeDashboardPolling = () => {};
    loadProviderSettings = () => activity.push('providers');
    audioAnalysisSettings = { authenticationChanged() { activity.push('authorized:' + isAdmin()); }, load() { activity.push('audio-settings'); } };
  `, sandbox);
  return { activity, run(expression) { return virtualMachine.runInContext(expression, sandbox); } };
}
test('settings route loads audio settings alongside existing provider settings', () => {
  const value = dashboardFixture(); value.run('handleRoute()');
  assert.deepEqual(value.activity, ['providers', 'audio-settings']);
});
test('dashboard authentication changes clear audio state after the new account permissions apply', () => {
  const value = dashboardFixture();
  value.run("setAuthToken('administrator-token', 'administrator', 'Admin')");
  value.run("setAuthToken('', '', '')");
  assert.deepEqual(value.activity, ['authorized:true', 'authorized:false']);
});
