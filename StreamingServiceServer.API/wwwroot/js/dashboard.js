/* ==========================================================================
   Resonance Music Dashboard Client JavaScript
   Handles API communication, navigation, rendering carousels, authentication, and album actions.
   ========================================================================== */

const API_BASE = '/api/dashboard';

const state = {
  stats: {},
  queued: [],
  downloaded: [],
  failed: [],
  currentAlbumId: null,
  authToken: localStorage.getItem('resonance_token') || '',
  username: localStorage.getItem('resonance_user') || '',
  userRole: localStorage.getItem('resonance_role') || ''
};

function escapeHtml(str) {
  if (str === null || str === undefined) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

async function parseJsonResponse(res) {
  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch (err) {
    return { success: false, message: text || `HTTP ${res.status}` };
  }
}

document.addEventListener('DOMContentLoaded', () => {
  initApp();
});

async function initApp() {
  setupNavigation();
  setupModal();
  setupAuthForms();
  setupCarouselButtons();
  
  window.addEventListener('hashchange', handleRoute);
  
  await tryRefreshToken();
  updateAuthUI();
  await refreshData();
  handleRoute();
  
  // Periodically refresh queued downloads & stats every 10 seconds if admin
  setInterval(async () => {
    if (isAdmin()) {
      await fetchStats();
      await fetchQueued();
      renderStats();
      if (getCurrentView() === 'overview') {
        renderQueuedCarousel();
      } else if (getCurrentView() === 'queue') {
        renderQueueTable();
      }
    }
  }, 10000);
}

// ─── AUTHENTICATION & ROLE HELPERS ───

function isAuthenticated() {
  return !!state.authToken;
}

function isAdmin() {
  const role = state.userRole || parseJwtRole(state.authToken);
  return isAuthenticated() && role.toLowerCase() === 'admin';
}

function parseJwtRole(token) {
  if (!token) return '';
  try {
    const base64Url = token.split('.')[1];
    const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
    const jsonPayload = decodeURIComponent(atob(base64).split('').map(c => {
      return '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2);
    }).join(''));
    const payload = JSON.parse(jsonPayload);
    return payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] || payload['role'] || '';
  } catch {
    return '';
  }
}

function setAuthToken(token, username, role) {
  state.authToken = token || '';
  state.username = username || '';
  state.userRole = role || parseJwtRole(token) || '';

  if (token) {
    localStorage.setItem('resonance_token', token);
    localStorage.setItem('resonance_user', username);
    localStorage.setItem('resonance_role', state.userRole);
  } else {
    localStorage.removeItem('resonance_token');
    localStorage.removeItem('resonance_user');
    localStorage.removeItem('resonance_role');
  }
  updateAuthUI();
}

function handleLogout() {
  setAuthToken('', '', '');
  state.stats = {};
  state.queued = [];
  state.downloaded = [];
  state.failed = [];
  alert('signed out successfully.');
  window.location.hash = '#overview';
  handleRoute();
}

function updateAuthUI() {
  const loginLink = document.getElementById('nav-login-link');
  const userBadge = document.getElementById('header-user-badge');
  const userNameEl = document.getElementById('user-display-name');

  if (isAuthenticated()) {
    if (loginLink) loginLink.style.display = 'none';
    if (userBadge) userBadge.style.display = 'flex';
    const roleIcon = isAdmin() ? '👑' : '👤';
    if (userNameEl) userNameEl.textContent = `${roleIcon} ${state.username} (${(state.userRole || 'User').toLowerCase()})`;
  } else {
    if (loginLink) loginLink.style.display = 'inline-block';
    if (userBadge) userBadge.style.display = 'none';
  }
}

async function tryRefreshToken() {
  try {
    const res = await fetch('/Authentication/refresh-token', { method: 'POST' });
    if (res.ok) {
      const data = await res.json();
      if (data.token) {
        setAuthToken(data.token, state.username || 'user', data.role);
      }
    }
  } catch (err) {
    // Ignore refresh errors on initial load
  }
}

function authFetch(url, options = {}) {
  options.headers = options.headers || {};
  if (state.authToken) {
    options.headers['Authorization'] = `Bearer ${state.authToken}`;
  }
  return fetch(url, options).then(async (response) => {
    if (response.status === 401 || response.status === 403) {
      if (response.status === 403) {
        alert('Access Denied: Only Admin users are authorized to view or manage library data.');
      } else {
        openAuthModal();
      }
      throw new Error('Authentication/Authorization required');
    }
    return response;
  });
}

function requireAdminAuth(actionCallback) {
  if (!isAuthenticated()) {
    openAuthModal();
    return false;
  }
  if (!isAdmin()) {
    alert('Access Denied: Only Admin users are authorized to perform download & library actions.');
    return false;
  }
  actionCallback();
  return true;
}

function openAuthModal() {
  const modal = document.getElementById('auth-prompt-modal');
  if (modal) modal.style.display = 'flex';
}

function closeAuthModal() {
  const modal = document.getElementById('auth-prompt-modal');
  if (modal) modal.style.display = 'none';
}

function renderAdminOnlyNotice(message = "Admin authentication required to view library, queue, and download statistics.") {
  return `
    <div style="padding: 3rem 2rem; text-align: center; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 8px; margin: 1.5rem 0; width: 100%;">
      <div style="font-size: 2.2rem; margin-bottom: 0.6rem;">🔒</div>
      <h3 style="font-size: 1.2rem; margin-bottom: 0.4rem;">admin access <i>required</i></h3>
      <p style="color: var(--text-muted); font-size: 0.9rem; max-width: 440px; margin: 0 auto 1.2rem auto;">
        ${message}
      </p>
      <button onclick="openAuthModal()" class="btn-cta">sign in as admin</button>
    </div>
  `;
}

// ─── ROUTING & VIEW NAVIGATION ───

function getCurrentView() {
  const hash = window.location.hash || '#overview';
  if (hash.startsWith('#album')) return 'album';
  if (hash.startsWith('#queue')) return 'queue';
  if (hash.startsWith('#failed')) return 'failed';
  if (hash.startsWith('#downloaded')) return 'downloaded';
  if (hash.startsWith('#playlists')) return 'playlists';
  if (hash.startsWith('#analysis')) return 'analysis';
  if (hash.startsWith('#settings')) return 'settings';
  if (hash.startsWith('#login')) return 'login';
  return 'overview';
}

function handleRoute() {
  const hash = window.location.hash || '#overview';
  
  document.querySelectorAll('.page-view').forEach(el => el.classList.remove('active'));
  document.querySelectorAll('.nav-link').forEach(el => el.classList.remove('active'));
  
  if (hash.startsWith('#album')) {
    const params = new URLSearchParams(hash.split('?')[1]);
    const albumId = params.get('id');
    if (albumId) {
      loadAlbumDetail(albumId);
    }
    document.getElementById('view-album-detail').classList.add('active');
  } else if (hash === '#queue') {
    document.getElementById('view-queue').classList.add('active');
    document.querySelector('.nav-link[href="#queue"]')?.classList.add('active');
    renderQueueTable();
  } else if (hash === '#downloaded') {
    document.getElementById('view-downloaded').classList.add('active');
    document.querySelector('.nav-link[href="#downloaded"]')?.classList.add('active');
    renderDownloadedGrid();
  } else if (hash === '#failed') {
    document.getElementById('view-failed').classList.add('active');
    document.querySelector('.nav-link[href="#failed"]')?.classList.add('active');
    renderFailedTable();
  } else if (hash === '#playlists') {
    document.getElementById('view-playlists').classList.add('active');
    document.querySelector('.nav-link[href="#playlists"]')?.classList.add('active');
    renderPlaylistsView();
  } else if (hash === '#analysis') {
    document.getElementById('view-analysis').classList.add('active');
    document.querySelector('.nav-link[href="#analysis"]')?.classList.add('active');
    fetchAnalysisData();
  } else if (hash === '#settings') {
    document.getElementById('view-settings').classList.add('active');
    document.querySelector('.nav-link[href="#settings"]')?.classList.add('active');
    loadProviderSettings();
  } else if (hash === '#login') {
    document.getElementById('view-login').classList.add('active');
    document.querySelector('.nav-link[href="#login"]')?.classList.add('active');
  } else {
    document.getElementById('view-overview').classList.add('active');
    document.querySelector('.nav-link[href="#overview"]')?.classList.add('active');
    renderOverview();
  }

  window.scrollTo({ top: 0, behavior: 'smooth' });
}

async function refreshData() {
  if (!isAdmin()) {
    state.stats = {};
    state.queued = [];
    state.downloaded = [];
    state.failed = [];
    renderStats();
    return;
  }

  try {
    const [statsRes, queuedRes, downloadedRes, failedRes] = await Promise.all([
      authFetch(`${API_BASE}/stats`),
      authFetch(`${API_BASE}/queued`),
      authFetch(`${API_BASE}/downloaded`),
      authFetch(`${API_BASE}/failed`)
    ]);

    if (statsRes.ok) state.stats = await statsRes.json();
    if (queuedRes.ok) state.queued = await queuedRes.json();
    if (downloadedRes.ok) state.downloaded = await downloadedRes.json();
    if (failedRes.ok) state.failed = await failedRes.json();
  } catch (err) {
    console.error('Failed to fetch dashboard data:', err);
  }
  renderStats();
}

async function fetchStats() {
  if (!isAdmin()) return;
  try {
    const res = await authFetch(`${API_BASE}/stats`);
    if (res.ok) state.stats = await res.json();
  } catch (err) {
    console.error('Failed to fetch stats:', err);
  }
}

async function fetchQueued() {
  if (!isAdmin()) return;
  try {
    const res = await authFetch(`${API_BASE}/queued`);
    if (res.ok) state.queued = await res.json();
  } catch (err) {
    console.error('Failed to fetch queued albums:', err);
  }
}

async function fetchDownloaded(search = '') {
  if (!isAdmin()) return;
  try {
    const url = search ? `${API_BASE}/downloaded?search=${encodeURIComponent(search)}` : `${API_BASE}/downloaded`;
    const res = await authFetch(url);
    if (res.ok) state.downloaded = await res.json();
  } catch (err) {
    console.error('Failed to fetch downloaded albums:', err);
  }
}

function renderStats() {
  document.getElementById('stat-queued').textContent = isAdmin() ? (state.stats.queuedCount ?? 0) : '—';
  document.getElementById('stat-downloading').textContent = isAdmin() ? (state.stats.activeTorrents ?? 0) : '—';
  document.getElementById('stat-downloaded').textContent = isAdmin() ? (state.stats.downloadedCount ?? 0) : '—';
  document.getElementById('stat-recordings').textContent = isAdmin() ? (state.stats.recordingsCount ?? 0) : '—';
}

function renderOverview() {
  renderQueuedCarousel();
  renderDownloadedCarousel();
}

function renderQueuedCarousel() {
  const track = document.getElementById('queued-carousel-track');
  if (!track) return;
  
  if (!isAdmin()) {
    track.innerHTML = renderAdminOnlyNotice("Sign in with an admin account to view active and queued downloads.");
    return;
  }

  if (state.queued.length === 0) {
    track.innerHTML = `
      <div style="padding: 2rem; color: var(--text-muted); font-size: 0.95rem;">
        no albums in download pipeline right now.
      </div>
    `;
    return;
  }
  
  track.innerHTML = state.queued.map(item => {
    let badgeClass = 'badge-ochre';
    let statusLabel = item.status;
    
    if (item.status === 'downloading') {
      badgeClass = 'badge-terracotta';
      statusLabel = `${Math.round(item.progress * 100)}% downloaded`;
    } else if (item.status === 'failed') {
      badgeClass = 'badge-slate';
      statusLabel = 'failed';
    } else {
      statusLabel = 'queued';
    }

    const coverUrl = `${API_BASE}/placeholder-cover`;

    return `
      <a href="#album?id=${item.id}" class="album-card">
        <div class="album-cover-wrapper">
          <img src="${coverUrl}" alt="${item.title}" class="album-cover-img" />
          <span class="album-badge ${badgeClass}">${statusLabel}</span>
        </div>
        <div class="album-info">
          <div class="album-title">${item.title}</div>
          <div class="album-artist">${item.artist}</div>
          ${item.status === 'downloading' ? `
            <div class="progress-bar-bg">
              <div class="progress-bar-fill" style="width: ${Math.round(item.progress * 100)}%"></div>
            </div>
          ` : ''}
        </div>
      </a>
    `;
  }).join('');
}

function renderDownloadedCarousel() {
  const track = document.getElementById('downloaded-carousel-track');
  if (!track) return;

  if (!isAdmin()) {
    track.innerHTML = renderAdminOnlyNotice("Sign in with an admin account to view downloaded releases in your library.");
    return;
  }

  if (state.downloaded.length === 0) {
    track.innerHTML = `
      <div style="padding: 2rem; color: var(--text-muted); font-size: 0.95rem;">
        no downloaded albums in library yet.
      </div>
    `;
    return;
  }

  track.innerHTML = state.downloaded.map(item => {
    return `
      <a href="#album?id=${item.id}" class="album-card">
        <div class="album-cover-wrapper">
          <img src="${item.cover}" alt="${item.title}" class="album-cover-img" onError="this.src='${API_BASE}/placeholder-cover'" />
          <span class="album-badge badge-olive">downloaded</span>
        </div>
        <div class="album-info">
          <div class="album-title">${item.title}</div>
          <div class="album-artist">${item.artistName}</div>
          <div class="album-meta">
            <span>${item.recordingsCount} tracks</span>
          </div>
        </div>
      </a>
    `;
  }).join('');
}

function renderQueueTable() {
  const container = document.getElementById('queue-table-body');
  if (!container) return;

  if (!isAdmin()) {
    container.innerHTML = `<tr><td colspan="5">${renderAdminOnlyNotice("Admin authentication required to view download queue.")}</td></tr>`;
    return;
  }

  if (state.queued.length === 0) {
    container.innerHTML = `
      <tr>
        <td colspan="5" style="text-align: center; color: var(--text-muted); padding: 3rem;">
          download queue is currently empty.
        </td>
      </tr>
    `;
    return;
  }

  container.innerHTML = state.queued.map(item => {
    return `
      <tr onclick="window.location.hash='#album?id=${item.id}'">
        <td>
          <strong>${item.title}</strong>
        </td>
        <td>${item.artist}</td>
        <td>
          <span class="album-badge ${item.status === 'downloading' ? 'badge-terracotta' : (item.status === 'failed' ? 'badge-slate' : 'badge-ochre')}">
            ${item.status} ${item.status === 'downloading' ? `(${Math.round(item.progress * 100)}%)` : ''}
          </span>
        </td>
        <td>${item.sourceName || '—'}</td>
        <td onclick="event.stopPropagation()">
          <button onclick="handleRequeue('${item.id}')" class="btn-secondary" style="padding: 0.3rem 0.7rem; font-size: 0.8rem;">requeue</button>
          <button onclick="handleSetDownloaded('${item.id}')" class="btn-secondary" style="padding: 0.3rem 0.7rem; font-size: 0.8rem; margin-left: 0.3rem;">set downloaded</button>
        </td>
      </tr>
    `;
  }).join('');
}

function renderFailedTable() {
  const container = document.getElementById('failed-table-body');
  if (!container) return;

  if (!isAdmin()) {
    container.innerHTML = `<tr><td colspan="4">${renderAdminOnlyNotice("Admin authentication required to view failed download releases.")}</td></tr>`;
    return;
  }

  const failedItems = state.failed || [];
  if (failedItems.length === 0) {
    container.innerHTML = `
      <tr>
        <td colspan="4" style="text-align: center; color: var(--text-muted); padding: 3rem;">
          no failed downloads recorded.
        </td>
      </tr>
    `;
    return;
  }

  container.innerHTML = failedItems.map(item => {
    return `
      <tr onclick="window.location.hash='#album?id=${item.id}'">
        <td>
          <strong>${item.title}</strong>
        </td>
        <td>${item.artist}</td>
        <td>
          <span class="album-badge badge-terracotta">failed</span>
        </td>
        <td onclick="event.stopPropagation()">
          <button onclick="handleRequeue('${item.id}')" class="btn-cta" style="padding: 0.3rem 0.7rem; font-size: 0.8rem;">requeue</button>
          <button onclick="handleDeleteAlbum('${item.id}')" class="btn-secondary" style="padding: 0.3rem 0.7rem; font-size: 0.8rem; margin-left: 0.3rem;">delete</button>
        </td>
      </tr>
    `;
  }).join('');
}

function renderDownloadedGrid() {
  const container = document.getElementById('downloaded-grid-container');
  if (!container) return;

  if (!isAdmin()) {
    container.innerHTML = renderAdminOnlyNotice("Admin authentication required to view album library collection.");
    return;
  }

  if (state.downloaded.length === 0) {
    container.innerHTML = `
      <div style="padding: 3rem; text-align: center; color: var(--text-muted);">
        no downloaded albums found matching criteria.
      </div>
    `;
    return;
  }

  container.innerHTML = `
    <div style="display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 2rem;">
      ${state.downloaded.map(item => `
        <a href="#album?id=${item.id}" class="album-card" style="flex: auto;">
          <div class="album-cover-wrapper">
            <img src="${item.cover}" alt="${item.title}" class="album-cover-img" onError="this.src='${API_BASE}/placeholder-cover'" />
            <span class="album-badge badge-olive">downloaded</span>
          </div>
          <div class="album-info">
            <div class="album-title">${item.title}</div>
            <div class="album-artist">${item.artistName}</div>
            <div class="album-meta">
              <span>${item.recordingsCount} tracks</span>
            </div>
          </div>
        </a>
      `).join('')}
    </div>
  `;
}

async function loadAlbumDetail(id) {
  state.currentAlbumId = id;
  const container = document.getElementById('album-detail-content');
  if (!container) return;

  if (!isAdmin()) {
    container.innerHTML = renderAdminOnlyNotice("Admin authentication required to view album details.");
    return;
  }

  container.innerHTML = `<div style="padding: 3rem; text-align: center; color: var(--text-muted);">loading album details...</div>`;

  try {
    const res = await authFetch(`${API_BASE}/album/${id}`);
    if (!res.ok) {
      container.innerHTML = `<div style="padding: 3rem; color: var(--accent-terracotta);">Album not found.</div>`;
      return;
    }

    const album = await res.json();
    renderAlbumDetail(album);
  } catch (err) {
    container.innerHTML = renderAdminOnlyNotice("Admin authentication required to view album details.");
  }
}

function renderAlbumDetail(album) {
  const container = document.getElementById('album-detail-content');
  const coverUrl = album.cover || `${API_BASE}/placeholder-cover`;
  const badgeClass = album.isDownloaded ? 'badge-olive' : (album.status === 'downloading' ? 'badge-terracotta' : 'badge-ochre');
  const statusText = album.isDownloaded ? 'downloaded' : (album.status || 'queued');

  let recordingsHtml = '';
  if (album.isDownloaded && album.recordings && album.recordings.length > 0) {
    recordingsHtml = `
      <h3 style="margin-top: 2rem; margin-bottom: 1rem;">track listing (${album.recordings.length})</h3>
      <ul class="track-list">
        ${album.recordings.map((track, i) => {
          const isMissing = track.fileExists === false;
          return `
          <li class="track-item" style="${isMissing ? 'background-color: rgba(198, 40, 40, 0.06); border-left: 3px solid #c62828; padding-left: 0.8rem;' : ''}">
            <span class="track-number">${track.position || i + 1}</span>
            <span class="track-name" style="display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap;">
              ${escapeHtml(track.title)}
              ${isMissing ? `<span class="badge badge-terracotta" style="font-size: 0.75rem; padding: 0.15rem 0.45rem; border-radius: 4px; background: #ffebee; color: #c62828; border: 1px solid #ffcdd2;" title="Audio file missing on filesystem">⚠️ missing file</span>` : ''}
            </span>
            <span style="font-size: 0.85rem; color: var(--text-muted);">${track.length ? Math.floor(track.length / 60) + ':' + String(track.length % 60).padStart(2, '0') : ''}</span>
          </li>
        `;
        }).join('')}
      </ul>
    `;
  } else {
    recordingsHtml = `
      <div style="margin-top: 2rem; padding: 1.5rem; background-color: var(--bg-subtle); border: 1px dashed var(--border-light); color: var(--text-muted); border-radius: 4px;">
        no track recordings indexed yet. upload audio files below to populate this album.
      </div>
    `;
  }

  container.innerHTML = `
    <div style="margin-bottom: 2rem;">
      <a href="javascript:history.back()" class="nav-link">&larr; back</a>
    </div>

    <div class="album-detail-grid">
      <div>
        <img src="${coverUrl}" class="album-detail-cover" onError="this.src='${API_BASE}/placeholder-cover'" />
      </div>
      <div>
        <span class="album-badge ${badgeClass}" style="position: relative; top: auto; right: auto; display: inline-block; margin-bottom: 0.8rem;">
          ${statusText}
        </span>
        <h1 style="font-size: 2.8rem;">${album.title}</h1>
        <div class="album-detail-artist">by ${album.artist}</div>

        <div class="album-actions-toolbar">
          <button onclick="handleRequeue('${album.id}')" class="btn-secondary">
            requeue download
          </button>
          ${!album.isDownloaded ? `
            <button onclick="handleSetDownloaded('${album.id}')" class="btn-cta" style="background-color: var(--accent-olive);">
              mark as downloaded
            </button>
          ` : ''}
          <button onclick="handleDeleteAlbum('${album.id}')" class="btn-secondary" style="color: var(--accent-terracotta); border-color: var(--accent-terracotta);">
            remove album
          </button>
        </div>

        <!-- Replace Audio Uploader -->
        <div style="margin-top: 2.5rem;">
          <h3>replace or add audio files</h3>
          <p style="font-size: 0.9rem; color: var(--text-muted); margin-top: 0.3rem;">
            upload custom audio files (.mp3, .flac, .m4a) to replace existing recordings for this album.
          </p>
          <div id="dropzone" class="uploader-box">
            <div class="uploader-title">drag & drop audio files here</div>
            <div class="uploader-hint">or click to browse local files</div>
            <input type="file" id="audio-file-input" multiple accept="audio/*" style="display: none;" />
          </div>
          <div id="upload-status" style="margin-top: 0.8rem; font-size: 0.9rem;"></div>
        </div>

        ${recordingsHtml}
      </div>
    </div>
  `;

  setupDropzone(album.id);
}

function setupDropzone(albumId) {
  const dropzone = document.getElementById('dropzone');
  const fileInput = document.getElementById('audio-file-input');
  const statusDiv = document.getElementById('upload-status');
  if (!dropzone || !fileInput) return;

  dropzone.addEventListener('click', () => {
    requireAdminAuth(() => fileInput.click());
  });

  dropzone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropzone.classList.add('dragover');
  });

  dropzone.addEventListener('dragleave', () => {
    dropzone.classList.remove('dragover');
  });

  dropzone.addEventListener('drop', (e) => {
    e.preventDefault();
    dropzone.classList.remove('dragover');
    if (e.dataTransfer.files.length > 0) {
      uploadAudioFiles(albumId, e.dataTransfer.files);
    }
  });

  fileInput.addEventListener('change', () => {
    if (fileInput.files.length > 0) {
      uploadAudioFiles(albumId, fileInput.files);
    }
  });
}

async function uploadAudioFiles(albumId, files) {
  if (!requireAdminAuth(() => {})) return;

  const statusDiv = document.getElementById('upload-status');
  if (statusDiv) statusDiv.innerHTML = `<span style="color: var(--accent-ochre);">uploading ${files.length} file(s)...</span>`;

  const formData = new FormData();
  for (let i = 0; i < files.length; i++) {
    formData.append('files', files[i]);
  }

  try {
    const res = await authFetch(`${API_BASE}/album/${albumId}/replace-audio`, {
      method: 'POST',
      body: formData
    });

    if (res.ok) {
      const data = await res.json().catch(() => ({}));
      if (statusDiv) statusDiv.innerHTML = `<span style="color: var(--accent-olive);">${data.message || 'audio files successfully updated!'}</span>`;
      await refreshData();
      setTimeout(() => loadAlbumDetail(albumId), 1000);
    } else {
      const errData = await res.json().catch(() => ({}));
      const errMsg = errData.message || 'failed to upload audio files.';
      if (statusDiv) statusDiv.innerHTML = `<span style="color: var(--accent-terracotta);">${errMsg}</span>`;
    }
  } catch (err) {
    if (statusDiv) statusDiv.innerHTML = `<span style="color: var(--accent-terracotta);">error uploading files: ${err.message}</span>`;
  }
}

async function handleRequeue(id) {
  if (!requireAdminAuth(() => {})) return;

  const confirmMsg = "⚠️ PERMANENTLY DELETE & REQUEUE ALBUM?\n\n" +
    "This action will:\n" +
    "1. Delete all existing physical audio files from disk.\n" +
    "2. Remove album track recordings & metadata from your library.\n" +
    "3. Re-add the album to the download queue for fresh scraping.\n\n" +
    "Are you sure you want to delete existing media and requeue?";

  if (!confirm(confirmMsg)) return;

  try {
    const res = await authFetch(`${API_BASE}/album/${id}/requeue`, { method: 'POST' });
    if (res.ok) {
      alert('album media deleted and re-queued for fresh download.');
      await refreshData();
      if (getCurrentView() === 'album') loadAlbumDetail(id);
      else if (getCurrentView() === 'queue') renderQueueTable();
      else if (getCurrentView() === 'failed') renderFailedTable();
    } else {
      const data = await res.json().catch(() => ({}));
      alert(`failed to requeue: ${data.message || 'unknown error'}`);
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      alert('failed to requeue album.');
    }
  }
}

async function requeueAllFailed() {
  if (!requireAdminAuth(() => {})) return;

  const confirmMsg = "↻ REQUEUE ALL FAILED DOWNLOADS?\n\n" +
    "This will move all failed albums back into the active download queue for fresh multi-provider scraping.\n\n" +
    "Proceed with requeueing all failed items?";

  if (!confirm(confirmMsg)) return;

  try {
    const res = await authFetch(`${API_BASE}/failed/requeue-all`, { method: 'POST' });
    if (res.ok) {
      const data = await res.json().catch(() => ({}));
      alert(data.message || 'All failed downloads requeued.');
      await refreshData();
      if (getCurrentView() === 'failed') renderFailedTable();
      else if (getCurrentView() === 'queue') renderQueueTable();
    } else {
      const data = await res.json().catch(() => ({}));
      alert(`Failed to requeue all: ${data.message || 'Unknown error'}`);
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      alert('Error requeueing all failed downloads.');
    }
  }
}

async function requeueAllActiveQueue() {
  if (!requireAdminAuth(() => {})) return;

  const confirmMsg = "⚠️ REQUEUE ALL ACTIVE QUEUED & PENDING ITEMS?\n\n" +
    "This will reset all active transfers (Soulseek & Torrents) and re-add all queue items back to the download pipeline for fresh multi-provider scraping.\n\n" +
    "Proceed with requeueing all active queue items?";

  if (!confirm(confirmMsg)) return;

  try {
    const res = await authFetch(`${API_BASE}/queue/requeue-all`, { method: 'POST' });
    if (res.ok) {
      const data = await res.json().catch(() => ({}));
      alert(data.message || 'All queue items requeued.');
      await refreshData();
      renderQueueTable();
    } else {
      const data = await res.json().catch(() => ({}));
      alert(`Failed to requeue all active queue: ${data.message || 'Unknown error'}`);
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      alert('Error requeueing active queue.');
    }
  }
}

async function handleSetDownloaded(id) {
  if (!requireAdminAuth(() => {})) return;

  try {
    const res = await authFetch(`${API_BASE}/album/${id}/set-downloaded`, { method: 'POST' });
    if (res.ok) {
      alert('album marked as downloaded.');
      await refreshData();
      if (getCurrentView() === 'album') loadAlbumDetail(id);
      else if (getCurrentView() === 'queue') renderQueueTable();
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      alert('failed to set album as downloaded.');
    }
  }
}

async function handleDeleteAlbum(id) {
  if (!requireAdminAuth(() => {})) return;

  if (!confirm('are you sure you want to remove this album?')) return;
  try {
    const res = await authFetch(`${API_BASE}/album/${id}`, { method: 'DELETE' });
    if (res.ok) {
      alert('album removed.');
      await refreshData();
      window.location.hash = '#overview';
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      alert('failed to remove album.');
    }
  }
}

function setupNavigation() {
  const searchInput = document.getElementById('search-downloaded-input');
  if (searchInput) {
    searchInput.addEventListener('input', (e) => {
      fetchDownloaded(e.target.value).then(() => renderDownloadedGrid());
    });
  }
}

function setupCarouselButtons() {
  document.querySelectorAll('.carousel-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const targetId = btn.getAttribute('data-target');
      const direction = btn.getAttribute('data-dir');
      const track = document.getElementById(targetId);
      if (track) {
        const scrollAmount = direction === 'left' ? -300 : 300;
        track.scrollBy({ left: scrollAmount, behavior: 'smooth' });
      }
    });
  });
}

function setupModal() {
  const modal = document.getElementById('add-album-modal');
  const openBtn = document.getElementById('btn-open-add-modal');
  const closeBtn = document.getElementById('btn-close-modal');
  const form = document.getElementById('add-album-form');

  if (openBtn && modal) {
    openBtn.addEventListener('click', () => {
      requireAdminAuth(() => {
        modal.style.display = 'flex';
      });
    });
  }

  if (closeBtn && modal) {
    closeBtn.addEventListener('click', () => modal.style.display = 'none');
  }

  if (modal) {
    modal.addEventListener('click', (e) => {
      if (e.target === modal) modal.style.display = 'none';
    });
  }

  if (form) {
    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      if (!requireAdminAuth(() => {})) return;

      const title = document.getElementById('input-album-title').value;
      const artist = document.getElementById('input-album-artist').value;

      try {
        const res = await authFetch(`${API_BASE}/album/add`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ title, artist })
        });

        if (res.ok) {
          modal.style.display = 'none';
          form.reset();
          await refreshData();
          alert('album added to queue!');
          if (getCurrentView() === 'overview') renderOverview();
          else if (getCurrentView() === 'queue') renderQueueTable();
        }
      } catch (err) {
        if (err.message !== 'Authentication/Authorization required') {
          alert('failed to add album to queue.');
        }
      }
    });
  }
}

// ─── AUTHENTICATION FORMS & HANDLERS ───

let isPageRegisterMode = false;

function togglePageAuthMode() {
  isPageRegisterMode = !isPageRegisterMode;
  const title = document.getElementById('auth-page-title');
  const subtitle = document.getElementById('auth-page-subtitle');
  const userGroup = document.getElementById('auth-page-username-group');
  const labelId = document.getElementById('label-page-identifier');
  const submitBtn = document.getElementById('btn-page-auth-submit');
  const togglePrompt = document.getElementById('auth-page-toggle-prompt');
  const toggleBtn = document.getElementById('btn-page-auth-toggle');

  if (isPageRegisterMode) {
    if (title) title.innerHTML = 'create a <i>resonance account</i>';
    if (subtitle) subtitle.textContent = 'register to manage library releases and configuration.';
    if (userGroup) userGroup.style.display = 'block';
    if (labelId) labelId.textContent = 'email address';
    if (submitBtn) submitBtn.textContent = 'create account';
    if (togglePrompt) togglePrompt.textContent = 'already have an account?';
    if (toggleBtn) toggleBtn.textContent = 'sign in';
  } else {
    if (title) title.innerHTML = 'sign in to <i>resonance</i>';
    if (subtitle) subtitle.textContent = 'authenticate to manage download queues, requeue releases, or update provider settings.';
    if (userGroup) userGroup.style.display = 'none';
    if (labelId) labelId.textContent = 'email or username';
    if (submitBtn) submitBtn.textContent = 'sign in';
    if (togglePrompt) togglePrompt.textContent = "don't have an account?";
    if (toggleBtn) toggleBtn.textContent = 'register new account';
  }
}

function setupAuthForms() {
  const pageForm = document.getElementById('auth-page-form');
  const modalForm = document.getElementById('auth-modal-form');

  if (pageForm) {
    pageForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const statusEl = document.getElementById('auth-page-status');
      if (statusEl) statusEl.innerHTML = `<span style="color: var(--accent-ochre);">authenticating...</span>`;

      const username = document.getElementById('input-page-username').value;
      const identifier = document.getElementById('input-page-identifier').value;
      const password = document.getElementById('input-page-password').value;
      const remember = document.getElementById('input-page-remember').checked;

      try {
        await executeAuthRequest(isPageRegisterMode, identifier, password, username, remember);
        if (statusEl) statusEl.innerHTML = `<span style="color: var(--accent-olive);">successfully authenticated!</span>`;
        setTimeout(() => {
          window.location.hash = '#overview';
          refreshData().then(() => handleRoute());
        }, 800);
      } catch (err) {
        if (statusEl) statusEl.innerHTML = `<span style="color: var(--accent-terracotta);">${err.message}</span>`;
      }
    });
  }

  if (modalForm) {
    modalForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const statusEl = document.getElementById('auth-modal-status');
      if (statusEl) statusEl.innerHTML = `<span style="color: var(--accent-ochre);">authenticating...</span>`;

      const username = document.getElementById('input-modal-username')?.value || '';
      const identifier = document.getElementById('input-modal-identifier').value;
      const password = document.getElementById('input-modal-password').value;

      try {
        await executeAuthRequest(false, identifier, password, username, true);
        if (statusEl) statusEl.innerHTML = `<span style="color: var(--accent-olive);">successfully authenticated!</span>`;
        setTimeout(() => {
          closeAuthModal();
          refreshData().then(() => handleRoute());
        }, 800);
      } catch (err) {
        if (statusEl) statusEl.innerHTML = `<span style="color: var(--accent-terracotta);">${err.message}</span>`;
      }
    });
  }
}

async function executeAuthRequest(isRegister, identifier, password, username, remember) {
  const url = isRegister ? '/Authentication/register' : '/Authentication/login';
  const payload = isRegister
    ? { email: identifier, username: username || identifier.split('@')[0], password: password }
    : { emailOrUsername: identifier, password: password, keepMeLoggedIn: remember };

  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });

  if (!res.ok) {
    throw new Error(isRegister ? 'Registration failed. User or email may already exist.' : 'Invalid email/username or password.');
  }

  const data = await res.json();
  if (data.token) {
    setAuthToken(data.token, data.username || username || identifier, data.role);
  }
  return data;
}

/* ==========================================================================
   SETTINGS PAGE — Provider Configuration
   ========================================================================== */

let providerSettingsCache = [];

async function loadProviderSettings() {
  const container = document.getElementById('provider-cards-container');

  if (!isAdmin()) {
    if (container) container.innerHTML = renderAdminOnlyNotice("Admin authentication required to view download provider settings.");
    return;
  }

  try {
    const res = await authFetch(`${API_BASE}/settings/providers`);
    if (res.ok) {
      providerSettingsCache = await res.json();
      renderProviderPriorityList();
      renderProviderCards();
    }
  } catch (err) {
    console.error('Failed to load provider settings:', err);
    if (container) container.innerHTML = renderAdminOnlyNotice("Admin authentication required to view download provider settings.");
  }
}

function renderProviderPriorityList() {
  const list = document.getElementById('provider-priority-list');
  if (!list) return;

  if (!isAdmin()) {
    list.innerHTML = renderAdminOnlyNotice("Admin authentication required.");
    return;
  }

  const sorted = [...providerSettingsCache].sort((a, b) => a.priorityOrder - b.priorityOrder);

  list.innerHTML = sorted.map((p, i) => `
    <div class="priority-item ${p.isEnabled ? '' : 'priority-disabled'}" data-id="${p.id}" draggable="true">
      <div class="priority-handle">≡</div>
      <div class="priority-info">
        <span class="priority-rank">#${i + 1}</span>
        <span class="priority-name">${p.providerName}</span>
        <span class="priority-badge ${p.isEnabled ? 'badge-olive' : 'badge-muted'}">${p.isEnabled ? 'active' : 'disabled'}</span>
      </div>
      <label class="toggle-switch">
        <input type="checkbox" ${p.isEnabled ? 'checked' : ''} onchange="toggleProvider('${p.id}', this.checked)" />
        <span class="toggle-slider"></span>
      </label>
    </div>
  `).join('');

  setupDragReorder(list);
}

function setupDragReorder(list) {
  let dragEl = null;

  list.addEventListener('dragstart', (e) => {
    dragEl = e.target.closest('.priority-item');
    if (dragEl) dragEl.classList.add('dragging');
  });

  list.addEventListener('dragover', (e) => {
    e.preventDefault();
    const target = e.target.closest('.priority-item');
    if (target && target !== dragEl) {
      const rect = target.getBoundingClientRect();
      const midY = rect.top + rect.height / 2;
      if (e.clientY < midY) {
        list.insertBefore(dragEl, target);
      } else {
        list.insertBefore(dragEl, target.nextSibling);
      }
    }
  });

  list.addEventListener('dragend', () => {
    if (dragEl) dragEl.classList.remove('dragging');
    dragEl = null;
    const items = list.querySelectorAll('.priority-item');
    items.forEach((item, i) => {
      const id = item.dataset.id;
      const config = providerSettingsCache.find(p => p.id === id);
      if (config) config.priorityOrder = i + 1;
      const rankEl = item.querySelector('.priority-rank');
      if (rankEl) rankEl.textContent = `#${i + 1}`;
    });
  });
}

function toggleProvider(id, enabled) {
  if (!requireAdminAuth(() => {})) return;
  const config = providerSettingsCache.find(p => p.id === id);
  if (config) {
    config.isEnabled = enabled;
    renderProviderPriorityList();
    renderProviderCards();
  }
}

function renderProviderCards() {
  const container = document.getElementById('provider-cards-container');
  if (!container) return;

  if (!isAdmin()) {
    container.innerHTML = renderAdminOnlyNotice("Admin authentication required.");
    return;
  }

  container.innerHTML = providerSettingsCache.map(p => {
    const isSoulseek = p.providerName === 'Soulseek';
    const accentClass = isSoulseek ? 'accent-ochre' : 'accent-olive';
    const icon = isSoulseek ? '♫' : '▼';

    return `
      <div class="provider-card ${p.isEnabled ? '' : 'provider-card-disabled'}">
        <div class="provider-card-header">
          <span class="provider-icon ${accentClass}">${icon}</span>
          <h3>${p.providerName.toLowerCase()} <i>configuration</i></h3>
        </div>
        <div class="provider-card-body">
          <div class="form-group">
            <label class="form-label">${isSoulseek ? 'slskd api url' : 'qbittorrent url'}</label>
            <input type="text" class="form-input" value="${p.apiUrl || ''}" 
                   onchange="updateProviderField('${p.id}', 'apiUrl', this.value)" 
                   placeholder="${isSoulseek ? 'http://localhost:5030' : 'http://localhost:8080'}" />
          </div>
          ${isSoulseek ? `
            <div class="form-group">
              <label class="form-label">api key</label>
              <input type="password" class="form-input" value="${p.apiKey || ''}" 
                     onchange="updateProviderField('${p.id}', 'apiKey', this.value)" 
                     placeholder="your slskd api key" />
            </div>
            <div class="form-group">
              <label class="form-label">web UI username <i>(optional fallback)</i></label>
              <input type="text" class="form-input" value="${p.username || ''}" 
                     onchange="updateProviderField('${p.id}', 'username', this.value)" placeholder="e.g. slskd" />
            </div>
            <div class="form-group">
              <label class="form-label">web UI password <i>(optional fallback)</i></label>
              <input type="password" class="form-input" value="${p.password || ''}" 
                     onchange="updateProviderField('${p.id}', 'password', this.value)" placeholder="••••••••" />
            </div>
          ` : `
            <div class="form-group">
              <label class="form-label">username</label>
              <input type="text" class="form-input" value="${p.username || ''}" 
                     onchange="updateProviderField('${p.id}', 'username', this.value)" />
            </div>
            <div class="form-group">
              <label class="form-label">password</label>
              <input type="password" class="form-input" value="${p.password || ''}" 
                     onchange="updateProviderField('${p.id}', 'password', this.value)" />
            </div>
          `}
          <div style="margin-top: 1rem;">
            <button class="btn-secondary" onclick="testProviderConnection('${p.id}')">test connection</button>
            <span id="test-result-${p.id}" style="margin-left: 1rem; font-size: 0.85rem;"></span>
          </div>
        </div>
      </div>
    `;
  }).join('');
}

function updateProviderField(id, field, value) {
  if (!requireAdminAuth(() => {})) return;
  const config = providerSettingsCache.find(p => p.id === id);
  if (config) config[field] = value;
}

async function saveProviderSettings() {
  if (!requireAdminAuth(() => {})) return;

  const statusDiv = document.getElementById('settings-status');
  try {
    const payload = providerSettingsCache.map(p => ({
      id: p.id,
      priorityOrder: p.priorityOrder,
      isEnabled: p.isEnabled,
      apiUrl: p.apiUrl,
      username: p.username,
      password: p.password,
      apiKey: p.apiKey,
      formatPreferences: p.formatPreferences,
      extraConfig: p.extraConfig
    }));

    const res = await authFetch(`${API_BASE}/settings/providers`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    if (res.ok) {
      if (statusDiv) statusDiv.innerHTML = `<span style="color: var(--accent-olive);">settings saved successfully.</span>`;
      setTimeout(() => { if (statusDiv) statusDiv.innerHTML = ''; }, 3000);
    } else {
      if (statusDiv) statusDiv.innerHTML = `<span style="color: var(--accent-terracotta);">failed to save settings.</span>`;
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      if (statusDiv) statusDiv.innerHTML = `<span style="color: var(--accent-terracotta);">error saving settings: ${err.message}</span>`;
    }
  }
}

async function testProviderConnection(id) {
  if (!requireAdminAuth(() => {})) return;

  const config = providerSettingsCache.find(p => p.id === id);
  if (!config) return;

  const resultEl = document.getElementById(`test-result-${id}`);
  if (resultEl) resultEl.innerHTML = `<span style="color: var(--accent-ochre);">testing...</span>`;

  try {
    const res = await authFetch(`${API_BASE}/settings/providers/test`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        providerName: config.providerName,
        apiUrl: config.apiUrl,
        username: config.username,
        password: config.password,
        apiKey: config.apiKey
      })
    });

    const data = await res.json();
    if (resultEl) {
      const color = data.success ? 'var(--accent-olive)' : 'var(--accent-terracotta)';
      resultEl.innerHTML = `<span style="color: ${color};">${data.message}</span>`;
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      if (resultEl) resultEl.innerHTML = `<span style="color: var(--accent-terracotta);">connection error.</span>`;
    }
  }
}

// ─── AUDIO INTEGRITY ANALYSIS ───

let analysisState = {
  summary: { totalAlbums: 0, completeAlbums: 0, missingAlbums: 0, totalMissingTracks: 0 },
  items: [],
  filterText: ''
};

async function fetchAnalysisData() {
  const container = document.getElementById('analysis-list-container');
  if (!container) return;

  if (!isAdmin()) {
    container.innerHTML = renderAdminOnlyNotice("Admin authentication required to run library audio integrity analysis.");
    return;
  }

  container.innerHTML = `<div style="padding: 2rem; text-align: center; color: var(--text-muted);">loading album audio analysis...</div>`;

  try {
    const res = await authFetch('/api/dashboard/analysis');
    const data = await parseJsonResponse(res);
    if (data.success) {
      analysisState.summary = data.summary || {};
      analysisState.items = data.items || [];
      renderAnalysisUI();
    } else {
      container.innerHTML = `<div style="padding: 2rem; color: var(--accent-terracotta); text-align: center;">Error: ${escapeHtml(data.message)}</div>`;
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      container.innerHTML = `<div style="padding: 2rem; color: var(--accent-terracotta); text-align: center;">Failed to load analysis: ${escapeHtml(err.message)}</div>`;
    }
  }
}

async function triggerAnalysisScan() {
  const btn = document.getElementById('btn-analysis-scan');
  const notice = document.getElementById('analysis-status-notice');
  if (btn) btn.disabled = true;
  if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 6px; color: var(--accent-olive);">Scanning library audio files on disk... Please wait.</div>`;

  try {
    const res = await authFetch('/api/dashboard/analysis/scan', { method: 'POST' });
    const data = await parseJsonResponse(res);
    if (data.success) {
      analysisState.summary = data.summary || {};
      analysisState.items = data.items || [];
      renderAnalysisUI();
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #e8f5e9; border: 1px solid #c8e6c9; border-radius: 6px; color: #2e7d32;">Scan complete! Re-checked all library audio files.</div>`;
    } else {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Scan error: ${escapeHtml(data.message)}</div>`;
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Scan error: ${escapeHtml(err.message)}</div>`;
    }
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function triggerRequeueFullyMissingAlbums() {
  const btn = document.getElementById('btn-analysis-requeue');
  const notice = document.getElementById('analysis-status-notice');
  if (btn) {
    btn.disabled = true;
    btn.style.opacity = '0.6';
  }
  if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 6px; color: var(--accent-olive);">⏳ Requeuing 100% missing albums for download...</div>`;

  try {
    const res = await authFetch('/api/dashboard/analysis/requeue-fully-missing', { method: 'POST' });
    const data = await parseJsonResponse(res);
    if (data.success) {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #e8f5e9; border: 1px solid #c8e6c9; border-radius: 6px; color: #2e7d32;">✓ ${escapeHtml(data.message || '100% missing albums requeued for download.')}</div>`;
      await fetchAnalysisData();
    } else {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Requeue error: ${escapeHtml(data.message)}</div>`;
    }
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Requeue error: ${escapeHtml(err.message)}</div>`;
    }
  } finally {
    if (btn) {
      btn.disabled = false;
      btn.style.opacity = '1';
    }
  }
}
window.triggerRequeueFullyMissingAlbums = triggerRequeueFullyMissingAlbums;

async function pollSoulseekStatus(notice, onComplete) {
  const maxWaitMs = 10 * 60 * 1000; // 10 minutes max
  const pollInterval = 3000;
  const start = Date.now();

  while (Date.now() - start < maxWaitMs) {
    await new Promise(r => setTimeout(r, pollInterval));
    try {
      const res = await authFetch('/api/dashboard/analysis/scan-soulseek/status', { method: 'GET' });
      const status = await parseJsonResponse(res);
      const elapsed = Math.round((Date.now() - start) / 1000);

      if (status.state === 'running') {
        if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 6px; color: var(--accent-olive);">⏳ Scan running... ${elapsed}s elapsed</div>`;
      } else if (status.state === 'complete') {
        if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #e8f5e9; border: 1px solid #c8e6c9; border-radius: 6px; color: #2e7d32;">✓ Soulseek scan complete! Imported <strong>${status.importedCount ?? 0}</strong> tracks.</div>`;
        if (onComplete) await onComplete(status);
        return;
      } else if (status.state === 'error') {
        if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Scan error: ${escapeHtml(status.error || 'Unknown error')}</div>`;
        return;
      }
    } catch (e) {
      // ignore transient polling errors
    }
  }
  if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #fff8e1; border: 1px solid #ffe082; border-radius: 6px; color: #f57f17;">Scan is taking longer than expected. Check server logs.</div>`;
}

async function triggerSoulseekImportAll() {
  const btn = document.getElementById('btn-analysis-soulseek');
  const notice = document.getElementById('analysis-status-notice');
  if (btn) btn.disabled = true;
  if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 6px; color: var(--accent-olive);">Starting Soulseek scan...</div>`;

  try {
    const res = await authFetch('/api/dashboard/analysis/scan-soulseek', { method: 'POST' });
    const data = await parseJsonResponse(res);
    if (!data.success) {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Import error: ${escapeHtml(data.message)}</div>`;
      return;
    }

    if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 6px; color: var(--accent-olive);">⏳ Scan running in background...</div>`;

    await pollSoulseekStatus(notice, async (status) => {
      await fetchAnalysisData();
      renderAnalysisUI();
    });
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Import error: ${escapeHtml(err.message)}</div>`;
    }
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function triggerSoulseekImportSingle(albumId) {
  const notice = document.getElementById('analysis-status-notice');
  if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 6px; color: var(--accent-olive);">Starting Soulseek scan for album...</div>`;

  try {
    const res = await authFetch(`/api/dashboard/analysis/album/${albumId}/scan-soulseek`, { method: 'POST' });
    const data = await parseJsonResponse(res);
    if (!data.success) {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Import error: ${escapeHtml(data.message)}</div>`;
      return;
    }

    if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 6px; color: var(--accent-olive);">⏳ Scan running in background...</div>`;

    await pollSoulseekStatus(notice, async () => {
      await fetchAnalysisData();
    });
  } catch (err) {
    if (err.message !== 'Authentication/Authorization required') {
      if (notice) notice.innerHTML = `<div style="padding: 0.8rem; background: #ffebee; border: 1px solid #ffcdd2; border-radius: 6px; color: #c62828;">Import failed: ${escapeHtml(err.message)}</div>`;
    }
  }
}


function filterAnalysisList() {
  const input = document.getElementById('analysis-search-input');
  analysisState.filterText = (input ? input.value : '').toLowerCase();
  renderAnalysisUI(false);
}

function renderAnalysisUI(updateStats = true) {
  if (updateStats) {
    const s = analysisState.summary || {};
    const totalEl = document.getElementById('stat-analysis-total');
    const compEl = document.getElementById('stat-analysis-complete');
    const missAlbEl = document.getElementById('stat-analysis-missing-albums');
    const missTrkEl = document.getElementById('stat-analysis-missing-tracks');

    if (totalEl) totalEl.textContent = s.totalAlbums || 0;
    if (compEl) compEl.textContent = s.completeAlbums || 0;
    if (missAlbEl) missAlbEl.textContent = s.missingAlbums || 0;
    if (missTrkEl) missTrkEl.textContent = s.totalMissingTracks || 0;
  }

  const container = document.getElementById('analysis-list-container');
  if (!container) return;

  const query = analysisState.filterText.trim();
  let items = analysisState.items || [];

  if (query) {
    items = items.filter(item => 
      (item.title && item.title.toLowerCase().includes(query)) ||
      (item.artistName && item.artistName.toLowerCase().includes(query))
    );
  }

  const countEl = document.getElementById('analysis-filter-count');
  if (countEl) {
    countEl.textContent = query ? `showing ${items.length} of ${analysisState.items.length} albums` : `showing all ${items.length} albums`;
  }

  if (items.length === 0) {
    container.innerHTML = `<div style="padding: 3rem; text-align: center; color: var(--text-muted); background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 8px;">No albums match the criteria.</div>`;
    return;
  }

  let html = `<div style="display: flex; flex-direction: column; gap: 0.75rem;">`;

  items.forEach(item => {
    const coverUrl = item.cover || 'https://via.placeholder.com/100?text=No+Cover';
    const isComplete = item.missingTracks === 0;
    const badgeBg = isComplete ? '#e8f5e9' : '#ffebee';
    const badgeColor = isComplete ? '#2e7d32' : '#c62828';
    const badgeBorder = isComplete ? '#c8e6c9' : '#ffcdd2';
    const badgeText = isComplete ? `0 / ${item.totalTracks} missing` : `${item.missingTracks} / ${item.totalTracks} missing`;

    html += `
      <div style="display: flex; align-items: center; justify-content: space-between; padding: 1rem 1.2rem; background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 8px; flex-wrap: wrap; gap: 1rem;">
        <div style="display: flex; align-items: center; gap: 1rem; flex: 1; min-width: 260px;">
          <img src="${coverUrl}" style="width: 54px; height: 54px; border-radius: 6px; object-fit: cover;" alt="${escapeHtml(item.title)}" onerror="this.src='https://via.placeholder.com/100?text=No+Cover'" />
          <div>
            <div style="font-weight: 600; font-size: 1rem;">
              <a href="#album?id=${item.albumId}" style="text-decoration: none; color: inherit;">${escapeHtml(item.title)}</a>
            </div>
            <div style="color: var(--text-muted); font-size: 0.85rem; margin-top: 0.2rem;">${escapeHtml(item.artistName)}</div>
          </div>
        </div>

        <div style="display: flex; align-items: center; gap: 1.5rem; flex-wrap: wrap;">
          <div style="padding: 0.35rem 0.8rem; background: ${badgeBg}; border: 1px solid ${badgeBorder}; color: ${badgeColor}; border-radius: 20px; font-weight: 600; font-size: 0.85rem;">
            ${badgeText}
          </div>
          <button class="btn-secondary" style="padding: 0.4rem 0.9rem; font-size: 0.8rem;" onclick="triggerSoulseekImportSingle('${item.albumId}')">
            scan inside soulseek folder
          </button>
        </div>
      </div>
    `;
  });

  html += `</div>`;
  container.innerHTML = html;
}

// ═══════════════════════════════════════════════════════════════
//  PLAYLIST MANAGER
// ═══════════════════════════════════════════════════════════════

const LIBRARY_API = '/Library';

// ── State for this section ──
let pm = {
  playlists: [],
  currentPlaylistId: null,
  currentPlaylistName: '',
  currentTracks: [],
  parsedImportTracks: [],
  importResults: [],
  replaceTargetPlaylistId: null,
  replaceTargetOldRecordingId: null,
  replaceTargetRowIndex: null,
  replaceSelectedRecording: null,
  replaceSearchTimer: null,
};

// ── Routing entry point ──
async function renderPlaylistsView() {
  if (!isAuthenticated()) {
    document.getElementById('playlists-list-container').innerHTML =
      renderAdminOnlyNotice('sign in to manage your playlists.');
    return;
  }
  closePLaylistDetail();
  await fetchAndRenderPlaylists();
}

async function fetchAndRenderPlaylists() {
  const container = document.getElementById('playlists-list-container');
  container.innerHTML = '<p style="color:var(--text-muted); padding: 1rem 0;">loading playlists…</p>';
  try {
    const res = await authFetch(`${LIBRARY_API}/playlists`);
    const data = await res.json();
    pm.playlists = data || [];
    renderPlaylistCards();
  } catch (err) {
    container.innerHTML = `<p style="color:#c62828;">failed to load playlists: ${escapeHtml(err.message)}</p>`;
  }
}

function renderPlaylistCards() {
  const container = document.getElementById('playlists-list-container');
  if (!pm.playlists.length) {
    container.innerHTML = `
      <div style="text-align:center; padding: 3rem 1rem; color: var(--text-muted);">
        <div style="font-size:2.5rem; margin-bottom:0.8rem;">🎵</div>
        <p>no playlists yet. import one using the button above!</p>
      </div>`;
    return;
  }
  let html = '<div style="display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 1rem; margin-bottom: 2rem;">';
  pm.playlists.forEach(pl => {
    const id   = escapeHtml(pl.id);
    const name = escapeHtml(pl.name || 'Untitled');
    html += `
      <div style="background: var(--bg-card); border: 1px solid var(--border-light); border-radius: 10px;
                  padding: 1.2rem; position: relative; transition: box-shadow 0.18s, border-color 0.18s;"
           onmouseenter="this.style.boxShadow='0 4px 16px rgba(0,0,0,0.09)'; this.style.borderColor='var(--accent-olive)';"
           onmouseleave="this.style.boxShadow=''; this.style.borderColor='var(--border-light)';">
        <div style="cursor:pointer;" onclick="openPlaylistDetail('${id}', '${name}')">
          <div style="font-size:1.05rem; font-weight:600; margin-bottom:0.3rem;">${name}</div>
          <div style="font-size:0.8rem; color:var(--text-muted); margin-bottom:0.9rem;">${pl.count !== undefined ? pl.count + ' tracks' : ''}</div>
        </div>
        <div style="display:flex; gap:0.5rem; align-items:center;">
          <button class="btn-secondary" style="font-size:0.75rem; padding:0.25rem 0.7rem; flex:1;"
              onclick="openPlaylistDetail('${id}', '${name}')">
            open
          </button>
          <button id="del-btn-${id}" class="btn-secondary" style="font-size:0.75rem; padding:0.25rem 0.7rem; color:#c62828; border-color:#c62828;"
              onclick="startDeletePlaylist('${id}', '${name}', this)">
            delete
          </button>
        </div>
        <div id="del-confirm-${id}" style="display:none; margin-top:0.7rem; padding:0.6rem 0.8rem;
            background:#fff0f0; border:1px solid #f5c2c2; border-radius:6px; font-size:0.82rem;">
          <div style="margin-bottom:0.5rem; color:#7f1d1d;">delete <strong>${name}</strong>? this cannot be undone.</div>
          <div style="display:flex; gap:0.5rem;">
            <button class="btn-secondary" style="font-size:0.75rem; flex:1;"
                onclick="cancelDeletePlaylist('${id}')">cancel</button>
            <button class="btn-cta" style="font-size:0.75rem; flex:1; background:#c62828; border-color:#c62828;"
                onclick="confirmDeletePlaylist('${id}', this)">yes, delete</button>
          </div>
        </div>
      </div>`;
  });
  html += '</div>';
  container.innerHTML = html;
}

// ── Playlist detail (track list) ──
async function openPlaylistDetail(playlistId, playlistName) {
  pm.currentPlaylistId = playlistId;
  pm.currentPlaylistName = playlistName;
  document.getElementById('playlists-list-container').style.display = 'none';
  const importBtn = document.getElementById('btn-open-import-modal');
  if (importBtn) importBtn.style.display = 'none';
  const detail = document.getElementById('playlist-detail-container');
  detail.style.display = 'block';
  document.getElementById('playlist-detail-title').textContent = playlistName;
  document.getElementById('playlist-track-body').innerHTML =
    '<tr><td colspan="5" style="text-align:center; padding:1.5rem; color:var(--text-muted);">loading…</td></tr>';
  document.getElementById('playlist-track-filter').value = '';

  try {
    let page = 1, pageSize = 50, all = [], hasMore = true;
    while (hasMore) {
      const res = await authFetch(`${LIBRARY_API}/playlists/${playlistId}/recordings?page=${page}&pageSize=${pageSize}`);
      const tracks = await res.json();
      all = all.concat(tracks);
      hasMore = tracks.length === pageSize;
      page++;
    }
    pm.currentTracks = all;
    renderPlaylistTrackTable(all);
    document.getElementById('playlist-track-count').textContent = `${all.length} tracks`;
  } catch (err) {
    document.getElementById('playlist-track-body').innerHTML =
      `<tr><td colspan="5" style="color:#c62828; padding:1rem;">${escapeHtml(err.message)}</td></tr>`;
  }
}

function renderPlaylistTrackTable(tracks) {
  const tbody = document.getElementById('playlist-track-body');
  if (!tracks.length) {
    tbody.innerHTML = '<tr><td colspan="5" style="text-align:center; padding:1.5rem; color:var(--text-muted);">no tracks</td></tr>';
    return;
  }
  const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
  tbody.innerHTML = tracks.map((t, i) => {
    const isUnavailable = !t.id || t.id === EMPTY_GUID;
    const statusBadge = isUnavailable
      ? `<span style="background:#c62828; color:#fff; font-size:0.71rem; padding:0.14rem 0.48rem; border-radius:999px; font-weight:600;">unavailable</span>`
      : `<span style="background:#2e7d32; color:#fff; font-size:0.71rem; padding:0.14rem 0.48rem; border-radius:999px; font-weight:600;">available</span>`;

    const safeTitle  = (t.title  || '').replace(/'/g, "\\'").replace(/"/g, '&quot;');
    const safeArtist = (t.artistName || '').replace(/'/g, "\\'").replace(/"/g, '&quot;');
    const safeAlbum  = (t.releaseTitle || '').replace(/'/g, "\\'").replace(/"/g, '&quot;');
    const safeCover  = (t.cover || '').replace(/'/g, "\\'").replace(/"/g, '&quot;');
    const safeId     = (t.id || EMPTY_GUID).replace(/'/g, "\\'");

    const replaceBtn = `<button class="btn-secondary" style="font-size:0.75rem; padding:0.25rem 0.65rem;"
        onclick="openReplaceSongModal('${escapeHtml(pm.currentPlaylistId)}','${safeId}',${i},'${safeTitle}','${safeArtist}','${safeAlbum}','${safeCover}')">
        ⇄ replace
      </button>`;

    const queueBtn = isUnavailable
      ? `<button class="btn-secondary" style="font-size:0.75rem; padding:0.25rem 0.65rem; margin-left:0.4rem; background:var(--accent-olive); color:#fff; border-color:var(--accent-olive);"
          onclick="openQueueAlbumModal('${safeTitle}','${safeArtist}','${safeAlbum}')">
          ⬇ queue album
        </button>`
      : '';

    return `<tr data-title="${escapeHtml((t.title||'').toLowerCase())}" data-artist="${escapeHtml((t.artistName||'').toLowerCase())}">
      <td style="color:var(--text-muted); font-size:0.8rem;">${i + 1}</td>
      <td>
        <div style="font-weight:500; font-size:0.88rem;">${escapeHtml(t.title || '(untitled)')}</div>
        <div style="font-size:0.78rem; color:var(--text-muted);">${escapeHtml(t.artistName || '')} — ${escapeHtml(t.releaseTitle || '')}</div>
      </td>
      <td>
        ${isUnavailable
          ? '<span style="color:var(--text-muted); font-size:0.82rem;">—</span>'
          : `<div style="font-size:0.85rem;">${escapeHtml(t.title || '')}</div>`}
      </td>
      <td>${statusBadge}</td>
      <td style="white-space:nowrap;">${replaceBtn}${queueBtn}</td>
    </tr>`;
  }).join('');
}

function filterPlaylistTracks() {
  const q = document.getElementById('playlist-track-filter').value.toLowerCase();
  const rows = document.querySelectorAll('#playlist-track-body tr[data-title]');
  let shown = 0;
  rows.forEach(row => {
    const match = !q || row.dataset.title.includes(q) || row.dataset.artist.includes(q);
    row.style.display = match ? '' : 'none';
    if (match) shown++;
  });
  document.getElementById('playlist-track-count').textContent = `${shown} / ${pm.currentTracks.length} tracks`;
}

function closePLaylistDetail() {
  document.getElementById('playlist-detail-container').style.display = 'none';
  document.getElementById('playlists-list-container').style.display = '';
  const importBtn = document.getElementById('btn-open-import-modal');
  if (importBtn) importBtn.style.display = '';
  pm.currentPlaylistId = null;
  pm.currentTracks = [];
}

// ── Import Modal ──
function openImportModal() {
  if (!isAuthenticated()) { openAuthModal(); return; }
  showImportStep1();
  document.getElementById('import-json-input').value = '';
  document.getElementById('import-playlist-name').value = '';
  document.getElementById('import-parse-error').style.display = 'none';
  document.getElementById('import-playlist-modal').style.display = 'flex';
}

function closeImportModal() {
  document.getElementById('import-playlist-modal').style.display = 'none';
}

function showImportStep1() {
  document.getElementById('import-step-1').style.display = 'block';
  document.getElementById('import-step-2').style.display = 'none';
  document.getElementById('import-confirm-status').textContent = '';
}

async function previewImport() {
  const errEl = document.getElementById('import-parse-error');
  errEl.style.display = 'none';
  const raw = document.getElementById('import-json-input').value.trim();
  if (!raw) { errEl.textContent = 'Please paste a JSON array of tracks.'; errEl.style.display = 'block'; return; }

  let parsed;
  try {
    parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) throw new Error('Expected a JSON array at the root level.');
  } catch (e) {
    errEl.textContent = `JSON parse error: ${e.message}`;
    errEl.style.display = 'block';
    return;
  }

  // Normalise Spotify-like exports → {name, artistName, releaseName}
  pm.parsedImportTracks = parsed.map(item => ({
    name:        item.name        ?? item.track   ?? item.title   ?? '',
    artistName:  item.artistName  ?? (Array.isArray(item.artists) ? item.artists[0]?.name : item.artist) ?? '',
    releaseName: item.releaseName ?? item.album?.name ?? item.album ?? '',
  })).filter(t => t.name);

  if (!pm.parsedImportTracks.length) {
    errEl.textContent = 'No valid tracks found. Each item needs at least a "name" field.';
    errEl.style.display = 'block';
    return;
  }

  document.getElementById('import-step-1').style.display = 'none';
  document.getElementById('import-step-2').style.display = 'block';

  const previewBody = document.getElementById('import-preview-body');
  previewBody.innerHTML = pm.parsedImportTracks.map((t, i) => `
    <tr>
      <td style="color:var(--text-muted); font-size:0.8rem;">${i + 1}</td>
      <td>
        <div style="font-weight:500; font-size:0.85rem;">${escapeHtml(t.name)}</div>
        <div style="font-size:0.78rem; color:var(--text-muted);">${escapeHtml(t.artistName)} — ${escapeHtml(t.releaseName)}</div>
      </td>
      <td id="import-match-${i}" style="color:var(--text-muted); font-size:0.82rem;">—</td>
      <td id="import-status-${i}"><span style="font-size:0.75rem; color:var(--text-muted);">pending…</span></td>
    </tr>`).join('');

  document.getElementById('import-summary').textContent = `${pm.parsedImportTracks.length} tracks — matching against local library…`;
  document.getElementById('btn-confirm-import').disabled = true;
  await runPreviewMatching();
}

async function runPreviewMatching() {
  const tracks = pm.parsedImportTracks;
  pm.importResults = new Array(tracks.length).fill(null);
  const progressWrapper = document.getElementById('import-progress-bar-wrapper');
  const progressBar     = document.getElementById('import-progress-bar');
  progressWrapper.style.display = 'block';

  for (let i = 0; i < tracks.length; i++) {
    try {
      const q = encodeURIComponent(tracks[i].name + ' ' + tracks[i].artistName);
      const res  = await authFetch(`/Metadata/search/db?query=${q}&page=1&pageSize=3`);
      const data = await res.json();
      const best = [...(data.tracks || []), ...(data.fuzzyTracks || [])][0] || null;
      pm.importResults[i] = best;

      const matchCell  = document.getElementById(`import-match-${i}`);
      const statusCell = document.getElementById(`import-status-${i}`);
      if (best) {
        matchCell.innerHTML  = `<div style="font-size:0.83rem; font-weight:500;">${escapeHtml(best.title||'')}</div>
          <div style="font-size:0.75rem; color:var(--text-muted);">${escapeHtml(best.artistName||'')} — ${escapeHtml(best.releaseTitle||'')}</div>`;
        statusCell.innerHTML = `<span style="background:#2e7d32; color:#fff; font-size:0.7rem; padding:0.12rem 0.45rem; border-radius:999px; font-weight:600;">matched</span>`;
      } else {
        matchCell.innerHTML  = `<span style="color:var(--text-muted); font-size:0.8rem;">not found</span>`;
        statusCell.innerHTML = `<span style="background:#c62828; color:#fff; font-size:0.7rem; padding:0.12rem 0.45rem; border-radius:999px; font-weight:600;">unavailable</span>`;
      }
    } catch (_) { pm.importResults[i] = null; }
    progressBar.style.width = Math.round(((i + 1) / tracks.length) * 100) + '%';
  }

  const matched = pm.importResults.filter(r => r !== null).length;
  document.getElementById('import-summary').textContent =
    `${matched} / ${tracks.length} tracks matched. ${tracks.length - matched} will be marked unavailable.`;
  document.getElementById('btn-confirm-import').disabled = false;
  progressWrapper.style.display = 'none';
}

async function confirmImport() {
  const name     = (document.getElementById('import-playlist-name').value.trim()) || 'Imported Playlist';
  const statusEl = document.getElementById('import-confirm-status');
  const btn      = document.getElementById('btn-confirm-import');
  btn.disabled   = true;
  statusEl.style.color = '';
  statusEl.textContent = 'creating playlist…';

  try {
    // 1. Create empty playlist
    const createRes = await authFetch(`${LIBRARY_API}/playlists/create-for-import`, {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ name, recordingIds: [] }),
    });
    if (!createRes.ok) throw new Error(`create failed: HTTP ${createRes.status}`);
    const { id: playlistId } = await createRes.json();

    statusEl.textContent = 'matching & importing tracks…';

    // 2. Server-side fuzzy import (DB-only, no MusicBrainz)
    const importRes = await authFetch(`${LIBRARY_API}/playlists/${playlistId}/import-json`, {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify(pm.parsedImportTracks),
    });
    if (!importRes.ok) throw new Error(`import failed: HTTP ${importRes.status}`);
    const results = await importRes.json();

    const matchedCount = results.filter(r => r.isAvailable).length;
    statusEl.textContent = `✓ "${name}" created! ${matchedCount} / ${results.length} tracks matched.`;
    statusEl.style.color = 'var(--accent-olive)';

    setTimeout(async () => {
      closeImportModal();
      await fetchAndRenderPlaylists();
    }, 1800);
  } catch (err) {
    statusEl.textContent = `error: ${err.message}`;
    statusEl.style.color = '#c62828';
    btn.disabled = false;
  }
}

// ── Replace Song Modal ──
function openReplaceSongModal(playlistId, oldRecordingId, rowIndex, title, artist, album, cover) {
  pm.replaceTargetPlaylistId    = playlistId;
  pm.replaceTargetOldRecordingId = oldRecordingId;
  pm.replaceTargetRowIndex      = rowIndex;
  pm.replaceSelectedRecording   = null;

  document.getElementById('replace-song-context').innerHTML =
    `<strong>replacing:</strong> ${escapeHtml(title)} <span style="color:var(--text-muted);">by ${escapeHtml(artist)}</span>`;
  document.getElementById('replace-song-search-input').value = title;
  document.getElementById('replace-song-results').innerHTML  = '';
  document.getElementById('btn-confirm-replace').disabled    = true;
  document.getElementById('replace-song-status').textContent = '';
  document.getElementById('replace-song-modal').style.display = 'flex';
  performReplaceSearch(title);
}

function closeReplaceSongModal() {
  document.getElementById('replace-song-modal').style.display = 'none';
  pm.replaceSelectedRecording = null;
}

function debouncedReplaceSearch() {
  clearTimeout(pm.replaceSearchTimer);
  const q = document.getElementById('replace-song-search-input').value.trim();
  pm.replaceSearchTimer = setTimeout(() => performReplaceSearch(q), 280);
}

async function performReplaceSearch(query) {
  const container = document.getElementById('replace-song-results');
  if (!query || query.length < 2) { container.innerHTML = ''; return; }
  container.innerHTML = '<div style="padding:0.8rem; color:var(--text-muted); font-size:0.85rem;">searching…</div>';
  try {
    const res  = await authFetch(`/Metadata/search/db?query=${encodeURIComponent(query)}&page=1&pageSize=12`);
    const data = await res.json();
    const tracks = [...(data.tracks || []), ...(data.fuzzyTracks || [])];
    if (!tracks.length) {
      container.innerHTML = '<div style="padding:0.8rem; color:var(--text-muted); font-size:0.85rem;">no results</div>';
      return;
    }
    container.innerHTML = tracks.map(t => {
      const id    = escapeHtml(t.id||'');
      const title = escapeHtml(t.title||'');
      const art   = escapeHtml(t.artistName||'');
      const alb   = escapeHtml(t.releaseTitle||'');
      const cov   = escapeHtml(t.cover||'');
      return `<div class="replace-result-row"
           style="padding: 0.7rem 1rem; cursor:pointer; border-bottom: 1px solid var(--border-light); transition: background 0.12s;"
           onmouseenter="this.style.background='var(--secondaryBackground)'"
           onmouseleave="this.style.background=''"
           onclick="selectReplaceRecording(this,'${id}','${title}','${art}','${alb}','${cov}')">
        <div style="font-weight:500; font-size:0.88rem;">${title}</div>
        <div style="font-size:0.78rem; color:var(--text-muted);">${art} — ${alb}</div>
      </div>`;
    }).join('');
  } catch (e) {
    container.innerHTML = `<div style="padding:0.8rem; color:#c62828; font-size:0.82rem;">${escapeHtml(e.message)}</div>`;
  }
}

function selectReplaceRecording(el, id, title, artist, album, cover) {
  document.querySelectorAll('.replace-result-row').forEach(r => r.style.outline = '');
  el.style.outline = '2px solid var(--accent-olive)';
  pm.replaceSelectedRecording = { id, title, artist, album, cover };
  document.getElementById('btn-confirm-replace').disabled = false;
}

async function confirmReplaceRecording() {
  if (!pm.replaceSelectedRecording) return;
  const statusEl = document.getElementById('replace-song-status');
  const btn      = document.getElementById('btn-confirm-replace');
  btn.disabled   = true;
  statusEl.textContent = 'replacing…';

  const { replaceTargetPlaylistId: pid, replaceTargetOldRecordingId: oldId, replaceTargetRowIndex: rowIdx } = pm;
  const { id, title, artist, album, cover } = pm.replaceSelectedRecording;

  try {
    const res = await authFetch(`${LIBRARY_API}/playlists/${encodeURIComponent(pid)}/recordings/${encodeURIComponent(oldId)}/replace`, {
      method:  'PUT',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ id, title, artist, album, cover }),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    statusEl.textContent = '✓ replaced!';
    statusEl.style.color = 'var(--accent-olive)';

    // Update local state
    const track = pm.currentTracks[rowIdx];
    if (track) { track.id = id; track.title = title; track.artistName = artist; track.releaseTitle = album; track.cover = cover; }
    renderPlaylistTrackTable(pm.currentTracks);

    setTimeout(closeReplaceSongModal, 900);
  } catch (err) {
    statusEl.textContent = `error: ${err.message}`;
    statusEl.style.color = '#c62828';
    btn.disabled = false;
  }
}
// ── Queue Album from Playlist ──
function openQueueAlbumModal(trackTitle, artistName, albumName) {
  if (!isAuthenticated()) { openAuthModal(); return; }

  // Pre-fill: album name + artist from the playlist entry
  document.getElementById('queue-album-track-label').textContent =
    `Track: "${trackTitle}" by ${artistName}`;
  document.getElementById('queue-album-title-input').value  = albumName || '';
  document.getElementById('queue-album-artist-input').value = artistName || '';
  document.getElementById('queue-album-status').textContent = '';
  document.getElementById('btn-confirm-queue-album').disabled = false;
  document.getElementById('queue-album-modal').style.display = 'flex';
}

function closeQueueAlbumModal() {
  document.getElementById('queue-album-modal').style.display = 'none';
}

async function confirmQueueAlbum() {
  const title    = document.getElementById('queue-album-title-input').value.trim();
  const artist   = document.getElementById('queue-album-artist-input').value.trim();
  const statusEl = document.getElementById('queue-album-status');
  const btn      = document.getElementById('btn-confirm-queue-album');

  if (!title) { statusEl.textContent = 'Album title is required.'; statusEl.style.color = '#c62828'; return; }

  btn.disabled = true;
  statusEl.style.color = '';
  statusEl.textContent = 'queueing…';

  try {
    const res = await authFetch(`${API_BASE}/album/add`, {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ title, artist }),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    statusEl.textContent = `✓ "${title}" queued for download!`;
    statusEl.style.color = 'var(--accent-olive)';
    setTimeout(closeQueueAlbumModal, 1500);
  } catch (err) {
    statusEl.textContent = `error: ${err.message}`;
    statusEl.style.color = '#c62828';
    btn.disabled = false;
  }
}

// ── Delete Playlist ──
function startDeletePlaylist(playlistId, playlistName, btn) {
  // Hide confirm panel on all cards first
  document.querySelectorAll('[id^="del-confirm-"]').forEach(el => el.style.display = 'none');
  document.querySelectorAll('[id^="del-btn-"]').forEach(el => el.textContent = 'delete');
  // Show confirm panel for this card
  const confirmPanel = document.getElementById(`del-confirm-${playlistId}`);
  if (confirmPanel) confirmPanel.style.display = 'block';
  btn.textContent = 'deleting…';
}

function cancelDeletePlaylist(playlistId) {
  const confirmPanel = document.getElementById(`del-confirm-${playlistId}`);
  if (confirmPanel) confirmPanel.style.display = 'none';
  const delBtn = document.getElementById(`del-btn-${playlistId}`);
  if (delBtn) delBtn.textContent = 'delete';
}

async function confirmDeletePlaylist(playlistId, btn) {
  btn.disabled = true;
  btn.textContent = 'deleting…';
  try {
    const res = await authFetch(`${LIBRARY_API}/playlists/${playlistId}`, { method: 'DELETE' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    // Remove from local state and re-render cards
    pm.playlists = pm.playlists.filter(p => p.id !== playlistId);
    renderPlaylistCards();
  } catch (err) {
    btn.disabled = false;
    btn.textContent = 'yes, delete';
    const panel = document.getElementById(`del-confirm-${playlistId}`);
    if (panel) {
      const errEl = document.createElement('div');
      errEl.style.cssText = 'color:#c62828; font-size:0.78rem; margin-top:0.4rem;';
      errEl.textContent = `error: ${err.message}`;
      panel.appendChild(errEl);
    }
  }
}
