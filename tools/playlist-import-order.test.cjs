const test = require('node:test');
const assert = require('node:assert/strict');
const filesystem = require('node:fs');
const virtualMachine = require('node:vm');

const Fixture = Object.freeze({
  dashboardPath: '../StreamingServiceServer.API/wwwroot/js/dashboard.js',
  encoding: 'utf8',
  title: 'Imported song',
  secondTitle: 'Another imported song',
  artist: 'Imported artist',
  album: 'Imported album',
  additionDate: '2020-01-01T14:00:00+02:00',
  emptyText: '',
  sourceExpression: 'playlistImportTrack(inputTrack)',
  reverseExpression: 'inputTracks.map(playlistImportTrack).reverse()',
});

function dashboard() {
  const sandbox = {
    document: { addEventListener() {} },
    window: {},
    localStorage: { getItem() { return Fixture.emptyText; } },
    console,
  };
  virtualMachine.createContext(sandbox);
  virtualMachine.runInContext(filesystem.readFileSync(require.resolve(Fixture.dashboardPath), Fixture.encoding), sandbox);
  return sandbox;
}

function normalize(inputTrack) {
  const sandbox = dashboard();
  sandbox.inputTrack = inputTrack;
  return JSON.parse(JSON.stringify(virtualMachine.runInContext(Fixture.sourceExpression, sandbox)));
}

test('import forwards the original addition date without converting its timezone', () => {
  assert.deepEqual(normalize({ name: Fixture.title, artistName: Fixture.artist, releaseName: Fixture.album, addedAt: Fixture.additionDate }),
    { name: Fixture.title, artistName: Fixture.artist, releaseName: Fixture.album, addedAt: Fixture.additionDate });
});

test('Spotify addition dates survive artist and album normalization', () => {
  assert.deepEqual(normalize({ name: Fixture.title, artists: [{ name: Fixture.artist }], album: { name: Fixture.album }, added_at: Fixture.additionDate }),
    { name: Fixture.title, artistName: Fixture.artist, releaseName: Fixture.album, addedAt: Fixture.additionDate });
});

test('undated imports leave addition history unknown', () => {
  assert.deepEqual(normalize({ title: Fixture.title, artist: Fixture.artist, album: Fixture.album }),
    { name: Fixture.title, artistName: Fixture.artist, releaseName: Fixture.album, addedAt: null });
});

test('reversing file order keeps each original addition date attached to its song', () => {
  const sandbox = dashboard();
  sandbox.inputTracks = [
    { name: Fixture.title, added_at: Fixture.additionDate },
    { name: Fixture.secondTitle },
  ];
  const reversed = JSON.parse(JSON.stringify(virtualMachine.runInContext(Fixture.reverseExpression, sandbox)));
  assert.deepEqual(reversed, [
    { name: Fixture.secondTitle, artistName: Fixture.emptyText, releaseName: Fixture.emptyText, addedAt: null },
    { name: Fixture.title, artistName: Fixture.emptyText, releaseName: Fixture.emptyText, addedAt: Fixture.additionDate },
  ]);
});
