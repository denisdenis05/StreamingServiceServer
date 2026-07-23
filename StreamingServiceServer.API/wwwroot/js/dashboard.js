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
        ${album.recordings.map((track, i) => `
          <li class="track-item">
            <span class="track-number">${track.position || i + 1}</span>
            <span class="track-name">${track.title}</span>
            <span style="font-size: 0.85rem; color: var(--text-muted);">${track.length ? Math.floor(track.length / 60) + ':' + String(track.length % 60).padStart(2, '0') : ''}</span>
          </li>
        `).join('')}
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
