export interface NovelPublicSettings {
  endpoint: string
  chatModel: string
  chatRequestPath: string
  novelWorkspacePath?: string | null
  compressionTokenThreshold: number
  maximumOutputTokens: number
  systemPrompt: string
  defaultSystemPrompt: string
  systemPromptVersion: number
}

export interface NovelSessionSummary {
  id: string
  name: string
  lastOpenedAt: string
  isActive: boolean
}

export interface NovelConversationItem {
  role: 'user' | 'assistant'
  content: string
}

export interface NovelBootstrapResponse {
  isConfigured: boolean
  configurationError?: string | null
  settings: NovelPublicSettings
  sessions: NovelSessionSummary[]
  activeSessionId?: string | null
  conversation: NovelConversationItem[]
  rootWorkspacePath?: string | null
  workspacePath?: string | null
  sessionStatePath?: string | null
  globalMemoryStatePath?: string | null
}

export interface NovelMemorySnippet {
  scope: 'session' | 'global'
  id: string
  content: string
  createdAt: string
  metadata?: Record<string, string> | null
}

export interface NovelInspectorResponse {
  kind: 'session' | 'global'
  description: string
  memories: NovelMemorySnippet[]
}

export interface NovelWorkspaceFileEntry {
  path: string
  name: string
  extension: 'txt' | 'md'
  size: number
  lastModifiedAt: string
}

export interface WorkspaceExplorerResponse {
  rootPath: string
  files: NovelWorkspaceFileEntry[]
}

export interface WorkspaceFilePreviewResponse {
  path: string
  content: string
}

export type StreamEventType = 'status' | 'heartbeat' | 'delta' | 'tool_call' | 'tool_result' | 'compression' | 'complete' | 'stopped' | 'error'

export interface NovelStreamEvent {
  type: StreamEventType
  content: string
  toolCallId?: string | null
  toolName?: string | null
  toolArguments?: string | null
  toolSucceeded?: boolean | null
  compression?: NovelCompressionRecord | null
}

export interface NovelRunSummary {
  id: string
  sessionId: string
  command: string
  status: 'running' | 'stopping' | 'completed' | 'stopped' | 'failed' | 'interrupted'
  startedAt: string
  completedAt?: string | null
  lastSequence: number
  error?: string | null
  isTerminal: boolean
}

export interface NovelRunEvent extends NovelStreamEvent {
  runId: string
  sequence: number
  occurredAt: string
}

export interface NovelCompressionRecord {
  id: string
  occurredAt: string
  reason: string
  beforeMessageCount: number
  afterMessageCount: number
  beforeTokenEstimate: number
  afterTokenEstimate: number
  sessionMemoriesCreated: number
  globalMemoriesCreated: number
}

export interface NovelContextDiagnostics {
  sessionId: string
  activeHistoryMessageCount: number
  activeHistoryTokenEstimate: number
  compressionTokenThreshold: number
  maximumOutputTokens: number
  systemPromptVersion: number
  sessionInstructions: string
  sessionMemoryCount: number
  globalMemoryCount: number
  compressionHistory: NovelCompressionRecord[]
}

export interface NovelContextInspectorResponse {
  context: NovelContextDiagnostics
  recentRuns: NovelRunSummary[]
}

export interface ToolActivity {
  callId: string
  name: string
  arguments: string
  result: string
  state: 'running' | 'success' | 'error'
}

export type MessageState = 'complete' | 'streaming' | 'stopped' | 'error'

export interface UiMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  state: MessageState
  tools: ToolActivity[]
}

export type RunPhase = 'idle' | 'preparing' | 'waiting' | 'streaming' | 'tool' | 'stopping' | 'stopped' | 'error'

export interface RunState {
  phase: RunPhase
  detail: string
  elapsedSeconds: number
  startedAt: number | null
  lastActivityAt: number | null
  receivedFirstDelta: boolean
}

export interface NovelSettingsRequest {
  endpoint: string
  apiKey: string
  chatModel: string
  chatRequestPath: string
  novelWorkspacePath: string
  compressionTokenThreshold: number
  maximumOutputTokens: number
  systemPrompt: string
}

export type InspectorTab = 'workspace' | 'context' | 'session' | 'global'
