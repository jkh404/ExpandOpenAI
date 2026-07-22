import { computed, onUnmounted, reactive, ref } from 'vue'
import { requestJson, subscribeRun } from '../lib/api'
import type {
  NovelBootstrapResponse,
  NovelRunEvent,
  NovelRunSummary,
  NovelSessionSummary,
  NovelSettingsRequest,
  RunState,
  UiMessage,
} from '../types'

export function useNovelAgent() {
  const bootstrap = ref<NovelBootstrapResponse | null>(null)
  const messages = ref<UiMessage[]>([])
  const loading = ref(true)
  const busy = ref(false)
  const statusText = ref('正在连接本机智能体…')
  const statusKind = ref<'ready' | 'busy' | 'error'>('busy')
  const subscriptionController = ref<AbortController | null>(null)
  const currentRun = ref<NovelRunSummary | null>(null)
  const runState = reactive<RunState>({
    phase: 'idle',
    detail: '',
    elapsedSeconds: 0,
    startedAt: null,
    lastActivityAt: null,
    receivedFirstDelta: false,
  })
  let runClock: number | undefined

  const isConfigured = computed(() => bootstrap.value?.isConfigured === true)
  const activeSession = computed(() => bootstrap.value?.sessions.find(session => session.isActive) ?? null)

  async function initialize(): Promise<void> {
    loading.value = true
    try {
      await refreshBootstrap(true)
      if (isConfigured.value) await resumeActiveRun()
    } catch (error) {
      setError(error)
    } finally {
      loading.value = false
    }
  }

  async function refreshBootstrap(replaceConversation: boolean): Promise<void> {
    const response = await requestJson<NovelBootstrapResponse>('/api/bootstrap')
    bootstrap.value = response
    if (replaceConversation) {
      messages.value = mapConversation(response.conversation)
    }
    if (!response.isConfigured) {
      statusText.value = response.configurationError || '请先配置模型服务。'
      statusKind.value = 'error'
    } else if (!busy.value) {
      statusText.value = '会话已恢复，可以继续创作'
      statusKind.value = 'ready'
    }
  }

  async function send(message: string): Promise<void> {
    const command = message.trim()
    if (!command || busy.value || !bootstrap.value?.isConfigured) return

    const assistantMessage: UiMessage = {
      id: createId(),
      role: 'assistant',
      content: '',
      state: 'streaming',
      tools: [],
    }
    messages.value.push(
      { id: createId(), role: 'user', content: command, state: 'complete', tools: [] },
      assistantMessage,
    )
    // 后续流式更新必须写入 Vue 代理对象，否则正文虽然会借助其他状态重绘，
    // 但依赖消息变化的自动滚动监听收不到通知。
    const assistant = messages.value[messages.value.length - 1]

    busy.value = true
    startRunClock()
    updateRun('preparing', '正在准备上下文、长期记忆和可用工具…')

    try {
      const run = await requestJson<NovelRunSummary>('/api/runs', {
        method: 'POST',
        body: JSON.stringify({ sessionId: bootstrap.value.activeSessionId, message: command }),
      })
      currentRun.value = run
      startRunClock(run.startedAt)
      void followRun(run, assistant)
    } catch (error) {
      assistant.state = 'error'
      assistant.content += `\n${toErrorMessage(error)}`
      updateRun('error', toErrorMessage(error))
      setError(error)
      busy.value = false
      stopRunClock()
    }
  }

  async function stop(): Promise<void> {
    if (!busy.value || !currentRun.value) return
    updateRun('stopping', '正在通知模型和工具停止当前任务…')
    try {
      currentRun.value = await requestJson<NovelRunSummary>(
        `/api/runs/${encodeURIComponent(currentRun.value.id)}`,
        { method: 'DELETE' },
      )
    } catch (error) {
      setError(error)
    }
  }

  async function resumeActiveRun(): Promise<void> {
    const sessionId = bootstrap.value?.activeSessionId
    if (!sessionId) return
    const run = await requestJson<NovelRunSummary | null>(
      `/api/runs/active?sessionId=${encodeURIComponent(sessionId)}`,
    )
    if (!run) return

    const assistantMessage: UiMessage = {
      id: createId(),
      role: 'assistant',
      content: '',
      state: 'streaming',
      tools: [],
    }
    messages.value.push(
      { id: createId(), role: 'user', content: run.command, state: 'complete', tools: [] },
      assistantMessage,
    )
    const assistant = messages.value[messages.value.length - 1]
    currentRun.value = run
    busy.value = true
    startRunClock(run.startedAt)
    updateRun('waiting', '已重新连接后台任务，正在恢复执行事件…')
    void followRun(run, assistant)
  }

  async function followRun(run: NovelRunSummary, assistant: UiMessage): Promise<void> {
    subscriptionController.value?.abort()
    const controller = new AbortController()
    subscriptionController.value = controller
    let lastSequence = 0

    try {
      while (!controller.signal.aborted) {
        try {
          await subscribeRun(run.id, lastSequence, controller.signal, event => {
            if (event.sequence <= lastSequence) return
            lastSequence = event.sequence
            handleStreamEvent(event, assistant)
          })
        } catch (error) {
          if (controller.signal.aborted || isAbortError(error)) return
          updateRun('waiting', `任务仍在后台运行，事件连接中断，正在重连：${toErrorMessage(error)}`)
          await waitForReconnect(controller.signal)
        }

        let snapshot: NovelRunSummary
        try {
          snapshot = await requestJson<NovelRunSummary>(`/api/runs/${encodeURIComponent(run.id)}`)
        } catch (error) {
          if (controller.signal.aborted || isAbortError(error)) return
          updateRun('waiting', `后台任务状态暂时不可用，正在重试：${toErrorMessage(error)}`)
          await waitForReconnect(controller.signal)
          continue
        }
        currentRun.value = snapshot
        if (snapshot.isTerminal) {
          await finalizeRun(snapshot, assistant)
          return
        }

        updateRun('waiting', '后台任务仍在运行，正在重新订阅最新事件…')
        await waitForReconnect(controller.signal)
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        assistant.state = 'error'
        assistant.content += `\n${toErrorMessage(error)}`
        updateRun('error', toErrorMessage(error))
        setError(error)
        busy.value = false
        stopRunClock()
      }
    } finally {
      if (subscriptionController.value === controller) subscriptionController.value = null
    }
  }

  async function finalizeRun(run: NovelRunSummary, assistant: UiMessage): Promise<void> {
    if (run.status === 'completed') {
      assistant.state = 'complete'
      statusText.value = '本轮任务已完成'
      statusKind.value = 'ready'
    } else if (run.status === 'stopped') {
      assistant.state = 'stopped'
      if (!assistant.content.includes('[已停止执行]')) {
        assistant.content += assistant.content.trim()
          ? '\n\n[已停止执行]'
          : '（已停止执行，尚未收到模型内容）'
      }
      updateRun('stopped', '任务已由用户停止，当前部分输出已保留。')
      statusText.value = '已停止执行'
      statusKind.value = 'ready'
    } else {
      assistant.state = 'error'
      const error = run.error || (run.status === 'interrupted'
        ? '服务在任务执行期间退出，任务已中断。'
        : '后台任务执行失败。')
      if (!assistant.content.includes(error)) assistant.content += `\n${error}`
      updateRun('error', error)
    }

    currentRun.value = run
    busy.value = false
    stopRunClock()
    await refreshBootstrap(false)
  }

  async function activateSession(sessionId: string): Promise<void> {
    if (busy.value || sessionId === bootstrap.value?.activeSessionId) return
    loading.value = true
    try {
      bootstrap.value = await requestJson<NovelBootstrapResponse>(
        `/api/sessions/${encodeURIComponent(sessionId)}/activate`,
        { method: 'POST' },
      )
      messages.value = mapConversation(bootstrap.value.conversation)
      statusText.value = '会话已切换'
      statusKind.value = 'ready'
    } catch (error) {
      setError(error)
    } finally {
      loading.value = false
    }
  }

  async function createSession(name: string): Promise<void> {
    if (busy.value) return
    await requestJson<NovelSessionSummary>('/api/sessions', {
      method: 'POST',
      body: JSON.stringify({ name: name.trim() || null }),
    })
    await refreshBootstrap(true)
  }

  async function saveSettings(settings: NovelSettingsRequest): Promise<void> {
    if (busy.value) throw new Error('请先停止当前任务，再修改连接或工作区设置。')
    bootstrap.value = await requestJson<NovelBootstrapResponse>('/api/settings', {
      method: 'POST',
      body: JSON.stringify(settings),
    })
    messages.value = mapConversation(bootstrap.value.conversation)
    statusText.value = '设置已保存，写作运行时已重新载入'
    statusKind.value = 'ready'
  }

  function handleStreamEvent(event: NovelRunEvent, assistant: UiMessage): void {
    runState.lastActivityAt = Date.now()
    if (event.type === 'status') {
      updateRun('waiting', event.content)
      return
    }
    if (event.type === 'heartbeat') {
      runState.detail = runState.receivedFirstDelta
        ? `连接保持中 · ${event.content}`
        : `模型仍在处理首个响应 · ${event.content}`
      statusText.value = runState.detail
      return
    }
    if (event.type === 'delta') {
      runState.receivedFirstDelta = true
      assistant.content += event.content
      if (event.content.includes('[工具调用:')) updateRun('tool', '智能体正在调用工具并等待结果…')
      else if (event.content.includes('[工具结果:')) updateRun('tool', '工具已返回结果，智能体正在继续处理…')
      else updateRun('streaming', '正在接收模型输出…')
      return
    }
    if (event.type === 'tool_call') {
      assistant.tools.push({
        callId: event.toolCallId || createId(),
        name: event.toolName || 'unknown_tool',
        arguments: event.toolArguments || '{}',
        result: '',
        state: 'running',
      })
      updateRun('tool', `正在调用工具：${event.toolName || 'unknown_tool'}`)
      return
    }
    if (event.type === 'tool_result') {
      let activity = assistant.tools.find(tool => tool.callId === event.toolCallId)
      if (!activity) {
        activity = {
          callId: event.toolCallId || createId(),
          name: event.toolName || 'unknown_tool',
          arguments: '{}',
          result: '',
          state: 'running',
        }
        assistant.tools.push(activity)
      }
      if (activity) {
        activity.result = event.content
        activity.state = event.toolSucceeded === false ? 'error' : 'success'
        updateRun('tool', activity.state === 'success'
          ? `工具 ${activity.name} 已完成，智能体正在继续处理…`
          : `工具 ${activity.name} 执行失败，智能体正在评估下一步…`)
      }
      return
    }
    if (event.type === 'compression') {
      updateRun('waiting', event.content)
      return
    }
    if (event.type === 'error') {
      assistant.state = 'error'
      return
    }
    if (event.type === 'stopped') {
      assistant.state = 'stopped'
      return
    }
    if (event.type === 'complete') assistant.state = 'complete'
  }

  function updateRun(phase: RunState['phase'], detail: string): void {
    runState.phase = phase
    runState.detail = detail
    runState.lastActivityAt = Date.now()
    statusText.value = detail
    statusKind.value = phase === 'error' ? 'error' : phase === 'stopped' ? 'ready' : 'busy'
  }

  function startRunClock(startedAt?: string): void {
    stopRunClock()
    runState.phase = 'preparing'
    runState.elapsedSeconds = 0
    runState.startedAt = startedAt ? Date.parse(startedAt) : Date.now()
    runState.lastActivityAt = Date.now()
    runState.receivedFirstDelta = false
    runClock = window.setInterval(() => {
      if (runState.startedAt) runState.elapsedSeconds = Math.floor((Date.now() - runState.startedAt) / 1000)
    }, 1000)
  }

  function stopRunClock(): void {
    if (runClock !== undefined) window.clearInterval(runClock)
    runClock = undefined
  }

  function setError(error: unknown): void {
    statusText.value = toErrorMessage(error)
    statusKind.value = 'error'
  }

  onUnmounted(() => {
    subscriptionController.value?.abort()
    stopRunClock()
  })

  return {
    bootstrap,
    messages,
    loading,
    busy,
    statusText,
    statusKind,
    runState,
    currentRun,
    isConfigured,
    activeSession,
    initialize,
    send,
    stop,
    activateSession,
    createSession,
    saveSettings,
  }
}

function createId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function waitForReconnect(signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const onAbort = () => {
      window.clearTimeout(timeout)
      reject(new DOMException('Aborted', 'AbortError'))
    }
    const timeout = window.setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, 800)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

function mapConversation(items: NovelBootstrapResponse['conversation']): UiMessage[] {
  const messages: UiMessage[] = []
  let pendingTool: UiMessage | null = null
  for (const item of items) {
    const toolCall = item.role === 'assistant' && item.content.match(/^\[工具调用\]\s*(.+)$/s)
    if (toolCall) {
      pendingTool = {
        id: createId(),
        role: 'assistant',
        content: '',
        state: 'complete',
        tools: [{
          callId: createId(),
          name: toolCall[1].trim(),
          arguments: '（历史记录未保存参数）',
          result: '',
          state: 'running',
        }],
      }
      messages.push(pendingTool)
      continue
    }

    const toolResult = item.role === 'assistant' && item.content.match(/^\[工具结果\]\s*\n?([\s\S]*)$/)
    if (toolResult && pendingTool) {
      pendingTool.tools[0].result = toolResult[1].trim()
      pendingTool.tools[0].state = /error|exception|失败/i.test(toolResult[1]) ? 'error' : 'success'
      pendingTool = null
      continue
    }

    if (pendingTool) {
      pendingTool.tools[0].state = 'success'
      pendingTool.tools[0].result ||= '（历史记录未包含工具结果摘要）'
      pendingTool = null
    }
    messages.push({ id: createId(), role: item.role, content: item.content, state: 'complete', tools: [] })
  }
  if (pendingTool) {
    pendingTool.tools[0].state = 'success'
    pendingTool.tools[0].result ||= '（历史记录未包含工具结果摘要）'
  }
  return messages
}
