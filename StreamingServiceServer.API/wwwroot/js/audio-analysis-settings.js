(function (root) {
  const Policy = Object.freeze({
    profile: 'musicnn-v1',
    conflictStatus: 409,
    endpointSuffix: '/settings/audio-analysis',
    testSuffix: '/test',
    contentType: 'application/json',
    emptyText: '',
    unavailableCount: 'Not loaded',
    countNames: ['pending', 'processing', 'completed', 'unavailable', 'failed']
  });
  const Messages = Object.freeze({
    authentication: 'Sign in as an admin to manage local audio analysis.',
    loading: 'Loading audio analysis settings.',
    ready: 'Settings loaded. Processing is controlled independently of playback.',
    saving: 'Saving audio analysis settings.',
    saved: 'Audio analysis settings saved.',
    testing: 'Testing the saved connection.',
    connected: 'The saved AudioMuse connection is ready.',
    failedLoad: 'Could not load audio analysis settings. Try refreshing.',
    failedSave: 'Could not save settings. Your changes are still here.',
    failedTest: 'The saved AudioMuse connection could not be verified.',
    conflict: 'Settings changed elsewhere. Refresh to load them before saving again. Your changes are still here.',
    tokenConfigured: 'A token is saved. Leave the field blank to keep it.',
    tokenMissing: 'No API token is saved.'
  });
  function createController({ document, request, isAuthorized, endpointBase }) {
    const elements = Object.fromEntries([
      'enabled', 'endpoint', 'token', 'clear-token', 'token-status', 'save', 'refresh', 'test', 'status', ...Policy.countNames
    ].map(name => [name, document.getElementById('audio-analysis-' + name)]));
    let revision = null;
    let pending = null;
    let generation = 0;
    let stale = false;
    const endpoint = endpointBase + Policy.endpointSuffix;
    function setStatus(message) { elements.status.textContent = message; }
    function renderControls() {
      const editable = isAuthorized() && revision !== null && pending === null;
      for (const name of ['enabled', 'endpoint', 'token', 'clear-token']) elements[name].disabled = !editable;
      elements.save.disabled = !editable || stale;
      elements.test.disabled = !editable;
      elements.refresh.disabled = !isAuthorized() || pending !== null;
    }
    function clearSecret() { elements.token.value = Policy.emptyText; elements['clear-token'].checked = false; }
    function apply(settings) {
      revision = settings.revision;
      stale = false;
      elements.enabled.checked = settings.enabled;
      elements.endpoint.value = settings.endpointUrl;
      elements['token-status'].textContent = settings.apiTokenConfigured ? Messages.tokenConfigured : Messages.tokenMissing;
      for (const name of Policy.countNames) elements[name].textContent = String(settings.counts[name]);
      clearSecret();
    }
    function validate(settings) {
      return settings && typeof settings.enabled === 'boolean' && typeof settings.endpointUrl === 'string'
        && settings.profile === Policy.profile && settings.revision !== null && settings.revision !== undefined
        && typeof settings.apiTokenConfigured === 'boolean' && settings.counts
        && Policy.countNames.every(name => Number.isSafeInteger(settings.counts[name]) && settings.counts[name] >= 0);
    }
    async function perform(kind) {
      if (!isAuthorized()) { authenticationChanged(); return; }
      if (pending || (kind !== 'load' && (revision === null || stale))) return;
      const controller = new AbortController();
      const operationGeneration = generation;
      pending = controller;
      renderControls();
      const current = () => operationGeneration === generation && pending === controller && isAuthorized();
      const loading = kind === 'load';
      const saving = kind === 'save';
      setStatus(loading ? Messages.loading : saving ? Messages.saving : Messages.testing);
      try {
        const options = { signal: controller.signal };
        if (!loading) options.method = saving ? 'PUT' : 'POST';
        if (saving) {
          options.headers = { 'Content-Type': Policy.contentType };
          options.body = JSON.stringify({ enabled: elements.enabled.checked, endpointUrl: elements.endpoint.value.trim(),
            profile: Policy.profile, expectedRevision: revision, apiToken: elements.token.value || null,
            clearApiToken: elements['clear-token'].checked });
        }
        const response = await request(endpoint + (kind === 'test' ? Policy.testSuffix : Policy.emptyText), options);
        if (!current()) return;
        if (response.status === Policy.conflictStatus && saving) { stale = true; setStatus(Messages.conflict); return; }
        if (!response.ok) throw new Error();
        const result = await response.json();
        if (!current()) return;
        if (kind === 'test') {
          if (result?.ready !== true) throw new Error();
          setStatus(Messages.connected);
        } else {
          if (!validate(result)) throw new Error();
          apply(result);
          setStatus(saving ? Messages.saved : Messages.ready);
        }
      } catch (failure) {
        if (current()) setStatus(loading ? Messages.failedLoad : saving ? Messages.failedSave : Messages.failedTest);
      } finally {
        if (current()) { pending = null; renderControls(); }
      }
    }
    function authenticationChanged() {
      generation += 1;
      pending?.abort();
      pending = null;
      revision = null;
      stale = false;
      elements.enabled.checked = false;
      elements.endpoint.value = Policy.emptyText;
      elements['token-status'].textContent = Policy.emptyText;
      clearSecret();
      for (const name of Policy.countNames) elements[name].textContent = Policy.unavailableCount;
      setStatus(Messages.authentication);
      renderControls();
    }
    elements.save.addEventListener('click', () => { void perform('save'); });
    elements.refresh.addEventListener('click', () => { void perform('load'); });
    elements.test.addEventListener('click', () => { void perform('test'); });
    authenticationChanged();
    return { load: () => perform('load'), save: () => perform('save'), test: () => perform('test'), authenticationChanged };
  }
  const exports = { createController };
  if (typeof module !== 'undefined' && module.exports) module.exports = exports;
  else root.AudioAnalysisSettings = exports;
})(typeof globalThis === 'undefined' ? this : globalThis);
