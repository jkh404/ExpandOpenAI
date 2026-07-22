import { onMounted, onUnmounted, ref, watch, type Ref } from 'vue'
import { requestJson, requestVoid } from '../lib/api'
import type {
  InspectorTab,
  NovelContextInspectorResponse,
  NovelInspectorResponse,
  NovelMemorySnippet,
  WorkspaceExplorerResponse,
  WorkspaceFilePreviewResponse,
} from '../types'

export function useInspector(isConfigured: Ref<boolean>, activeSessionId: Ref<string | null | undefined>) {
  const activeTab = ref<InspectorTab>('workspace')
  const workspace = ref<WorkspaceExplorerResponse>({ rootPath: '', files: [] })
  const memories = ref<NovelMemorySnippet[]>([])
  const description = ref('')
  const loading = ref(false)
  const error = ref('')
  const selectedFilePath = ref<string | null>(null)
  const previewContent = ref('')
  const previewLoading = ref(false)
  const context = ref<NovelContextInspectorResponse | null>(null)
  let pollTimer: number | undefined
  let requestSequence = 0
  let previewRequestSequence = 0
  let previewAbortController: AbortController | null = null

  async function selectTab(tab: InspectorTab): Promise<void> {
    cancelPreview()
    activeTab.value = tab
    await refresh(true)
  }

  async function refresh(force = false): Promise<void> {
    if (!isConfigured.value || loading.value && !force) return
    const sequence = ++requestSequence
    loading.value = true
    error.value = ''
    try {
      if (activeTab.value === 'workspace') {
        const response = await requestJson<WorkspaceExplorerResponse>('/api/workspace')
        if (sequence === requestSequence) workspace.value = response
      } else if (activeTab.value === 'context') {
        const response = await requestJson<NovelContextInspectorResponse>('/api/context')
        if (sequence === requestSequence) context.value = response
      } else {
        const response = await requestJson<NovelInspectorResponse>(`/api/memory/${activeTab.value}`)
        if (sequence === requestSequence) {
          memories.value = response.memories
          description.value = response.description
        }
      }
    } catch (reason) {
      if (sequence === requestSequence) error.value = reason instanceof Error ? reason.message : String(reason)
    } finally {
      if (sequence === requestSequence) loading.value = false
    }
  }

  async function previewFile(path: string): Promise<void> {
    const sequence = ++previewRequestSequence
    previewAbortController?.abort()
    const controller = new AbortController()
    previewAbortController = controller
    let timedOut = false
    const timeout = window.setTimeout(() => {
      timedOut = true
      controller.abort()
    }, 10_000)
    selectedFilePath.value = path
    previewContent.value = ''
    previewLoading.value = true
    try {
      const response = await requestJson<WorkspaceFilePreviewResponse>(
        `/api/workspace/file?path=${encodeURIComponent(path)}`,
        { signal: controller.signal },
      )
      if (sequence === previewRequestSequence && selectedFilePath.value === path) {
        previewContent.value = response.content
      }
    } catch (reason) {
      if (sequence === previewRequestSequence && selectedFilePath.value === path) {
        previewContent.value = timedOut
          ? '读取文件超时。请点击“重新读取”再试。'
          : reason instanceof Error ? reason.message : String(reason)
      }
    } finally {
      window.clearTimeout(timeout)
      if (previewAbortController === controller) previewAbortController = null
      if (sequence === previewRequestSequence) previewLoading.value = false
    }
  }

  function closePreview(): void {
    cancelPreview()
  }

  async function saveSessionInstructions(instructions: string): Promise<void> {
    if (!activeSessionId.value) return
    error.value = ''
    try {
      await requestVoid(`/api/sessions/${encodeURIComponent(activeSessionId.value)}/instructions`, {
        method: 'PUT',
        body: JSON.stringify({ instructions }),
      })
      await refresh(true)
    } catch (reason) {
      error.value = reason instanceof Error ? reason.message : String(reason)
    }
  }

  async function saveGlobalMemory(id: string, content: string): Promise<void> {
    error.value = ''
    try {
      await requestVoid(`/api/memory/global/${encodeURIComponent(id)}`, {
        method: 'PUT',
        body: JSON.stringify({ content }),
      })
      if (activeTab.value === 'global') await refresh(true)
    } catch (reason) {
      error.value = reason instanceof Error ? reason.message : String(reason)
    }
  }

  async function removeMemory(scope: 'session' | 'global', id: string): Promise<void> {
    error.value = ''
    try {
      await requestVoid(`/api/memory/${scope}/${encodeURIComponent(id)}`, { method: 'DELETE' })
      await refresh(true)
    } catch (reason) {
      error.value = reason instanceof Error ? reason.message : String(reason)
    }
  }

  function cancelPreview(): void {
    previewRequestSequence++
    previewAbortController?.abort()
    previewAbortController = null
    selectedFilePath.value = null
    previewContent.value = ''
    previewLoading.value = false
  }

  watch(activeSessionId, () => {
    cancelPreview()
    if (activeTab.value !== 'workspace') void refresh(true)
  })
  watch(isConfigured, configured => {
    if (configured) void refresh(true)
  })

  onMounted(() => {
    void refresh(true)
    pollTimer = window.setInterval(() => {
      if (document.visibilityState === 'visible'
        && (activeTab.value === 'workspace' || activeTab.value === 'context')) void refresh(false)
    }, 1000)
  })

  onUnmounted(() => {
    if (pollTimer !== undefined) window.clearInterval(pollTimer)
    cancelPreview()
  })

  return {
    activeTab,
    workspace,
    memories,
    description,
    loading,
    error,
    selectedFilePath,
    previewContent,
    previewLoading,
    context,
    selectTab,
    refresh,
    previewFile,
    closePreview,
    saveSessionInstructions,
    saveGlobalMemory,
    removeMemory,
  }
}
