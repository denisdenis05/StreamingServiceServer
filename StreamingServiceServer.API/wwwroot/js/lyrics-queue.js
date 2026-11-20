(function (root) {
  const Policy = Object.freeze({
    endpoint: '/api/dashboard/lyrics', elementPrefix: 'lyrics-queue-', pageSize: 25, firstPage: 1,
    initialGeneration: 0, generationIncrement: 1, pageIncrement: 1, noCount: 0,
    allStates: 'All', downloadedState: 'Downloaded', skippedState: 'Skipped', pendingState: 'Pending', changeEvent: 'change', clickEvent: 'click', abortedFailure: 'AbortError',
    commonModuleType: 'undefined', emptyText: '', unknownValue: '—',
    elementNames: ['summary', 'rows', 'filter', 'previous', 'next', 'page', 'message'],
  });
  const Labels = Object.freeze({
    Pending: 'Queued', Downloaded: 'Downloaded', Instrumental: 'Instrumental',
    NotFound: 'No lyrics found', Skipped: 'Skipped', Failed: 'Retrying',
    naming_issue: 'Audio analysis: name or artist mismatch', invalid_metadata: 'Incomplete recording metadata',
    unmatched_recording: 'No confident recording match', lyrics_not_found: 'No lyrics found',
    provider_failure: 'Lyrics service unavailable', provider_timeout: 'Lyrics service timed out',
    metadata_changed: 'Recording changed; queued again', invalid_response: 'Invalid lyrics response',
    loading: 'Loading lyrics downloads…', unauthorized: 'Sign in as an administrator to view lyrics downloads.',
    unavailable: 'Could not refresh lyrics downloads. Retrying automatically.', empty: 'No lyrics jobs match this filter.',
    initial: 'Lyrics downloads will appear as the library is scanned.', downloading: 'Downloading',
    downloadedSeparator: ' downloaded · ', skippedSeparator: ' skipped · ', waitingSuffix: ' awaiting discovery',
    pagePrefix: 'Page ', pageSeparator: ' of ', reasonSeparator: ' · ',
  });
  function createController({ document, request, isAuthorized, escapeHtml }) {
    const elements = Object.fromEntries(Policy.elementNames.map(name => [name, document.getElementById(Policy.elementPrefix + name)]));
    let active = false;
    let authorized = false;
    let generation = Policy.initialGeneration;
    let page = Policy.firstPage;
    let controller = null;
    let pending = null;
    function cancel() {
      generation += Policy.generationIncrement;
      controller?.abort();
      controller = null;
      pending = null;
    }
    function clear() {
      elements.rows.replaceChildren();
      elements.summary.textContent = Policy.unknownValue;
      elements.page.textContent = Policy.emptyText;
      elements.previous.disabled = true;
      elements.next.disabled = true;
      elements.message.textContent = Labels.unauthorized;
    }
    function render(data) {
      page = data.page;
      const count = state => data.counts.find(entry => entry.state === state)?.count ?? Policy.noCount;
      const undiscovered = Math.max(Policy.noCount, data.totalRecordings - data.totalJobs);
      elements.summary.textContent = count(Policy.downloadedState) + Labels.downloadedSeparator + count(Policy.skippedState)
        + Labels.skippedSeparator + undiscovered + Labels.waitingSuffix;
      elements.rows.innerHTML = data.jobs.map(job => {
        const state = Object.hasOwn(Labels, job.state) ? Labels[job.state] : Labels.Pending;
        const reason = Object.hasOwn(Labels, job.lastFailure) ? Labels[job.lastFailure] : Policy.emptyText;
        const status = job.isDownloading && job.state === Policy.pendingState ? Labels.downloading : state;
        return '<tr><td>' + escapeHtml(job.title) + '</td><td>' + escapeHtml(job.artist)
          + '</td><td>' + escapeHtml(status) + (reason ? Labels.reasonSeparator + escapeHtml(reason) : Policy.emptyText) + '</td></tr>';
      }).join(Policy.emptyText);
      const lastPage = Math.max(Policy.firstPage, Math.ceil(data.filteredJobs / data.pageSize));
      elements.page.textContent = Labels.pagePrefix + page + Labels.pageSeparator + lastPage;
      elements.previous.disabled = page <= Policy.firstPage;
      elements.next.disabled = page >= lastPage;
      elements.message.textContent = data.jobs.length === Policy.noCount
        ? data.totalJobs === Policy.noCount ? Labels.initial : Labels.empty : Policy.emptyText;
    }
    function refresh(replace = false) {
      if (!active) return Promise.resolve();
      if (!isAuthorized()) { cancel(); clear(); return Promise.resolve(); }
      if (pending !== null && !replace) return pending;
      controller?.abort();
      controller = new AbortController();
      generation += Policy.generationIncrement;
      const current = generation;
      const signal = controller.signal;
      const query = new URLSearchParams({ page: String(page), pageSize: String(Policy.pageSize) });
      if (elements.filter.value !== Policy.allStates) query.set('state', elements.filter.value);
      elements.message.textContent = Labels.loading;
      const task = Promise.resolve().then(() => request(Policy.endpoint + '?' + query, { signal }))
        .then(response => {
          if (!response.ok) throw new Error(Labels.unavailable);
          return response.json();
        }).then(data => { if (active && current === generation && isAuthorized()) render(data); })
        .catch(failure => {
          if (!active || current !== generation || failure?.name === Policy.abortedFailure) return;
          if (!isAuthorized()) clear();
          else elements.message.textContent = Labels.unavailable;
        }).finally(() => { if (pending === task) pending = null; });
      pending = task;
      return task;
    }
    elements.filter.addEventListener(Policy.changeEvent, () => { page = Policy.firstPage; void refresh(true); });
    elements.previous.addEventListener(Policy.clickEvent, () => {
      if (!elements.previous.disabled) { page -= Policy.pageIncrement; void refresh(true); }
    });
    elements.next.addEventListener(Policy.clickEvent, () => {
      if (!elements.next.disabled) { page += Policy.pageIncrement; void refresh(true); }
    });
    return { refresh, setActive(value) {
      const currentAuthorization = isAuthorized();
      if (active === value && authorized === currentAuthorization) return;
      active = value;
      authorized = currentAuthorization;
      if (!authorized) { cancel(); clear(); }
      else if (!active) cancel();
      else void refresh(true);
    } };
  }
  const exported = { createController };
  if (typeof module !== Policy.commonModuleType && module.exports) module.exports = exported;
  else root.LyricsDownloadQueue = exported;
})(globalThis);
