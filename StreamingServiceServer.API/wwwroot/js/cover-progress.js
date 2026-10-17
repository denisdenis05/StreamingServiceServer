(function (root) {
  const Policy = Object.freeze({ endpoint: '/api/dashboard/covers', pageSize: 25, firstPage: 1,
    maximumPercentage: 100, emptyPercentage: 0, searchDelayMilliseconds: 250 });
  const Labels = Object.freeze({ downloaded: 'Downloaded', downloading: 'Downloading', queued: 'Queued',
    retrying: 'Retrying', partial: 'Partly downloaded', unavailable: 'Unavailable', 'no-source': 'No artwork source' });
  function percentage(value) {
    const numeric = Number(value);
    return Number.isFinite(numeric) ? Math.min(Policy.maximumPercentage, Math.max(Policy.emptyPercentage, numeric)) : Policy.emptyPercentage;
  }
  function createController({ document, request, isAuthorized, escapeHtml, onAlbumSelected,
    schedule = setTimeout, cancelScheduled = clearTimeout }) {
    const elements = Object.fromEntries(['summary', 'bar', 'caption', 'rows', 'filter', 'search', 'previous', 'next', 'page', 'message']
      .map(name => [name, document.getElementById('cover-progress-' + name)]));
    let active = false; let authorized = false; let generation = 0; let page = Policy.firstPage; let searchTimer = null;
    let controller = null; let pending = null; let validData = false;
    function reset() {
      generation += 1; controller?.abort(); controller = null; pending = null;
      if (searchTimer !== null) { cancelScheduled(searchTimer); searchTimer = null; }
    }
    function clear(message) {
      validData = false;
      elements.bar.value = Policy.emptyPercentage;
      elements.summary.textContent = '—'; elements.caption.textContent = '';
      elements.rows.replaceChildren(); elements.page.textContent = '';
      elements.previous.disabled = true; elements.next.disabled = true;
      elements.message.textContent = message;
    }
    function render(data) {
      validData = true; page = data.page;
      elements.bar.value = percentage(data.summary.percentage);
      elements.summary.textContent = percentage(data.summary.percentage) + '%';
      elements.caption.textContent = data.summary.downloadedAlbums + ' of ' + data.summary.totalAlbums + ' albums have downloaded covers';
      const albums = Array.isArray(data.albums) ? data.albums : [];
      elements.rows.innerHTML = albums.map(album => {
        const completion = percentage(album.percentage);
        const status = Object.hasOwn(Labels, album.status) ? Labels[album.status] : 'Waiting';
        const retry = album.status === 'retrying' && album.nextAttemptAt ? new Date(album.nextAttemptAt) : null;
        const retryText = retry && Number.isFinite(retry.getTime()) ? ' · next attempt ' + retry.toLocaleTimeString() : '';
        return '<tr><td><button class="cover-album-link" data-cover-album="' + escapeHtml(album.id) + '">' + escapeHtml(album.title) + '</button></td>'
          + '<td>' + escapeHtml(album.artist) + '</td><td><div class="cover-album-completion"><progress max="100" value="' + completion
          + '" aria-label="' + escapeHtml('Cover download progress for ' + album.title) + '"></progress><span>' + completion + '%</span></div></td>'
          + '<td><span class="cover-status cover-status-' + (Object.hasOwn(Labels, album.status) ? album.status : 'queued') + '">' + escapeHtml(status) + '</span>'
          + escapeHtml(retryText) + '</td></tr>';
      }).join('');
      const lastPage = Math.max(Policy.firstPage, Math.ceil(data.filteredAlbums / data.pageSize));
      elements.page.textContent = 'Page ' + page + ' of ' + lastPage;
      elements.previous.disabled = page <= Policy.firstPage; elements.next.disabled = page >= lastPage;
      elements.message.textContent = albums.length === 0 ? data.summary.totalAlbums === 0 ? 'No albums in the library yet.' : 'No albums match this filter.' : '';
    }
    function refresh(replace = false) {
      if (!active) return Promise.resolve();
      if (!isAuthorized()) { reset(); clear('Sign in as an administrator to view cover downloads.'); return Promise.resolve(); }
      if (pending !== null && !replace) return pending;
      controller?.abort(); const operationController = new AbortController(); controller = operationController; const current = ++generation;
      if (!validData) elements.message.textContent = 'Loading cover downloads…';
      const query = new URLSearchParams({ page: String(page), pageSize: String(Policy.pageSize), filter: elements.filter.value,
        search: elements.search.value.trim() });
      const task = Promise.resolve().then(() => request(Policy.endpoint + '?' + query, { signal: operationController.signal })).then(response => {
        if (!response.ok) throw new Error('Cover download status is unavailable.');
        return response.json();
      }).then(data => {
        if (active && current === generation && isAuthorized()) render(data);
      }).catch(failure => {
        if (!active || current !== generation || failure?.name === 'AbortError') return;
        if (!isAuthorized()) { clear('Sign in as an administrator to view cover downloads.'); return; }
        elements.message.textContent = validData ? 'Could not refresh cover status. Showing the last result; retrying automatically.'
          : 'Could not load cover status. Retrying automatically.';
      }).finally(() => { if (pending === task) pending = null; });
      pending = task; return task;
    }
    elements.filter.addEventListener('change', () => { page = Policy.firstPage; void refresh(true); });
    elements.search.addEventListener('input', () => {
      if (searchTimer !== null) cancelScheduled(searchTimer);
      searchTimer = schedule(() => { searchTimer = null; page = Policy.firstPage; void refresh(true); }, Policy.searchDelayMilliseconds);
    });
    elements.previous.addEventListener('click', () => { if (!elements.previous.disabled) { page -= 1; void refresh(true); } });
    elements.next.addEventListener('click', () => { if (!elements.next.disabled) { page += 1; void refresh(true); } });
    elements.rows.addEventListener('click', event => {
      const button = event.target.closest('[data-cover-album]');
      if (button && elements.rows.contains(button)) onAlbumSelected(button.dataset.coverAlbum);
    });
    return { refresh, setActive(value) {
      const currentAuthorization = isAuthorized();
      if (active === value && authorized === currentAuthorization) return;
      active = value; authorized = currentAuthorization;
      if (!currentAuthorization) { reset(); clear('Sign in as an administrator to view cover downloads.'); }
      else if (!active) reset();
      else void refresh(true);
    } };
  }
  const exported = { createController, percentage };
  if (typeof module !== 'undefined' && module.exports) module.exports = exported;
  else root.CoverDownloadProgress = exported;
})(globalThis);
