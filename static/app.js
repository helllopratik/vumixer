/* VU Mixer front-end.  Talks to the stdlib Python server in server.py. */
(() => {
  'use strict';

  const $ = (sel) => document.querySelector(sel);
  const METER_LABELS = ['MIC', 'SYS', 'A', 'B'];
  const VER = 'v4';
  const state = {
    data: null,
    meters: {},
    display: {},
    failCount: 0,
    lastEventId: 0,
    loadedBuild: null,
    buildWarned: false,
    stateOk: 0,
  };

  // ---------------------------------------------------------------- helpers
  const logBox = () => document.getElementById('log-box');
  function log(msg) {
    const box = logBox();
    if (!box) return;
    const t = new Date();
    const ts = String(t.getHours()).padStart(2,'0') + ':' +
               String(t.getMinutes()).padStart(2,'0') + ':' +
               String(t.getSeconds()).padStart(2,'0') + '.' +
               String(t.getMilliseconds()).padStart(3,'0');
    const line = '[' + ts + '] ' + msg;
    const lines = (box.value ? box.value.split('\n') : []).concat(line);
    while (lines.length > 400) lines.shift();
    box.value = lines.join('\n');
    box.scrollTop = box.scrollHeight;
  }
  window.addEventListener('error', (e) => {
    log('⚠ JS error: ' + (e.message || e.error || e));
  });
  window.addEventListener('unhandledrejection', (e) => {
    log('⚠ unhandled rejection: ' + (e.reason && e.reason.message || e.reason || ''));
  });

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
  }

  async function api(path, opts) {
    opts = opts || {};
    // Never let a request hang the page: if a fetch is stuck (e.g. a stale
    // keep-alive socket from a server that died), abort it so the poll loops
    // keep running and buttons can never stay locked.
    const timeoutMs = opts.timeout || 6000;
    const ctrl = typeof AbortController !== 'undefined' ? new AbortController() : null;
    const timer = setTimeout(() => { if (ctrl) ctrl.abort(); }, timeoutMs);
    const action = (opts.method || 'GET') + ' ' + path;
    const quiet = path === '/api/meters';             // 80ms flood: never log
    const isStatePoll = path === '/api/state';        // 1s: log failures + heartbeat
    const t0 = performance.now();
    if (!quiet && !isStatePoll) log('→ ' + action);
    try {
      const res = await fetch(path, Object.assign({}, opts, ctrl ? { signal: ctrl.signal } : {}));
      let data = {};
      try { data = await res.json(); } catch (_) { /* ignore */ }
      if (!res.ok || data.ok === false) {
        throw new Error(data.error || ('HTTP ' + res.status));
      }
      if (isStatePoll) {
        if (!quiet) {
          state.stateOk += 1;
          if (state.stateOk % 15 === 0) {
            log('state ok (poll #' + state.stateOk + ')');
          }
        }
      } else if (!quiet) {
        log('← ok  ' + action + '  ' + Math.round(performance.now() - t0) + 'ms');
      }
      return data;
    } catch (e) {
      const msg = (e && e.name === 'AbortError')
        ? 'request timed out (server not responding?)'
        : ((e && e.message) || String(e));
      if (!quiet) log('← ERR ' + action + ': ' + msg);
      throw new Error(msg);
    } finally {
      clearTimeout(timer);
    }
  }

  function post(path, body) {
    return api(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {}),
    });
  }

  let toastTimer = null;
  function toast(msg, isErr) {
    const el = $('#toast');
    el.textContent = msg;
    el.className = 'toast' + (isErr ? ' toast--err' : '');
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.hidden = true; }, 3400);
  }

  function setStatus(ok) {
    const el = $('#status');
    if (ok) {
      el.classList.add('status--ok');
      el.classList.remove('status--err', 'status--wait');
    } else {
      el.classList.add('status--err');
      el.classList.remove('status--ok', 'status--wait');
      el.textContent = 'disconnected';
    }
  }

  // ------------------------------------------------------------ rendering
  function fillSelect(sel, items, current, emptyLabel) {
    if (!sel) return;
    const sig = items.map((i) => i.name + '\u0000' + (i.description || '')).join('\u0001') +
      '\u0002' + (current || '') + '\u0002' + (emptyLabel || '');
    if (sel.dataset.sig === sig) return;
    sel.dataset.sig = sig;

    const keep = sel.value;
    const want = current || keep;
    sel.innerHTML = '';
    if (!items.length) {
      const o = document.createElement('option');
      o.value = '';
      o.textContent = emptyLabel || 'none available';
      o.disabled = true;
      o.selected = true;
      sel.appendChild(o);
      return;
    }
    for (const it of items) {
      const o = document.createElement('option');
      o.value = it.name;
      o.textContent = it.description || it.name;
      sel.appendChild(o);
    }
    if (want && items.some((i) => i.name === want)) sel.value = want;
    if (want && !items.some((i) => i.name === want)) {
      const o = document.createElement('option');
      o.value = want;
      o.textContent = '(unavailable) ' + want;
      o.disabled = true;
      o.selected = true;
      sel.insertBefore(o, sel.firstChild);
    }
  }

  function setStrip(key, info) {
    if (!info) return;
    const range = document.getElementById(key + '-gain');
    const val = document.getElementById(key + '-gain-val');
    const mute = document.getElementById(key + '-mute');
    if (range) {
      range.dataset.serverVolume = String(info.volume);
      if (document.activeElement !== range) range.value = String(Math.round(info.volume));
    }
    if (val) val.textContent = Math.round(info.volume) + '%';
    if (mute) {
      if (mute.dataset.pending === '1') {
        const at = Number(mute.dataset.pendingAt || 0);
        if (Date.now() - at >= 8000) {
          mute.dataset.pending = '0';
          delete mute.dataset.pendingAt;
        }
      }
      if (mute.dataset.pending !== '1') {
        mute.dataset.muted = info.mute ? '1' : '0';
        mute.classList.toggle('active', !!info.mute);
      }
    }
  }

  function setMicFeeds(data) {
    const setBtn = (id, on) => {
      const b = document.getElementById(id);
      if (!b) return;
      if (b.dataset.pending === '1') {
        // watchdog: a lock older than 8s is stale (a fetch that never
        // settled); force-release it so the button can never stay frozen.
        const at = Number(b.dataset.pendingAt || 0);
        if (Date.now() - at < 8000) return;
        b.dataset.pending = '0';
        delete b.dataset.pendingAt;
      }
      b.dataset.on = on ? '1' : '0';
      b.classList.toggle('active', !!on);
    };
    setBtn('mic-send-a', data.mic_to_a);
    setBtn('mic-send-b', data.mic_to_b);
    setBtn('mic-ns', data.mic_ns);
    const ns = document.getElementById('mic-ns');
    if (ns && data.ns_available === false) {
      ns.disabled = true;
      ns.title = 'Noise suppression is not available (WebRTC AEC plugin missing)';
    }
  }

  function applyState(data) {
    if (!data || !data.devices) return;

    // build-code tracking: prove the page runs the latest code.
    if (data.build) {
      if (!state.loadedBuild) {
        state.loadedBuild = data.build;
        log('app ' + VER + ' loaded · code ' + data.build);
        const tag = document.getElementById('code-tag');
        if (tag) { tag.textContent = 'code ' + data.build; tag.classList.remove('stale'); }
      } else if (data.build !== state.loadedBuild) {
        log('⚠ server code changed: ' + state.loadedBuild + ' → ' + data.build +
            ' — RELOAD THE PAGE');
        state.loadedBuild = data.build;
        const tag = document.getElementById('code-tag');
        if (tag) { tag.textContent = 'code ' + data.build; tag.classList.add('stale'); }
      }
    }

    state.data = data;

    fillSelect($('#mic-device'), data.mics || [],
      data.mic && data.mic.device, 'no microphone found');
    fillSelect($('#a-device'), data.devices || [],
      data.buses && data.buses.A && data.buses.A.device, 'no output found');
    fillSelect($('#b-device'), data.devices || [],
      data.buses && data.buses.B && data.buses.B.device, 'no output found');

    setStrip('mic', data.mic);
    setStrip('system', data.system);
    setStrip('a', data.buses && data.buses.A);
    setStrip('b', data.buses && data.buses.B);
    setMicFeeds(data);

    const st = $('#status');
    st.classList.remove('status--wait');
    setStatus(true);
    st.textContent = 'connected · ' + (data.devices ? data.devices.length : 0) +
      ' out · ' + (data.mics ? data.mics.length : 0) + ' in';
    st.title = 'Auto-repair is on: unplugged microphones and outputs are ' +
      're-connected automatically when they come back.';
  }

  // ---------------------------------------------------------------- meters
  function levelToPct(level) {
    if (!(level > 0)) return 0;
    const db = 20 * Math.log10(level);
    return Math.max(0, Math.min(100, ((db + 60) / 60) * 100));
  }

  function display(label) {
    if (!state.display[label]) {
      state.display[label] = { rmsL: 0, rmsR: 0, peakL: 0, peakR: 0, clip: false };
    }
    return state.display[label];
  }

  function frame() {
    for (const label of METER_LABELS) {
      const t = state.meters[label] || {};
      const d = display(label);
      const ease = 0.45;
      d.rmsL += ((t.rmsL || 0) - d.rmsL) * ease;
      d.rmsR += ((t.rmsR || 0) - d.rmsR) * ease;
      d.peakL = Math.max(t.peakL || 0, d.peakL - 0.006);
      d.peakR = Math.max(t.peakR || 0, d.peakR - 0.006);
      d.clip = !!t.clip;
    }
    paint();
    requestAnimationFrame(frame);
  }

  function paint() {
    document.querySelectorAll('.vu[data-meter]').forEach((vu) => {
      const d = state.display[vu.dataset.meter];
      if (!d) return;
      const rows = vu.querySelectorAll('.vu-row');
      const pairs = [[d.rmsL, d.peakL], [d.rmsR, d.peakR]];
      rows.forEach((row, i) => {
        const fill = row.querySelector('.vu-fill');
        const peak = row.querySelector('.vu-peak');
        if (fill) {
          const pct = levelToPct(pairs[i][0]);
          fill.style.clipPath = 'inset(0 ' + (100 - pct).toFixed(1) + '% 0 0)';
        }
        if (peak) peak.style.left = levelToPct(pairs[i][1]).toFixed(1) + '%';
      });
      vu.classList.toggle('clip', d.clip);
    });
  }

  // -------------------------------------------------------------- polling
  async function loopMeters() {
    try {
      const d = await api('/api/meters');
      state.meters = d.meters || {};
      state.failCount = 0;
    } catch (_) {
      state.failCount += 1;
      if (state.failCount > 3) setStatus(false);
    }
    setTimeout(loopMeters, 80);
  }

  async function loopState() {
    try {
      const d = await api('/api/state');
      applyState(d);
      watchEvents(d);
    } catch (_) { /* the meters loop reports connectivity */ }
    setTimeout(loopState, 1000);
  }

  // Show a toast for hotplug / recovery events logged by the server.
  function watchEvents(d) {
    const evs = d.events || [];
    if (!evs.length) return;
    const last = evs[evs.length - 1];
    if (last.id > state.lastEventId) {
      toast(last.msg);
      state.lastEventId = last.id;
    } else if (last.id < state.lastEventId) {
      state.lastEventId = last.id; // server restarted; re-sync
    }
  }

  async function refreshDevices() {
    const btn = $('#btn-refresh');
    btn.disabled = true;
    btn.textContent = 'Checking…';
    try {
      const r = await post('/api/refresh', {});
      if (r.state) {
        applyState(r.state);
        watchEvents(r.state);
      }
      toast('Device check complete');
    } catch (e) {
      toast(e.message, true);
    } finally {
      btn.disabled = false;
      btn.textContent = 'Refresh devices';
    }
  }

  // -------------------------------------------------------------- actions
  function wireFader(key, endpoint, extra) {
    const range = document.getElementById(key + '-gain');
    const val = document.getElementById(key + '-gain-val');
    if (!range) return;
    range.addEventListener('input', () => {
      if (val) val.textContent = range.value + '%';
    });
    range.addEventListener('change', async () => {
      try {
        const r = await post(endpoint, Object.assign({ volume: Number(range.value) }, extra));
        if (r.state) applyState(r.state);
      } catch (e) { toast(e.message, true); }
    });
  }

  function wireMute(key, endpoint, extra) {
    const btn = document.getElementById(key + '-mute');
    if (!btn) return;
    btn.addEventListener('click', async () => {
      if (btn.dataset.pending === '1') {
        const at = Number(btn.dataset.pendingAt || 0);
        if (Date.now() - at < 8000) return;
        btn.dataset.pending = '0';
        delete btn.dataset.pendingAt;
      }
      const next = btn.dataset.muted !== '1';
      btn.dataset.muted = next ? '1' : '0';
      btn.classList.toggle('active', next);
      btn.dataset.pending = '1';
      btn.dataset.pendingAt = Date.now();
      try {
        const r = await post(endpoint, Object.assign({ mute: next }, extra));
        if (r.state) applyState(r.state);
      } catch (e) {
        log('FAIL mute ' + key + ': ' + e.message);
        toast(e.message, true);
        btn.dataset.muted = next ? '0' : '1';
        btn.classList.toggle('active', !next);
      } finally {
        btn.dataset.pending = '0';
        delete btn.dataset.pendingAt;
      }
    });
  }

  function wireDevice(sel, endpoint, extra) {
    const el = $(sel);
    if (!el) return;
    el.addEventListener('change', async () => {
      try {
        const body = Object.assign({ device: el.value }, extra);
        const r = await post(endpoint, body);
        if (r.state) applyState(r.state);
      } catch (e) { toast(e.message, true); }
    });
  }

  function wireMicSend(id, bus) {
    const b = document.getElementById(String(id).replace(/^#/, ''));
    if (!b) return;
    b.addEventListener('click', async () => {
      if (b.dataset.pending === '1') {
        // force-release any stale lock (>8s) so the button can never die;
        // a real request can't outlive the 6s fetch timeout.
        const at = Number(b.dataset.pendingAt || 0);
        if (Date.now() - at < 8000) return;
        b.dataset.pending = '0';
        delete b.dataset.pendingAt;
      }
      const next = b.dataset.on !== '1';
      log('click ' + bus + ' → ' + (next ? 'ENABLE' : 'DISABLE') + ' mic send');
      b.dataset.on = next ? '1' : '0';
      b.classList.toggle('active', next);
      b.dataset.pending = '1';
      b.dataset.pendingAt = Date.now();
      try {
        const r = await post('/api/mic/send', { bus: bus, enabled: next });
        if (r.state) applyState(r.state);
        log('OK ' + bus + ' ' + (next ? 'enabled' : 'disabled') +
            ' · server mic_to_' + bus.toLowerCase() + '=' +
            (r.state && (bus === 'A' ? r.state.mic_to_a : r.state.mic_to_b)));
        toast(next ? 'Mic sent to bus ' + bus : 'Mic removed from bus ' + bus);
      } catch (e) {
        log('FAIL ' + bus + ': ' + e.message);
        toast(e.message, true);
        b.dataset.on = next ? '0' : '1';
        b.classList.toggle('active', !next);
      } finally {
        b.dataset.pending = '0';
        delete b.dataset.pendingAt;
      }
    });
  }

  function wireMicNs() {
    const b = document.getElementById('mic-ns');
    if (!b) return;
    b.addEventListener('click', async () => {
      if (b.dataset.pending === '1') {
        const at = Number(b.dataset.pendingAt || 0);
        if (Date.now() - at < 8000) return;
        b.dataset.pending = '0';
        delete b.dataset.pendingAt;
      }
      const next = b.dataset.on !== '1';
      log('click NS → ' + (next ? 'on' : 'off'));
      b.dataset.on = next ? '1' : '0';
      b.classList.toggle('active', next);
      b.dataset.pending = '1';
      b.dataset.pendingAt = Date.now();
      try {
        const r = await post('/api/mic/ns', { enabled: next });
        if (r.state) applyState(r.state);
        log('OK NS ' + (next ? 'on' : 'off') + ' · server mic_ns=' + (r.state && r.state.mic_ns));
        toast(next ? 'Noise suppression on (WebRTC)' : 'Noise suppression off');
      } catch (e) {
        log('FAIL NS: ' + e.message);
        toast(e.message, true);
        b.dataset.on = next ? '0' : '1';
        b.classList.toggle('active', !next);
      } finally {
        b.dataset.pending = '0';
        delete b.dataset.pendingAt;
      }
    });
  }

  // ----------------------------------------------------------- diagnostics
  async function openDiagnose() {
    const dlg = $('#dlg-diagnose');
    const body = $('#diag-body');
    body.innerHTML = '<p class="muted">checking…</p>';
    if (!dlg.open) dlg.showModal();
    try {
      const d = await api('/api/diagnose');
      body.innerHTML = renderDiag(d.diagnostics || {});
      const chk = body.querySelector('#chk-default-input');
      if (chk) {
        chk.addEventListener('change', async () => {
          try {
            const r = await post('/api/default-input', { enabled: chk.checked });
            if (r.state) applyState(r.state);
            toast(chk.checked
              ? 'New apps will use Monitor of VM-Mic'
              : 'Default input restored');
          } catch (e) {
            toast(e.message, true);
            chk.checked = !chk.checked;
          }
        });
      }
    } catch (e) {
      body.innerHTML = '<p class="muted">Could not read diagnostics: ' + esc(e.message) + '</p>';
    }
  }

  function renderDiag(diag) {
    const hw = diag.hardware_users || [];
    const virt = diag.virtual_users || [];
    const out = [];

    out.push(
      '<div class="callout ' + (diag.default_is_hardware ? 'callout--warn' : 'callout--ok') + '">' +
      (diag.default_is_hardware
        ? 'New apps currently capture the <b>raw hardware microphone</b> (<code>' +
          esc(diag.default_source) + '</code>). Switch them to the mixed mic below.'
        : 'New apps capture <code>' + esc(diag.vm_mic_label || 'Monitor of VM-Mic') +
          '</code>. That is the right setting.') +
      '</div>');

    out.push(
      '<div class="diag-block"><h4>Default input for new apps</h4>' +
      '<div class="toggle-row">' +
      '<label class="switch"><input type="checkbox" id="chk-default-input"' +
      (diag.default_input_enabled ? ' checked' : '') + '><span class="slider"></span></label>' +
      '<span>Use <b>' + esc(diag.vm_mic_label || 'Monitor of VM-Mic') +
      '</b> as the default input</span></div></div>');

    out.push('<div class="diag-block"><h4>Apps using the hardware mic directly' +
      (hw.length ? ' ⚠' : '') + '</h4>');
    if (hw.length) {
      out.push('<ul class="diag-list">' + hw.map((u) =>
        '<li><span>' + esc(u.app) + '</span><span class="tag tag--bad">raw mic</span></li>'
      ).join('') + '</ul>');
      out.push('<p class="muted">These apps grab the microphone hardware. In each one, ' +
        'change the input to <b>' + esc(diag.vm_mic_label || 'Monitor of VM-Mic') +
        '</b>.</p>');
    } else {
      out.push('<p class="muted">None — nothing is competing for the raw microphone.</p>');
    }
    out.push('</div>');

    out.push('<div class="diag-block"><h4>Apps using the VM mic bus</h4>');
    if (virt.length) {
      out.push('<ul class="diag-list">' + virt.map((u) =>
        '<li><span>' + esc(u.app) + '</span><span class="tag tag--good">VM mic</span></li>'
      ).join('') + '</ul>');
    } else {
      out.push('<p class="muted">No app is listening to the mic bus right now.</p>');
    }
    out.push('</div>');

    out.push('<div class="diag-block"><h4>What to select in other apps</h4>' +
      '<p>In Discord, OBS, Zoom, a browser, etc. choose the input device named ' +
      '<code>' + esc(diag.vm_mic_label || 'Monitor of VM-Mic') + '</code> ' +
      '(system name <code>' + esc(diag.vm_mic_monitor || 'vm_miclink.monitor') + '</code>). ' +
      'That gives the app the same gain-controlled microphone signal that goes to ' +
      'buses A and B, without two programs competing for the raw device.</p></div>');

    return out.join('');
  }

  // ----------------------------------------------------------------- boot
  function init() {
    wireFader('mic', '/api/mic/gain');
    wireFader('system', '/api/system/gain');
    wireFader('a', '/api/bus/gain', { bus: 'A' });
    wireFader('b', '/api/bus/gain', { bus: 'B' });

    wireMute('mic', '/api/mic/gain');
    wireMute('system', '/api/system/gain');
    wireMute('a', '/api/bus/gain', { bus: 'A' });
    wireMute('b', '/api/bus/gain', { bus: 'B' });

    wireDevice('#mic-device', '/api/mic/device');
    wireDevice('#a-device', '/api/bus/device', { bus: 'A' });
    wireDevice('#b-device', '/api/bus/device', { bus: 'B' });

    wireMicSend('mic-send-a', 'A');
    wireMicSend('mic-send-b', 'B');
    wireMicNs();

    $('#btn-diagnose').addEventListener('click', openDiagnose);
    $('#btn-refresh').addEventListener('click', refreshDevices);
    $('#btn-help').addEventListener('click', () => $('#dlg-help').showModal());

    $('#btn-move').addEventListener('click', async () => {
      try {
        const r = await post('/api/move-streams', {});
        toast('Moved ' + r.moved_outputs + ' app' + (r.moved_outputs === 1 ? '' : 's') +
          ' to VM-System');
      } catch (e) { toast(e.message, true); }
    });

    document.querySelectorAll('.dialog').forEach((dlg) => {
      dlg.querySelectorAll('[data-close]').forEach((b) =>
        b.addEventListener('click', () => dlg.close()));
      dlg.addEventListener('click', (e) => { if (e.target === dlg) dlg.close(); });
    });

    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        document.querySelectorAll('dialog[open]').forEach((d) => d.close());
      }
    });

    document.getElementById('log-copy').addEventListener('click', async () => {
      const box = document.getElementById('log-box');
      if (!box || !box.value.trim()) { toast('Log is empty', true); return; }
      try {
        await navigator.clipboard.writeText(box.value);
        toast('Log copied — paste it back to me');
      } catch (_) {
        box.select();
        if (document.execCommand) document.execCommand('copy');
        toast('Select all & Ctrl+C');
      }
    });
    document.getElementById('log-clear').addEventListener('click', () => {
      const box = document.getElementById('log-box');
      if (box) box.value = '';
    });

    requestAnimationFrame(frame);
    loopMeters();
    loopState();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
