import type { NovelRunEvent, NovelStreamEvent } from '../types'

export class ApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message)
    this.name = 'ApiError'
  }
}

export async function requestJson<T>(url: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options.headers,
    },
  })
  if (!response.ok) throw await createApiError(response)
  return response.json() as Promise<T>
}

export async function requestVoid(url: string, options: RequestInit = {}): Promise<void> {
  const response = await fetch(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options.headers,
    },
  })
  if (!response.ok) throw await createApiError(response)
}

export async function streamChat(
  payload: { sessionId?: string | null; message: string },
  signal: AbortSignal,
  onEvent: (event: NovelStreamEvent) => void,
): Promise<void> {
  const response = await fetch('/api/chat/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
    signal,
  })
  if (!response.ok) throw await createApiError(response)
  if (!response.body) throw new ApiError('浏览器没有收到可读取的流式响应。', response.status)

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let completed = false
  try {
    while (true) {
      const { value, done } = await reader.read()
      buffer += decoder.decode(value, { stream: !done })
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''
      for (const line of lines) emitLine(line, onEvent)
      if (done) break
    }
    emitLine(buffer, onEvent)
    completed = true
  } finally {
    if (!completed) await reader.cancel().catch(() => {})
    reader.releaseLock()
  }
}

export async function subscribeRun(
  runId: string,
  afterSequence: number,
  signal: AbortSignal,
  onEvent: (event: NovelRunEvent) => void,
): Promise<void> {
  const response = await fetch(
    `/api/runs/${encodeURIComponent(runId)}/events?after=${Math.max(0, afterSequence)}`,
    { signal },
  )
  if (!response.ok) throw await createApiError(response)
  if (!response.body) throw new ApiError('浏览器没有收到可读取的任务事件流。', response.status)

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let completed = false
  try {
    while (true) {
      const { value, done } = await reader.read()
      buffer += decoder.decode(value, { stream: !done })
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''
      for (const line of lines) emitRunLine(line, onEvent)
      if (done) break
    }
    emitRunLine(buffer, onEvent)
    completed = true
  } finally {
    if (!completed) await reader.cancel().catch(() => {})
    reader.releaseLock()
  }
}

function emitLine(line: string, onEvent: (event: NovelStreamEvent) => void): void {
  if (!line.trim()) return
  const raw = JSON.parse(line) as Record<string, unknown>
  const event: NovelStreamEvent = {
    type: (raw.type ?? raw.Type) as NovelStreamEvent['type'],
    content: (raw.content ?? raw.Content) as string,
    toolCallId: (raw.toolCallId ?? raw.ToolCallId) as string | null | undefined,
    toolName: (raw.toolName ?? raw.ToolName) as string | null | undefined,
    toolArguments: (raw.toolArguments ?? raw.ToolArguments) as string | null | undefined,
    toolSucceeded: (raw.toolSucceeded ?? raw.ToolSucceeded) as boolean | null | undefined,
    compression: (raw.compression ?? raw.Compression) as NovelRunEvent['compression'],
  }
  if (!event.type || typeof event.content !== 'string') throw new Error('服务端返回了无效的流式事件。')
  onEvent(event)
}

function emitRunLine(line: string, onEvent: (event: NovelRunEvent) => void): void {
  if (!line.trim()) return
  const raw = JSON.parse(line) as Record<string, unknown>
  const event: NovelRunEvent = {
    runId: (raw.runId ?? raw.RunId) as string,
    sequence: Number(raw.sequence ?? raw.Sequence),
    occurredAt: (raw.occurredAt ?? raw.OccurredAt) as string,
    type: (raw.type ?? raw.Type) as NovelRunEvent['type'],
    content: (raw.content ?? raw.Content) as string,
    toolCallId: (raw.toolCallId ?? raw.ToolCallId) as string | null | undefined,
    toolName: (raw.toolName ?? raw.ToolName) as string | null | undefined,
    toolArguments: (raw.toolArguments ?? raw.ToolArguments) as string | null | undefined,
    toolSucceeded: (raw.toolSucceeded ?? raw.ToolSucceeded) as boolean | null | undefined,
  }
  if (!event.runId || !Number.isFinite(event.sequence) || !event.type || typeof event.content !== 'string') {
    throw new Error('服务端返回了无效的任务事件。')
  }
  onEvent(event)
}

async function createApiError(response: Response): Promise<ApiError> {
  const fallback = response.status === 429
    ? '已有任务仍在运行。请先停止当前任务，或等待服务端释放执行槽。'
    : `请求失败 (${response.status})`
  try {
    const problem = await response.json() as { detail?: string; title?: string }
    return new ApiError(problem.detail || problem.title || fallback, response.status)
  } catch {
    const text = await response.text().catch(() => '')
    return new ApiError(text || fallback, response.status)
  }
}
