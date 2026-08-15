/* ==========================================================================
   输入法一键切换 - 前端逻辑 (通过 pywebview 桥接 Python)
   ========================================================================== */

const $ = (id) => document.getElementById(id);

let api = null;
let recordingTarget = null;

/* ---------------- 无边框窗口：控制按钮 ----------------
   窗口拖动由 CSS -webkit-app-region: drag 交给 WebView2 原生处理，
   不再通过 JS 高频调用 move_window（避免跨线程桥接调用导致卡顿）。 */

function initWindowControls() {
  $('btnMinimize').addEventListener('click', () => api.minimize_window());
  $('btnClose').addEventListener('click', () => api.close_to_tray());
}

/* ---------------- 状态 ---------------- */

function setListening(on, hotkey, toggleHotkey) {
  const pill = $('statusPill');
  const st = $('statusText');
  if (on) {
    pill.className = 'status-dot status-on';
    st.textContent = `状态：监听中 (${hotkey}${toggleHotkey ? ' · 开关 ' + toggleHotkey : ''})`;
  } else {
    pill.className = 'status-dot status-off';
    st.textContent = '状态：未启动';
  }
  $('btnStart').disabled = on;
  $('btnStop').disabled = !on;
}

/* ---------------- 热键录制 ---------------- */

function enterRecording(target) {
  recordingTarget = target;
  const el = target === 'toggle' ? $('toggleHotkey') : $('switchHotkey');
  el.textContent = '按下热键... (ESC 取消)';
  el.classList.add('recording');
  $('btnCancel').classList.remove('hidden');
  $('btnRecordSwitch').disabled = true;
  $('btnRecordToggle').disabled = true;
  api.start_recording(target);
}

function exitRecording() {
  const el = recordingTarget === 'toggle' ? $('toggleHotkey') : $('switchHotkey');
  el.classList.remove('recording');
  $('btnCancel').classList.add('hidden');
  $('btnRecordSwitch').disabled = false;
  $('btnRecordToggle').disabled = false;
  recordingTarget = null;
}

/* ---------------- 初始化（pywebview 就绪后） ---------------- */

function bindEvents() {
  // 窗口控制
  $('btnRecordSwitch').addEventListener('click', () => enterRecording('hotkey'));
  $('switchHotkey').addEventListener('click', () => enterRecording('hotkey'));
  $('btnRecordToggle').addEventListener('click', () => enterRecording('toggle'));
  $('toggleHotkey').addEventListener('click', () => enterRecording('toggle'));
  $('btnCancel').addEventListener('click', () => api.cancel_recording());

  // 操作按钮
  $('btnStart').addEventListener('click', async () => {
    const ok = await api.start_listening();
    if (ok) {
      const cfg = await api.get_config();
      setListening(true, cfg.hotkey, cfg.toggle_hotkey);
    }
  });

  $('btnStop').addEventListener('click', async () => {
    await api.stop_listening();
    setListening(false);
  });

  $('btnManual').addEventListener('click', () => api.manual_test());

  $('btnDebug').addEventListener('click', () => {
    $('debugDrawer').classList.add('open');
  });

  $('btnCloseDebug').addEventListener('click', () => {
    $('debugDrawer').classList.remove('open');
  });

  // 点击抽屉以外的区域自动关闭，避免遮挡操作按钮（排除"调试日志"按钮本身）
  document.addEventListener('click', (e) => {
    const drawer = $('debugDrawer');
    if (drawer.classList.contains('open') &&
        !drawer.contains(e.target) &&
        !e.target.closest('#btnDebug')) {
      drawer.classList.remove('open');
    }
  });

  // 选项变化
  document.querySelectorAll('input[name=method]').forEach(r => {
    r.addEventListener('change', () => api.set_method(Number(r.value)));
  });

  $('chkAutostart').addEventListener('change', async (e) => {
    const ok = await api.set_autostart(e.target.checked);
    if (!ok) e.target.checked = !e.target.checked;
  });

  $('chkTrayStart').addEventListener('change', (e) => {
    api.set_tray_start(e.target.checked);
  });
}

async function init() {
  api = window.pywebview.api;
  initWindowControls();
  bindEvents();

  const cfg = await api.get_config();
  $('switchHotkey').textContent = cfg.hotkey || '未设置';
  $('toggleHotkey').textContent = cfg.toggle_hotkey || '未设置';

  document.querySelectorAll('input[name=method]').forEach(r => {
    r.checked = (Number(r.value) === cfg.method);
  });

  $('chkAutostart').checked = cfg.autostart;
  $('chkTrayStart').checked = cfg.start_to_tray;

  // 加载历史日志到调试抽屉（启动阶段的日志在桥接就绪前不实时推送，这里补齐）
  try {
    const logs = await api.get_logs();
    const el = $('debugLog');
    const frag = document.createDocumentFragment();
    logs.forEach((m) => {
      const line = document.createElement('div');
      line.textContent = String(m);
      frag.appendChild(line);
    });
    el.appendChild(frag);
    el.scrollTop = el.scrollHeight;
  } catch (e) {
    // 忽略：历史日志加载失败不影响主界面
  }

  if (cfg.listening) {
    setListening(true, cfg.hotkey, cfg.toggle_hotkey);
  }
}

/* ---------------- pywebview 回调事件 ---------------- */

window.__onEvent = (event, data) => {
  switch (event) {
    case 'recording_cancel':
      exitRecording();
      break;

    case 'hotkey_saved':
      exitRecording();
      if (data.target === 'toggle') {
        $('toggleHotkey').textContent = data.value || '未设置';
      } else {
        $('switchHotkey').textContent = data.value || '未设置';
      }
      break;

    case 'listening':
      setListening(true, data.hotkey, data.toggle_hotkey);
      break;

    case 'stopped':
      setListening(false);
      break;

    case 'log':
      appendLog(data);
      break;
  }
};

/* ---------------- 日志 ---------------- */

function appendLog(msg) {
  const el = $('debugLog');
  const line = document.createElement('div');
  line.textContent = String(msg);
  el.appendChild(line);
  el.scrollTop = el.scrollHeight;
}

/* ---------------- 启动 ---------------- */

window.addEventListener('pywebviewready', init);
