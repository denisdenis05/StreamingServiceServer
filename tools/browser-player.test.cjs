const { test } = require('node:test');
const assertion = require('node:assert/strict');
const { createController, button, formatTime } = require('../StreamingServiceServer.API/wwwroot/js/browser-player');
const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
function deferred() { let resolve; let reject; const promise = new Promise((complete, fail) => { resolve = complete; reject = fail; }); return { promise, resolve, reject }; }
class Element {
  constructor() { this.listeners = {}; this.dataset = {}; this.attributes = {}; this.textContent = ''; this.value = 0; this.disabled = false; this.hidden = true; }
  addEventListener(name, listener) { this.listeners[name] = listener; }
  emit(name, event = {}) { return this.listeners[name]?.(event); }
  setAttribute(name, value) { this.attributes[name] = value; }
}
class AudioFixture {
  constructor() { this.listeners = {}; this.paused = true; this.ended = false; this.currentTime = 0; this.duration = NaN; this.seekable = { length: 0 }; this.plays = 0; }
  addEventListener(name, listener) { this.listeners[name] = listener; }
  emit(name) { this.listeners[name]?.(); }
  async play() { this.plays += 1; if (this.failure) throw this.failure; this.paused = false; this.emit('playing'); }
  pause() { this.paused = true; this.emit('pause'); }
  removeAttribute() { this.src = ''; }
  load() {}
}
function fixture() {
  const elements = {}; const audio = []; const requests = []; const events = {}; let authentication = 'first'; let clearCalls = 0;
  let authorize = async () => ({ ok: true });
  const document = { getElementById(name) { return elements[name] ??= new Element(); }, querySelectorAll() { return []; },
    addEventListener(name, callback) { events[name] = callback; }, body: { classList: { add() {}, remove() {} } } };
  const player = createController({ document, authentication: () => authentication, createAudio() { const current = new AudioFixture(); audio.push(current); return current; },
    request(address, options) { requests.push({ address, options }); return authorize(); }, async clearSession() { clearCalls += 1; } });
  return { player, audio, requests, events, element(name) { return elements['browser-player-' + name]; },
    authorize(callback) { authorize = callback; }, account(value) { authentication = value; player.authenticationChanged(); }, get clears() { return clearCalls; } };
}
const track = { identifier: 'recording-one', title: 'Song', artist: 'Artist' };
test('button renders escaped track metadata and disables missing recordings', () => {
  const html = button({ ...track, title: '\"><script>', artist: '"' }, escape);
  assertion.ok(html.includes('&quot;&gt;&lt;script&gt;')); assertion.equal(html.includes('<script>'), false);
  assertion.ok(button({ ...track, available: false }, escape).includes(' disabled'));
  assertion.ok(button({ identifier: '00000000-0000-0000-0000-000000000000' }, escape).includes(' disabled'));
});
test('time labels handle fractional, invalid and long durations', () => {
  assertion.equal(formatTime(65.9), '1:05'); assertion.equal(formatTime(NaN), '0:00'); assertion.equal(formatTime(-1), '0:00'); assertion.equal(formatTime(3600), '60:00');
});
test('play authorizes then uses the existing stream URL without preview fallback or URL credentials', async () => {
  const environment = fixture(); await environment.player.play(track);
  assertion.equal(environment.requests[0].address, '/Stream/browser-session'); assertion.equal(environment.requests[0].options.method, 'POST');
  assertion.equal(environment.audio[0].src, '/Stream?id=recording-one&allowPreview=false'); assertion.equal(environment.element('status').textContent, 'Playing');
  assertion.equal(environment.element('title').textContent, 'Song'); assertion.equal(environment.element('container').hidden, false);
});
test('same-track play button toggles pause and resume without replacing the source', async () => {
  const environment = fixture(); await environment.player.play(track); await environment.player.play(track);
  assertion.equal(environment.audio[0].paused, true); assertion.equal(environment.element('status').textContent, 'Paused');
  await environment.player.toggle(); assertion.equal(environment.audio[0].paused, false); assertion.equal(environment.requests.length, 1);
});
test('loading another track stops the previous audio and ignores its late errors', async () => {
  const environment = fixture(); await environment.player.play(track); const previous = environment.audio[0]; await environment.player.play({ ...track, identifier: 'second' });
  assertion.equal(previous.paused, true); assertion.equal(previous.src, ''); previous.emit('error');
  assertion.equal(environment.element('status').textContent, 'Playing'); assertion.equal(environment.audio.length, 2);
});
test('a superseded pending authorization cannot start old audio', async () => {
  const environment = fixture(); const authorization = deferred(); environment.authorize(() => authorization.promise);
  const first = environment.player.play(track); await Promise.resolve(); await Promise.resolve();
  const second = environment.player.play({ ...track, identifier: 'second' }); authorization.resolve({ ok: true }); await Promise.all([first, second]);
  assertion.equal(environment.audio.length, 1); assertion.ok(environment.audio[0].src.includes('id=second'));
});
test('closing during authorization prevents audio startup', async () => {
  const environment = fixture(); const authorization = deferred(); environment.authorize(() => authorization.promise);
  const operation = environment.player.play(track); environment.player.stop(); authorization.resolve({ ok: true }); await operation;
  assertion.equal(environment.audio.length, 0); assertion.equal(environment.element('container').hidden, true);
});
test('logout stops playback and clears credentials after pending authorization settles', async () => {
  const environment = fixture(); const authorization = deferred(); environment.authorize(() => authorization.promise);
  const operation = environment.player.play(track); await Promise.resolve(); await Promise.resolve(); environment.account(''); authorization.resolve({ ok: true }); await operation;
  await new Promise(resolve => setImmediate(resolve)); assertion.equal(environment.audio.length, 0); assertion.equal(environment.clears, 1);
});
test('missing and anonymous selections do not request playback', async () => {
  const environment = fixture(); await environment.player.play({ ...track, available: false }); assertion.equal(environment.requests.length, 0);
  environment.account(''); await environment.player.play(track); assertion.equal(environment.requests.length, 0);
});
test('progress updates support seeking only when duration and ranges are available', async () => {
  const environment = fixture(); await environment.player.play(track); const audio = environment.audio[0]; assertion.equal(environment.element('progress').disabled, true);
  audio.duration = 120; audio.currentTime = 30; audio.seekable.length = 1; audio.emit('loadedmetadata'); assertion.equal(environment.element('progress').value, 25);
  assertion.equal(environment.element('elapsed').textContent, '0:30'); environment.element('progress').value = 50; environment.element('progress').emit('input'); assertion.equal(audio.currentTime, 60);
});
test('buffering, pause, end and replay reflect actual audio state', async () => {
  const environment = fixture(); await environment.player.play(track); const audio = environment.audio[0]; audio.emit('waiting'); assertion.equal(environment.element('status').textContent, 'Buffering…');
  audio.ended = true; audio.paused = true; audio.emit('ended'); assertion.equal(environment.element('status').textContent, 'Finished'); await environment.player.toggle(); assertion.equal(audio.currentTime, 0);
});
test('format/missing-file errors show a recoverable status and retry the track', async () => {
  const environment = fixture(); await environment.player.play(track); environment.audio[0].emit('error'); assertion.ok(environment.element('status').textContent.includes('unavailable'));
  await environment.player.toggle(); assertion.equal(environment.audio.length, 2); assertion.equal(environment.element('status').textContent, 'Playing');
});
test('authorization failures are visible and can be retried from the floating control', async () => {
  const environment = fixture(); environment.authorize(async () => ({ ok: false })); await environment.player.play(track); assertion.ok(environment.element('status').textContent.includes('authorize'));
  environment.authorize(async () => ({ ok: true })); await environment.player.toggle(); assertion.equal(environment.audio.length, 1);
});
test('delegated row buttons pass track metadata without inline JavaScript handlers', async () => {
  const environment = fixture(); const element = new Element(); element.dataset = { browserPlay: track.identifier, trackTitle: track.title, trackArtist: track.artist };
  environment.events.click({ target: { closest: () => element } }); await new Promise(resolve => setImmediate(resolve)); assertion.equal(environment.audio[0].plays, 1);
  assertion.equal(button(track, escape).includes('onclick='), false);
});
test('actual album and playlist renderers add play controls and disable missing rows', () => {
  const filesystem = require('node:fs'); const virtualMachine = require('node:vm'); const nodes = new Map();
  const document = { addEventListener() {}, getElementById(identifier) { if (!nodes.has(identifier)) nodes.set(identifier, { innerHTML: '' }); return nodes.get(identifier); } };
  const sandbox = { document, localStorage: { getItem() { return ''; } }, window: { location: { hash: '#album?id=fixture' } },
    DashboardBrowserPlayer: { button }, fixtureEscape: escape, fixtureAlbum: { id: 'album', artist: 'Artist', title: 'Album', isDownloaded: true,
      recordings: [{ id: 'present', title: 'Song', fileExists: true }, { id: 'missing', title: 'Missing', fileExists: false }] },
    fixtureTracks: [{ id: 'present', title: 'Song', artistName: 'Artist', releaseId: 'release' }, { id: 'unavailable', title: 'Missing', releaseId: null }] };
  virtualMachine.createContext(sandbox);
  virtualMachine.runInContext(filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/js/dashboard.js'), 'utf8'), sandbox);
  virtualMachine.runInContext('escapeHtml=fixtureEscape; setupDropzone=()=>{}; renderAlbumDetail(fixtureAlbum); renderPlaylistTrackTable(fixtureTracks);', sandbox);
  const album = nodes.get('album-detail-content').innerHTML; const playlist = nodes.get('playlist-track-body').innerHTML;
  assertion.ok(album.includes('data-browser-play="present"')); assertion.ok(album.includes('data-browser-play="missing"'));
  assertion.match(album, /data-browser-play="missing"[^>]+ disabled/); assertion.match(playlist, /data-browser-play="unavailable"[^>]+ disabled/);
  assertion.ok(playlist.includes('<td><button type="button" class="browser-track-play"'));
});
test('playback icons have explicit dimensions without relying on cached stylesheets', () => {
  const html = button(track, escape);
  assertion.match(html, /<svg[^>]*width="20"[^>]*height="20"/);
  assertion.ok(html.includes('xmlns="http://www.w3.org/2000/svg"'));
});
test('dashboard loads versioned player styles and scripts and has fixed-position fallback markup', () => {
  const filesystem = require('node:fs');
  const html = filesystem.readFileSync(require.resolve('../StreamingServiceServer.API/wwwroot/index.html'), 'utf8');
  const assets = [...html.matchAll(/(?:href|src)="(\/(?:css|js)\/[^"?]+)([^\"]*)"/g)];
  assertion.ok(assets.length >= 6);
  assertion.ok(assets.some(match => match[1] === '/css/browser-player.css'));
  assertion.ok(assets.every(match => match[2].startsWith('?v=')));
  assertion.match(html, /id="browser-player-container"[^>]+style="position: fixed;[^>]+transform: translate\(-50%, -100%\);/);
});
