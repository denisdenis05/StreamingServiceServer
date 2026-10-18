(function (root) {
  const Policy = Object.freeze({ streamPath: '/Stream', sessionPath: '/Stream/browser-session', emptyIdentifier: '00000000-0000-0000-0000-000000000000',
    completePercentage: 100, secondsPerMinute: 60 });
  const Icons = Object.freeze({ play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5v14l11-7z" fill="currentColor"/></svg>',
    pause: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 5h4v14H7zm6 0h4v14h-4z" fill="currentColor"/></svg>' });
  function button(track, escapeHtml) {
    const available = track.available !== false && track.identifier && track.identifier !== Policy.emptyIdentifier;
    return '<button type="button" class="browser-track-play" data-browser-play="' + escapeHtml(track.identifier || '')
      + '" data-track-title="' + escapeHtml(track.title || 'Untitled track') + '" data-track-artist="' + escapeHtml(track.artist || '')
      + '" aria-label="' + escapeHtml(available ? 'Play ' + (track.title || 'track') : 'Audio file missing')
      + '" title="' + (available ? 'Play track' : 'Audio file missing') + '" aria-pressed="false"' + (available ? '' : ' disabled') + '>' + Icons.play + '</button>';
  }
  function formatTime(value) {
    if (!Number.isFinite(value) || value < 0) return '0:00';
    return Math.floor(value / Policy.secondsPerMinute) + ':' + String(Math.floor(value % Policy.secondsPerMinute)).padStart(2, '0');
  }
  function createController({ document, request, clearSession, authentication, createAudio = () => new Audio() }) {
    const elements = Object.fromEntries(['container', 'title', 'artist', 'status', 'toggle', 'progress', 'elapsed', 'duration', 'close']
      .map(name => [name, document.getElementById('browser-player-' + name)]));
    let audio = null; let track = null; let generation = 0; let sessionOperation = Promise.resolve(); let failure = false;
    function buttons() {
      for (const element of document.querySelectorAll('[data-browser-play]')) {
        if (element.disabled) continue;
        const playing = track && element.dataset.browserPlay === track.identifier && audio && !audio.paused && !audio.ended && !failure;
        element.innerHTML = playing ? Icons.pause : Icons.play;
        element.setAttribute('aria-pressed', String(Boolean(playing)));
        element.setAttribute('aria-label', (playing ? 'Pause ' : 'Play ') + (element.dataset.trackTitle || 'track'));
      }
    }
    function render(status) {
      if (status !== undefined) elements.status.textContent = status;
      const playing = audio && !audio.paused && !audio.ended && !failure;
      elements.toggle.innerHTML = playing ? Icons.pause : Icons.play;
      elements.toggle.setAttribute('aria-label', playing ? 'Pause' : 'Play');
      const duration = audio?.duration;
      const position = audio?.currentTime || 0;
      const seekable = Number.isFinite(duration) && duration > 0 && audio?.seekable?.length > 0;
      elements.progress.disabled = !seekable || failure;
      elements.progress.value = Number.isFinite(duration) && duration > 0 ? Math.min(Policy.completePercentage, position / duration * Policy.completePercentage) : 0;
      elements.elapsed.textContent = formatTime(position); elements.duration.textContent = formatTime(duration);
      buttons();
    }
    function stop() {
      generation += 1;
      const previous = audio; audio = null; track = null; failure = false;
      if (previous) { previous.pause(); previous.removeAttribute('src'); previous.load(); }
      elements.container.hidden = true; document.body.classList.remove('has-browser-player'); buttons();
    }
    function fail(message, expectedGeneration) {
      if (expectedGeneration !== generation) return;
      failure = true; audio?.pause(); render(message);
    }
    async function resume(expectedGeneration) {
      const currentAudio = audio;
      if (!currentAudio) return;
      try { await currentAudio.play(); }
      catch (error) {
        if (expectedGeneration !== generation) return;
        if (error?.name === 'NotAllowedError') render('Press play to start audio.');
        else fail('Track unavailable or this browser cannot play its format.', expectedGeneration);
      }
    }
    async function play(selected) {
      if (selected.available === false || !selected.identifier || selected.identifier === Policy.emptyIdentifier) return;
      if (track?.identifier === selected.identifier && audio && !failure) return toggle();
      stop();
      const expectedGeneration = generation; const account = authentication();
      if (!account) return;
      track = { ...selected }; elements.container.hidden = false; document.body.classList.add('has-browser-player');
      elements.title.textContent = selected.title || 'Untitled track'; elements.artist.textContent = selected.artist || '';
      render('Loading audio…');
      const operation = sessionOperation.catch(() => undefined).then(async () => {
        if (expectedGeneration !== generation || authentication() !== account) return false;
        const response = await request(Policy.sessionPath, { method: 'POST' });
        if (!response.ok) throw new Error('Playback authentication failed.');
        return true;
      });
      sessionOperation = operation;
      try {
        if (!await operation || expectedGeneration !== generation || authentication() !== account) return;
        audio = createAudio(); const currentAudio = audio; currentAudio.preload = 'metadata';
        const current = () => expectedGeneration === generation && audio === currentAudio && authentication() === account;
        for (const [event, status] of [['playing', 'Playing'], ['pause', 'Paused'], ['waiting', 'Buffering…'], ['ended', 'Finished']]) {
          currentAudio.addEventListener(event, () => { if (current()) render(status); });
        }
        for (const event of ['timeupdate', 'loadedmetadata', 'durationchange', 'progress']) currentAudio.addEventListener(event, () => { if (current()) render(); });
        currentAudio.addEventListener('error', () => { if (current()) fail('Track unavailable or this browser cannot play its format.', expectedGeneration); });
        currentAudio.src = Policy.streamPath + '?id=' + encodeURIComponent(selected.identifier) + '&allowPreview=false';
        await resume(expectedGeneration);
      } catch { fail('Could not authorize playback. Sign in and try again.', expectedGeneration); }
    }
    async function toggle() {
      if (failure && track) { const retry = track; stop(); return play(retry); }
      if (!audio) return;
      if (audio.paused || audio.ended) { if (audio.ended) audio.currentTime = 0; return resume(generation); }
      audio.pause();
    }
    elements.toggle.addEventListener('click', () => { void toggle(); });
    elements.close.addEventListener('click', stop);
    elements.progress.addEventListener('input', () => {
      if (!audio || elements.progress.disabled || !Number.isFinite(audio.duration)) return;
      const position = Math.max(0, Math.min(Policy.completePercentage, Number(elements.progress.value)));
      if (Number.isFinite(position)) { audio.currentTime = position / Policy.completePercentage * audio.duration; render(); }
    });
    document.addEventListener('click', event => {
      const element = event.target.closest?.('[data-browser-play]');
      if (element && !element.disabled) void play({ identifier: element.dataset.browserPlay, title: element.dataset.trackTitle, artist: element.dataset.trackArtist });
    });
    function authenticationChanged() {
      stop(); sessionOperation = sessionOperation.catch(() => undefined).then(() => clearSession()).catch(() => undefined);
    }
    return { play, stop, toggle, refreshButtons: buttons, authenticationChanged };
  }
  const exported = { createController, button, formatTime };
  if (typeof module !== 'undefined' && module.exports) module.exports = exported;
  else root.DashboardBrowserPlayer = exported;
})(globalThis);
