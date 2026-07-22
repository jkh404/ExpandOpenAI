<script setup lang="ts">
import { ref } from 'vue'
import { formatTime } from '../lib/format'
import type { InspectorTab, NovelContextInspectorResponse, NovelMemorySnippet, WorkspaceExplorerResponse } from '../types'
import ContextInspector from './ContextInspector.vue'
import WorkspaceExplorer from './WorkspaceExplorer.vue'

defineProps<{
  open: boolean
  activeTab: InspectorTab
  workspace: WorkspaceExplorerResponse
  memories: NovelMemorySnippet[]
  description: string
  loading: boolean
  error: string
  selectedFilePath: string | null
  previewContent: string
  previewLoading: boolean
  context: NovelContextInspectorResponse | null
}>()

const emit = defineEmits<{
  close: []
  selectTab: [tab: InspectorTab]
  refresh: []
  previewFile: [path: string]
  closePreview: []
  saveInstructions: [instructions: string]
  saveGlobalMemory: [id: string, content: string]
  removeMemory: [scope: 'session' | 'global', id: string]
}>()

const globalMemoryId = ref('')
const globalMemoryContent = ref('')

function submitGlobalMemory(): void {
  if (!globalMemoryId.value.trim() || !globalMemoryContent.value.trim()) return
  emit('saveGlobalMemory', globalMemoryId.value.trim(), globalMemoryContent.value.trim())
  globalMemoryId.value = ''
  globalMemoryContent.value = ''
}

function confirmRemoveMemory(scope: 'session' | 'global', id: string): void {
  if (window.confirm(`确定删除记忆“${id}”吗？此操作不会删除小说文件。`)) {
    emit('removeMemory', scope, id)
  }
}
</script>

<template>
  <div v-if="open" class="mobile-scrim" @click="emit('close')" />
  <aside class="inspector" :class="{ 'is-open': open }">
    <header class="inspector-header"><div><p class="eyebrow">项目检查器</p><h2>可召回资料</h2></div><button class="panel-close" type="button" aria-label="关闭检查器" @click="emit('close')">×</button></header>
    <div class="tabs" role="tablist">
      <button v-for="tab in (['workspace', 'context', 'session', 'global'] as InspectorTab[])" :key="tab" type="button" class="tab" :class="{ 'is-active': activeTab === tab }" @click="emit('selectTab', tab)">
        {{ tab === 'workspace' ? '工作区' : tab === 'context' ? '上下文' : tab === 'session' ? '会话记忆' : '全局记忆' }}
      </button>
    </div>
    <section class="inspector-content">
      <p v-if="error" class="inspector-error">{{ error }}</p>
      <p v-else-if="loading && activeTab !== 'workspace' && activeTab !== 'context'" class="empty-inspector">正在读取资料…</p>
      <WorkspaceExplorer v-else-if="activeTab === 'workspace'" :workspace="workspace" :selected-path="selectedFilePath" :preview-content="previewContent" :preview-loading="previewLoading" @refresh="emit('refresh')" @select="emit('previewFile', $event)" @close-preview="emit('closePreview')" />
      <ContextInspector v-else-if="activeTab === 'context'" :data="context" @save-instructions="emit('saveInstructions', $event)" />
      <template v-else>
        <p class="memory-description">{{ description }}</p>
        <form v-if="activeTab === 'global'" class="memory-editor" @submit.prevent="submitGlobalMemory">
          <input v-model.trim="globalMemoryId" required placeholder="标识，例如 user-name">
          <textarea v-model.trim="globalMemoryContent" required rows="3" placeholder="跨小说稳定适用的称呼、偏好或协作约定" />
          <button type="submit">保存全局偏好</button>
        </form>
        <p v-if="memories.length === 0" class="empty-inspector">{{ activeTab === 'session' ? '尚无压缩归档。多轮创作后，较早对话摘要会出现在这里。' : '尚无跨小说偏好。只有稳定的通用写作习惯才会保存。' }}</p>
        <div v-else class="memory-list">
          <article v-for="memory in memories" :key="memory.id" class="memory-item">
            <header><h3>{{ memory.id }}</h3><button type="button" title="删除记忆" @click="confirmRemoveMemory(activeTab === 'session' ? 'session' : 'global', memory.id)">删除</button></header>
            <p>{{ memory.content }}</p><time>{{ formatTime(memory.createdAt) }}</time>
          </article>
        </div>
      </template>
    </section>
    <p class="inspector-note">工作区与上下文状态自动刷新；会话记忆属于当前小说；全局记忆仅保存跨小说偏好。</p>
  </aside>
</template>
