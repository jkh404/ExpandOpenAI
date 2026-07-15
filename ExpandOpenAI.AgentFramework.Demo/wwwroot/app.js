const state = {
  bootstrap: null,
  inspector: 'workspace',
  busy: false,
  workspaceData: null,
  workspaceSignature: '',
  workspaceLoading: false,
  selectedWorkspacePath: null,
  workspacePreview: null,
  collapsedFolders: new Set()
};
const $ = (selector) => document.querySelector(selector);
const conversation = $('#conversation');
const sessionList = $('#session-list');
const statusText = $('#status-text');
const statusDot = $('#status-dot');
const settingsDialog = $('#settings-dialog');
const settingsForm = $('#settings-form');
const inspector = $('#inspector-content');
const sendButton = $('#send');
const messageInput = $('#message');

async function request(url, options = {}) {
  const response = await fetch(url, { headers: { 'Content-Type': 'application/json', ...options.headers }, ...options });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.detail || problem.title || `请求失败 (${response.status})`);
  }
  return response.json();
}

function setStatus(text, kind = 'ready') {
  statusText.textContent = text;
  statusDot.parentElement.classList.toggle('is-busy', kind === 'busy');
  statusDot.parentElement.classList.toggle('is-error', kind === 'error');
}

function renderSessions() {
  sessionList.replaceChildren();
  for (const session of state.bootstrap.sessions || []) {
    const item = document.createElement('button');
    item.className = `session-item${session.isActive ? ' is-active' : ''}`;
    item.type = 'button';
    item.innerHTML = `<span>${escapeHtml(session.name)}</span><small>${formatTime(session.lastOpenedAt)}</small>`;
    item.addEventListener('click', () => activateSession(session.id));
    sessionList.append(item);
  }
}

function renderConversation(items = state.bootstrap?.conversation || []) {
  conversation.replaceChildren();
  if (!items.length) {
    conversation.append($('#empty-state-template').content.cloneNode(true));
    return;
  }
  for (const item of items) conversation.append(createMessage(item.role, item.content));
  scrollConversation();
}

function createMessage(role, text) {
  const wrapper = document.createElement('article');
  wrapper.className = `message ${role}`;
  const label = document.createElement('p');
  label.className = 'message-role';
  label.textContent = role === 'user' ? '你的创作指令' : role === 'error' ? '执行异常' : '小说撰写智能体';
  const body = document.createElement('div');
  body.className = 'message-body';
  body.textContent = text;
  wrapper.append(label, body);
  return wrapper;
}

async function refresh() {
  state.bootstrap = await request('/api/bootstrap');
  fillSettings(state.bootstrap.settings);
  if (!state.bootstrap.isConfigured) {
    setStatus(state.bootstrap.configurationError || '请配置模型服务', 'error');
    settingsDialog.showModal();
    return;
  }
  $('#session-title').textContent = state.bootstrap.sessions.find(s => s.isActive)?.name || '小说企划';
  $('#workspace-label').textContent = state.bootstrap.workspacePath || '尚未选择工作区';
  renderSessions();
  renderConversation();
  setStatus('已恢复可持续创作会话');
  await loadInspector();
}

async function activateSession(id) {
  try {
    state.bootstrap = await request(`/api/sessions/${encodeURIComponent(id)}/activate`, { method: 'POST' });
    $('#session-title').textContent = state.bootstrap.sessions.find(s => s.isActive)?.name || '小说企划';
    renderSessions(); renderConversation(); await loadInspector();
    setStatus('会话已恢复，可继续创作');
  } catch (error) { setStatus(error.message, 'error'); }
}

async function createSession() {
  const name = window.prompt('为新小说会话命名（可留空自动命名）：');
  if (name === null) return;
  try {
    await request('/api/sessions', { method: 'POST', body: JSON.stringify({ name }) });
    await refresh();
  } catch (error) { setStatus(error.message, 'error'); }
}

async function loadInspector() {
  const current = state.inspector;
  document.querySelectorAll('.tab').forEach(tab => tab.classList.toggle('is-active', tab.dataset.inspector === current));
  try {
    if (current === 'workspace') {
      await loadWorkspaceExplorer(true);
      return;
    }
    inspector.textContent = '';
    const data = await request(`/api/memory/${current}`);
    if (!data.memories?.length) {
      const empty = document.createElement('p'); empty.className = 'empty-inspector';
      empty.textContent = current === 'session' ? '尚无压缩归档。多轮创作后，较早对话会以摘要形式出现在这里。' : '尚无跨小说偏好。智能体仅在确认用户的通用写作习惯或协作约定时写入。';
      inspector.append(empty); return;
    }
    const list = document.createElement('div'); list.className = 'memory-list';
    for (const memory of data.memories) {
      const item = document.createElement('article'); item.className = 'memory-item';
      const title = document.createElement('h3'); title.textContent = memory.id;
      const content = document.createElement('p'); content.textContent = memory.content;
      const time = document.createElement('time'); time.textContent = formatTime(memory.createdAt);
      item.append(title, content, time); list.append(item);
    }
    inspector.append(list);
  } catch (error) { inspector.textContent = error.message; }
}

async function loadWorkspaceExplorer(force = false) {
  if (state.workspaceLoading) return;
  state.workspaceLoading = true;
  try {
    const data = await request('/api/workspace');
    const signature = `${data.rootPath}|${(data.files || []).map(file => `${file.path}:${file.size}:${file.lastModifiedAt}`).join('|')}`;
    if (!force && signature === state.workspaceSignature) return;

    state.workspaceData = data;
    state.workspaceSignature = signature;
    if (state.selectedWorkspacePath && !data.files.some(file => file.path === state.selectedWorkspacePath)) {
      state.selectedWorkspacePath = null;
      state.workspacePreview = null;
    }

    renderWorkspaceExplorer();
    if (state.selectedWorkspacePath) await loadWorkspacePreview(state.selectedWorkspacePath);
  } finally {
    state.workspaceLoading = false;
  }
}

function renderWorkspaceExplorer() {
  const data = state.workspaceData;
  if (!data) return;
  inspector.replaceChildren();

  const toolbar = document.createElement('div'); toolbar.className = 'workspace-toolbar';
  const heading = document.createElement('div');
  const count = document.createElement('strong'); count.textContent = `${data.files.length} 个文件`;
  const root = document.createElement('small'); root.textContent = data.rootPath; root.title = data.rootPath;
  heading.append(count, root);
  const refreshButton = document.createElement('button'); refreshButton.type = 'button'; refreshButton.textContent = '↻'; refreshButton.title = '立即刷新';
  refreshButton.addEventListener('click', () => loadWorkspaceExplorer(true));
  toolbar.append(heading, refreshButton); inspector.append(toolbar);

  if (!data.files.length) {
    const empty = document.createElement('p'); empty.className = 'empty-inspector';
    empty.textContent = '工作区中还没有 .txt 或 .md 文件。智能体创建文件后会自动出现在这里。';
    inspector.append(empty);
    return;
  }

  const treeRoot = buildWorkspaceTree(data.files);
  const tree = document.createElement('div'); tree.className = 'workspace-tree';
  appendWorkspaceTreeChildren(tree, treeRoot);
  inspector.append(tree);

  if (state.selectedWorkspacePath) {
    const preview = document.createElement('section'); preview.id = 'workspace-preview'; preview.className = 'workspace-preview';
    const previewHeader = document.createElement('header');
    const previewPath = document.createElement('strong'); previewPath.textContent = state.selectedWorkspacePath;
    const close = document.createElement('button'); close.type = 'button'; close.textContent = '×'; close.title = '关闭预览';
    close.addEventListener('click', () => {
      state.selectedWorkspacePath = null;
      state.workspacePreview = null;
      renderWorkspaceExplorer();
    });
    previewHeader.append(previewPath, close);
    const content = document.createElement('pre');
    content.textContent = state.workspacePreview?.content || '正在读取文件…';
    preview.append(previewHeader, content); inspector.append(preview);
  }
}

function buildWorkspaceTree(files) {
  const root = { folders: new Map(), files: [] };
  for (const file of files) {
    const parts = file.path.split('/');
    let current = root; let folderPath = '';
    for (const segment of parts.slice(0, -1)) {
      folderPath = folderPath ? `${folderPath}/${segment}` : segment;
      if (!current.folders.has(segment)) current.folders.set(segment, { path: folderPath, folders: new Map(), files: [] });
      current = current.folders.get(segment);
    }
    current.files.push(file);
  }
  return root;
}

function appendWorkspaceTreeChildren(parent, node) {
  const folders = [...node.folders.entries()].sort(([left], [right]) => left.localeCompare(right, 'zh-CN'));
  for (const [name, folder] of folders) {
    const details = document.createElement('details'); details.open = !state.collapsedFolders.has(folder.path);
    const summary = document.createElement('summary'); summary.textContent = name;
    details.addEventListener('toggle', () => {
      if (details.open) state.collapsedFolders.delete(folder.path); else state.collapsedFolders.add(folder.path);
    });
    details.append(summary);
    const children = document.createElement('div'); children.className = 'workspace-folder-children';
    appendWorkspaceTreeChildren(children, folder); details.append(children); parent.append(details);
  }

  const files = [...node.files].sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
  for (const file of files) parent.append(createWorkspaceFileButton(file));
}

function createWorkspaceFileButton(file) {
  const button = document.createElement('button'); button.type = 'button'; button.className = 'workspace-file';
  if (file.path === state.selectedWorkspacePath) button.classList.add('is-selected');
  button.title = `${file.path}\n更新于 ${formatTime(file.lastModifiedAt)}`;
  const badge = document.createElement('span'); badge.className = 'file-badge'; badge.textContent = file.extension.toUpperCase();
  const detail = document.createElement('span');
  const name = document.createElement('strong'); name.textContent = file.name;
  const metadata = document.createElement('small'); metadata.textContent = `${formatFileSize(file.size)} · ${formatTime(file.lastModifiedAt)}`;
  detail.append(name, metadata); button.append(badge, detail);
  button.addEventListener('click', async () => {
    state.selectedWorkspacePath = file.path;
    state.workspacePreview = null;
    renderWorkspaceExplorer();
    await loadWorkspacePreview(file.path);
  });
  return button;
}

async function loadWorkspacePreview(path) {
  try {
    const data = await request(`/api/workspace/file?path=${encodeURIComponent(path)}`);
    if (state.selectedWorkspacePath !== path) return;
    state.workspacePreview = data;
    renderWorkspaceExplorer();
  } catch (error) {
    if (state.selectedWorkspacePath !== path) return;
    state.workspacePreview = { path, content: error.message };
    renderWorkspaceExplorer();
  }
}

async function streamTask(message) {
  state.busy = true; sendButton.disabled = true; setStatus('正在自动批准工具并流式执行…', 'busy');
  conversation.querySelector('.empty-state')?.remove();
  conversation.append(createMessage('user', message));
  const assistant = createMessage('assistant', '');
  conversation.append(assistant);
  const assistantBody = assistant.querySelector('.message-body'); scrollConversation();
  try {
    const response = await fetch('/api/chat/stream', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId: state.bootstrap.activeSessionId, message })
    });
    if (!response.ok || !response.body) throw new Error((await response.text()) || '无法启动流式任务');
    const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = '';
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n'); buffer = lines.pop();
      for (const line of lines) {
        if (!line.trim()) continue;
        const update = JSON.parse(line);
        if (update.type === 'delta') { assistantBody.textContent += update.content; scrollConversation(); }
        if (update.type === 'status') setStatus(update.content, 'busy');
        if (update.type === 'error') { assistant.classList.add('error'); assistantBody.textContent += update.content; setStatus(update.content, 'error'); }
      }
    }
    await refresh();
  } catch (error) {
    assistant.classList.add('error'); assistantBody.textContent += `\n${error.message}`; setStatus(error.message, 'error');
  } finally { state.busy = false; sendButton.disabled = false; }
}

function fillSettings(settings = {}) {
  $('#setting-endpoint').value = settings.endpoint || '';
  $('#setting-model').value = settings.chatModel || '';
  $('#setting-path').value = settings.chatRequestPath || 'chat/completions';
  $('#setting-workspace').value = settings.novelWorkspacePath || '';
  $('#setting-compression-threshold').value = settings.compressionTokenThreshold || 100000;
}

function scrollConversation() { conversation.scrollTop = conversation.scrollHeight; }
function formatTime(value) { return value ? new Intl.DateTimeFormat('zh-CN', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value)) : ''; }
function formatFileSize(value) { return value < 1024 ? `${value} B` : value < 1048576 ? `${(value / 1024).toFixed(1)} KB` : `${(value / 1048576).toFixed(1)} MB`; }
function escapeHtml(value) { const node = document.createElement('span'); node.textContent = value; return node.innerHTML; }

$('#new-session').addEventListener('click', createSession);
$('#open-settings').addEventListener('click', () => settingsDialog.showModal());
$('#close-settings').addEventListener('click', () => settingsDialog.close());
document.querySelectorAll('.tab').forEach(tab => tab.addEventListener('click', async () => { state.inspector = tab.dataset.inspector; await loadInspector(); }));
$('#composer').addEventListener('submit', async (event) => { event.preventDefault(); const message = messageInput.value.trim(); if (!message || state.busy) return; messageInput.value = ''; await streamTask(message); });
messageInput.addEventListener('keydown', event => { if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') $('#composer').requestSubmit(); });
settingsForm.addEventListener('submit', async (event) => {
  event.preventDefault(); $('#settings-error').hidden = true;
  const payload = {
    endpoint: $('#setting-endpoint').value,
    apiKey: $('#setting-api-key').value,
    chatModel: $('#setting-model').value,
    chatRequestPath: $('#setting-path').value,
    novelWorkspacePath: $('#setting-workspace').value,
    compressionTokenThreshold: Number.parseInt($('#setting-compression-threshold').value, 10)
  };
  try { state.bootstrap = await request('/api/settings', { method: 'POST', body: JSON.stringify(payload) }); settingsDialog.close(); $('#setting-api-key').value = ''; await refresh(); }
  catch (error) { const node = $('#settings-error'); node.textContent = error.message; node.hidden = false; }
});

window.setInterval(() => {
  if (state.inspector === 'workspace' && state.bootstrap?.isConfigured && !document.hidden) {
    loadWorkspaceExplorer(false).catch(() => {});
  }
}, 1000);

refresh().catch(error => { setStatus(error.message, 'error'); settingsDialog.showModal(); });
