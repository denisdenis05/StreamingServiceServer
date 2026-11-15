const test = require('node:test');
const assert = require('node:assert/strict');
const filesystem = require('node:fs');
const virtualMachine = require('node:vm');

const emptyIdentifier = '00000000-0000-0000-0000-000000000000';
const searchIdentifier = '827351c4-5647-4ed9-901e-62544f800197';
const source = { username: 'album-peer', directoryPath: 'Music/Album', fileCount: 2,
  totalSizeBytes: 2000000, formats: ['FLAC'], files: [{ filename: 'Music/Album/01.flac', size: 1000000 }] };

function deferred() {
  let resolve;
  const promise = new Promise(complete => { resolve = complete; });
  return { promise, resolve };
}

async function settle() {
  const promiseTurns = 20;
  for (let turn = 0; turn < promiseTurns; turn++) await Promise.resolve();
}

function fixture() {
  const elements = new Map();
  const requests = [];
  const refreshes = [];
  function element(identifier) {
    if (!elements.has(identifier)) elements.set(identifier, { style: {}, value: '', textContent: '', innerHTML: '', disabled: false,
      querySelectorAll() { return []; } });
    return elements.get(identifier);
  }
  const sandbox = { document: { getElementById: element, addEventListener() {} },
    window: { location: { hash: '#queue' } }, localStorage: { getItem() { return ''; } },
    console, requests, refreshes, request(url, options) {
      const completion = deferred();
      requests.push({ url, options, respond(data, status = 200) {
        completion.resolve({ ok: status < 400, status, text: async () => JSON.stringify(data) });
      } });
      return completion.promise;
    } };
  virtualMachine.createContext(sandbox);
  virtualMachine.runInContext(filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/js/dashboard.js'), 'utf8'), sandbox);
  const run = expression => virtualMachine.runInContext(expression, sandbox);
  run(`state.authToken = 'administrator'; state.userRole = 'Admin'; authFetch = request;
    fetchQueued = async () => { refreshes.push('queued'); };
    state.queued = [{ id: 'album', title: 'Album', artist: 'Artist', status: 'failed', hasDownloadSource: false }];
    state.failed = [...state.queued];`);
  return { element, requests, refreshes, run };
}

async function openAdding(value) {
  value.run("openSlskdSourcesModal('album', true)");
  value.requests[0].respond({ sources: [source], searchIdentifier, importIdentifier: emptyIdentifier });
  await settle();
}

test('audio queue offers manual sources for queued and failed albums without a source', () => {
  const value = fixture();
  for (const status of ['queued', 'failed']) {
    value.run(`state.queued[0].status = '${status}'; renderQueueTable()`);
    assert.match(value.element('queue-table-body').innerHTML, /add source manually/);
  }
  value.run("state.queued[0] = { id: 'album', status: 'downloading', providerName: 'Soulseek', sourceName: 'slskd:source', hasDownloadSource: true }; renderQueueTable()");
  assert.match(value.element('queue-table-body').innerHTML, /change source/);
  assert.doesNotMatch(value.element('queue-table-body').innerHTML, /add source manually/);
  value.run("state.queued[0].providerName = 'Torrent'; renderQueueTable()");
  assert.doesNotMatch(value.element('queue-table-body').innerHTML, /add source manually/);
});

test('failed table offers manual source recovery only for albums without sources', () => {
  const value = fixture();
  value.run('renderFailedTable()');
  assert.match(value.element('failed-table-body').innerHTML, /add source manually/);
  value.run('state.failed[0].hasDownloadSource = true; renderFailedTable()');
  assert.doesNotMatch(value.element('failed-table-body').innerHTML, /add source manually/);
});

test('manual picker starts fresh search from album metadata and suppresses duplicate searches', async () => {
  const value = fixture();
  value.run("openSlskdSourcesModal('album', true); searchSlskdSources()");
  assert.equal(value.requests.length, 1);
  assert.match(value.requests[0].url, /slskd-source-search$/);
  assert.equal(value.requests[0].options.method, 'POST');
  assert.equal(JSON.parse(value.requests[0].options.body).query, 'Artist Album');
  assert.equal(value.element('slskd-sources-search-button').disabled, true);
  value.requests[0].respond({ sources: [], searchIdentifier, importIdentifier: emptyIdentifier });
  await settle();
  assert.match(value.element('slskd-sources-empty').textContent, /different search terms/);
  assert.equal(value.element('slskd-sources-search-button').disabled, false);
  value.element('slskd-sources-query').value = '  alternate album title  ';
  value.run('searchSlskdSources()');
  assert.equal(JSON.parse(value.requests[1].options.body).query, 'alternate album title');
  value.requests[1].respond({ sources: [], searchIdentifier, importIdentifier: emptyIdentifier });
  await settle();
});

test('successful manual selection clears failed UI state and refreshes the queue', async () => {
  const value = fixture();
  await openAdding(value);
  const selecting = value.run('selectSlskdSource(0)');
  value.run('selectSlskdSource(0); searchSlskdSources()');
  assert.equal(value.requests.length, 2);
  assert.match(value.requests[1].url, /add-slskd-source$/);
  const payload = JSON.parse(value.requests[1].options.body);
  assert.equal(payload.searchIdentifier, searchIdentifier);
  assert.equal(payload.expectedImportIdentifier, emptyIdentifier);
  assert.deepEqual(payload.files, source.files);
  value.requests[1].respond({ message: 'Source added. The album is queued.' });
  await settle();
  assert.equal(value.run('state.failed.length'), 0);
  assert.deepEqual(value.refreshes, ['queued']);
  assert.match(value.requests[2].url, /slskd-sources$/);
  assert.equal(value.element('slskd-sources-search-form').style.display, 'none');
  value.requests[2].respond({ sources: [{ ...source, isCurrentSource: true }], importIdentifier: searchIdentifier });
  await selecting;
  assert.equal(value.run('_slskdSourceChanging'), false);
  assert.match(value.element('slskd-sources-table-body').innerHTML, /current/);
});

test('enqueue failure retains failed album and lets selection retry', async () => {
  const value = fixture();
  await openAdding(value);
  const selecting = value.run('selectSlskdSource(0)');
  value.requests[1].respond({ message: 'slskd has not queued every selected file.' }, 502);
  await selecting;
  assert.equal(value.run('state.failed.length'), 1);
  assert.equal(value.run('_slskdSourceChanging'), false);
  assert.match(value.element('slskd-sources-status').textContent, /not queued/);
  assert.deepEqual(value.refreshes, []);
});

test('existing change-source flow still submits its original import identity', async () => {
  const value = fixture();
  value.run("openSlskdSourcesModal('album')");
  assert.match(value.requests[0].url, /slskd-sources$/);
  value.requests[0].respond({ sources: [source], importIdentifier: searchIdentifier });
  await settle();
  const selecting = value.run('selectSlskdSource(0)');
  assert.match(value.requests[1].url, /change-slskd-source$/);
  const payload = JSON.parse(value.requests[1].options.body);
  assert.equal(payload.expectedImportIdentifier, searchIdentifier);
  assert.equal(payload.searchIdentifier, undefined);
  value.requests[1].respond({ message: 'unavailable' }, 502);
  await selecting;
});

test('closed modal ignores late search results and clears stale choices on reopen', async () => {
  const value = fixture();
  value.run("openSlskdSourcesModal('album', true); closeSlskdSourcesModal(); openSlskdSourcesModal('other', true)");
  value.requests[0].respond({ sources: [source], searchIdentifier, importIdentifier: emptyIdentifier });
  await settle();
  assert.equal(value.run('_slskdSourcesData.length'), 0);
  assert.equal(value.run('_slskdSourcesAlbumId'), 'other');
  value.requests[1].respond({ sources: [], searchIdentifier, importIdentifier: emptyIdentifier });
  await settle();
  assert.equal(value.element('slskd-sources-container').style.display, 'none');
});

test('late assignment response cannot change a reopened modal or remove its failed row', async () => {
  const value = fixture();
  await openAdding(value);
  const selecting = value.run('selectSlskdSource(0)');
  value.run("closeSlskdSourcesModal(); openSlskdSourcesModal('other', true)");
  value.requests[1].respond({ message: 'Source added.' });
  await selecting;
  assert.equal(value.run('state.failed.length'), 1);
  assert.equal(value.run('_slskdSourcesAdding'), true);
  assert.deepEqual(value.refreshes, []);
  value.requests[2].respond({ sources: [], searchIdentifier, importIdentifier: emptyIdentifier });
  await settle();
});
