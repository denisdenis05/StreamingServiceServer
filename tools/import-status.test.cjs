const test = require('node:test');
const assert = require('node:assert/strict');
const filesystem = require('node:fs');
const virtualMachine = require('node:vm');

function render(importProgress, progress = 1) {
  const container = { innerHTML: '' };
  const sandbox = { document: { addEventListener() {}, getElementById() { return container; } },
    localStorage: { getItem() { return ''; } }, window: {}, console };
  virtualMachine.createContext(sandbox);
  virtualMachine.runInContext(filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/js/dashboard.js'), 'utf8'), sandbox);
  sandbox.album = { id: 'album', title: 'Album', artist: 'Artist', status: 'downloading',
    sourceName: 'slskd:source', providerName: 'Soulseek', hasDownloadSource: true, importProgress, progress };
  virtualMachine.runInContext("state.authToken = 'administrator'; state.userRole = 'Admin'; state.queued = [album]; renderQueueTable()", sandbox);
  return container.innerHTML;
}

test('completed downloads awaiting import have a separate status', () => {
  assert.match(render(null), /awaiting import/);
  assert.match(render(null), /Download complete; waiting for import/);
  assert.match(render(null, 0.5), /downloading \(50%\)/);
});

test('moved tracks with pending source cleanup show finalizing', () => {
  const markup = render({ phase: 'CleaningProvider', description: 'Tracks imported; cleaning up the download source', lastError: null });
  assert.match(markup, /finalizing/);
  assert.match(markup, /cleaning up the download source/);
  assert.doesNotMatch(markup, /downloading \(100%\)/);
});

test('persisted failures explain why an imported album stays in the queue', () => {
  const markup = render({ phase: 'RemovingQueue', description: 'Import complete; removing the album from the queue', lastError: 'database unavailable' });
  assert.match(markup, /import error/);
  assert.match(markup, /database unavailable/);
  assert.match(markup, /stays queued until import and finalization succeed/);
  assert.match(markup, /change source/);
});

test('partial publication shows the actual imported count', () => {
  const markup = render({ phase: 'ImportingTracks', description: '1/5 tracks imported', lastError: null });
  assert.match(markup, /importing/);
  assert.match(markup, /1\/5 tracks imported/);
});

test('diagnostic text is escaped before rendering', () => {
  const markup = render({ phase: 'ImportingTracks', description: '<script>description</script>', lastError: '<img src=x onerror=alert(1)>' });
  assert.doesNotMatch(markup, /<script>|<img src=x/);
  assert.match(markup, /&lt;script&gt;/);
  assert.match(markup, /&lt;img/);
});
