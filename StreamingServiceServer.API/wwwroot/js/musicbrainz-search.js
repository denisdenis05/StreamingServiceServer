(function (root) {
  'use strict';

  const maximumCachedSearches = 16;
  const maximumCachedGroups = 64;
  const searches = new Map();
  const groups = new Map();
  const generations = new Map();

  function clear(context) {
    generations.set(context, (generations.get(context) || 0) + 1);
  }

  function cached(cache, key, maximumEntries, load) {
    if (cache.has(key)) {
      const value = cache.get(key);
      cache.delete(key);
      cache.set(key, value);
      return value;
    }
    const pending = Promise.resolve().then(load).catch(failure => {
      if (cache.get(key) === pending) cache.delete(key);
      throw failure;
    });
    cache.set(key, pending);
    while (cache.size > maximumEntries) cache.delete(cache.keys().next().value);
    return pending;
  }

  async function read(address) {
    const response = await authFetch(address);
    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      throw new Error(problem?.message || `MusicBrainz request failed (HTTP ${response.status}).`);
    }
    return response.json();
  }

  function element(tag, className, text) {
    const created = document.createElement(tag);
    created.className = className;
    if (text !== undefined) created.textContent = text;
    return created;
  }

  function releaseChoice(release, group, select) {
    const choice = element('button', 'mb-release-choice');
    choice.type = 'button';
    const artist = release.artist || group.artist;
    choice.setAttribute('aria-label', `Select ${release.title} by ${artist}, release ${release.id}`);
    const title = element('span', 'mb-release-title', release.title);
    const count = Number.isInteger(release.recordingCount) ? release.recordingCount : null;
    const recordings = element('span', 'mb-release-count', count === null ? '— songs' : `${count} ${count === 1 ? 'song' : 'songs'}`);
    recordings.title = count === null ? 'MusicBrainz did not provide a track count.' : 'Tracks across all media in this release';
    const details = [release.groupType || group.type, release.date, release.country, release.format,
      release.disambiguation].filter(Boolean).join(' · ');
    choice.append(title, recordings, element('span', 'mb-release-artist', artist),
      element('span', 'mb-release-details', details), element('span', 'mb-release-identifier', release.id));
    choice.addEventListener('click', () => select(release.title, artist, release.id));
    return choice;
  }

  function loadGroup(base, groupId) {
    return cached(groups, JSON.stringify([base, groupId]), maximumCachedGroups, async () => {
      const releases = new Map();
      let offset = 0;
      while (offset !== null) {
        const page = await read(`${base}/mb-search/groups/${encodeURIComponent(groupId)}/releases?offset=${offset}`);
        for (const release of page.releases) releases.set(release.id, release);
        if (page.nextOffset !== null && (!Number.isInteger(page.nextOffset) || page.nextOffset <= offset)) {
          throw new Error('MusicBrainz returned an invalid release page.');
        }
        offset = page.nextOffset;
      }
      return [...releases.values()];
    });
  }

  function groupCard(base, group, context, generation, index, select) {
    const card = element('article', 'mb-release-group');
    let representative = releaseChoice(group.release, group, select);
    const editions = element('div', 'mb-release-editions');
    editions.id = `mb-release-editions-${context}-${generation}-${index}`;
    editions.hidden = true;
    const footer = element('div', 'mb-release-footer');
    const toggle = element('button', 'mb-release-expand', 'See more');
    toggle.type = 'button';
    toggle.setAttribute('aria-expanded', 'false');
    toggle.setAttribute('aria-controls', editions.id);
    footer.append(toggle);
    let loaded = false;
    let pending = null;

    toggle.addEventListener('click', async () => {
      const expanded = editions.hidden;
      editions.hidden = !expanded;
      toggle.textContent = expanded ? 'See less' : 'See more';
      toggle.setAttribute('aria-expanded', String(expanded));
      if (!expanded || loaded || pending) return;
      editions.replaceChildren(element('div', 'mb-release-message', 'Loading other releases…'));
      editions.setAttribute('aria-busy', 'true');
      pending = loadGroup(base, group.id);
      try {
        const releases = await pending;
        if (generations.get(context) !== generation) return;
        const current = releases.find(release => release.id === group.release.id);
        if (current) {
          const updated = releaseChoice(current, group, select);
          representative.replaceWith(updated);
          representative = updated;
        }
        const alternatives = releases.filter(release => release.id !== group.release.id);
        editions.replaceChildren(...alternatives.map(release => releaseChoice(release, group, select)));
        if (!alternatives.length) editions.append(element('div', 'mb-release-message', 'No other releases found.'));
        loaded = true;
      } catch (failure) {
        if (generations.get(context) === generation) {
          editions.replaceChildren(element('div', 'mb-release-message mb-release-error',
            `${failure.message} Collapse and expand to retry.`));
        }
      } finally {
        pending = null;
        editions.removeAttribute('aria-busy');
      }
    });

    card.append(representative, editions, footer);
    return card;
  }

  async function search(context, container, base, title, artist, select) {
    clear(context);
    const generation = generations.get(context);
    container.replaceChildren(element('div', 'mb-release-message', 'Searching MusicBrainz…'));
    container.setAttribute('aria-busy', 'true');
    try {
      const key = JSON.stringify([base, title.trim().toLowerCase(), artist.trim().toLowerCase()]);
      const results = await cached(searches, key, maximumCachedSearches, () =>
        read(`${base}/mb-search/groups?${new URLSearchParams({ title, artist })}`));
      if (generations.get(context) !== generation) return;
      container.replaceChildren(...results.map((group, index) =>
        groupCard(base, group, context, generation, index, select)));
      if (!results.length) container.append(element('div', 'mb-release-message', 'No results found.'));
    } catch (failure) {
      if (generations.get(context) === generation) {
        container.replaceChildren(element('div', 'mb-release-message mb-release-error', failure.message));
      }
    } finally {
      if (generations.get(context) === generation) container.removeAttribute('aria-busy');
    }
  }

  root.DashboardMusicBrainzSearch = { clear, search };
})(globalThis);
