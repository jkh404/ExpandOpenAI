<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import ChatWorkspace from './components/ChatWorkspace.vue'
import InspectorPanel from './components/InspectorPanel.vue'
import SessionModal from './components/SessionModal.vue'
import SessionSidebar from './components/SessionSidebar.vue'
import SettingsModal from './components/SettingsModal.vue'
import { useInspector } from './composables/useInspector'
import { useNovelAgent } from './composables/useNovelAgent'
import type { NovelSettingsRequest } from './types'

const agent = useNovelAgent()
const activeSessionId = computed(() => agent.bootstrap.value?.activeSessionId)
const inspector = useInspector(agent.isConfigured, activeSessionId)
const settingsOpen = ref(false)
const settingsSaving = ref(false)
const settingsError = ref('')
const sessionOpen = ref(false)
const sessionSaving = ref(false)
const sessionError = ref('')
const mobileInspectorOpen = ref(false)

watch(agent.isConfigured, configured => {
  if (!configured && !agent.loading.value) settingsOpen.value = true
})

onMounted(async () => {
  await agent.initialize()
  if (!agent.isConfigured.value) settingsOpen.value = true
})

async function saveSettings(settings: NovelSettingsRequest): Promise<void> {
  settingsSaving.value = true
  settingsError.value = ''
  try {
    await agent.saveSettings(settings)
    settingsOpen.value = false
    await inspector.refresh(true)
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : String(error)
  } finally {
    settingsSaving.value = false
  }
}

async function createSession(name: string): Promise<void> {
  sessionSaving.value = true
  sessionError.value = ''
  try {
    await agent.createSession(name)
    sessionOpen.value = false
    await inspector.refresh(true)
  } catch (error) {
    sessionError.value = error instanceof Error ? error.message : String(error)
  } finally {
    sessionSaving.value = false
  }
}

async function activateSession(id: string): Promise<void> {
  await agent.activateSession(id)
  await inspector.refresh(true)
}
</script>

<template>
  <div class="grain" aria-hidden="true" />
  <div class="app-shell">
    <SessionSidebar :sessions="agent.bootstrap.value?.sessions || []" :root-workspace-path="agent.bootstrap.value?.rootWorkspacePath || ''" :workspace-path="agent.bootstrap.value?.workspacePath || ''" :busy="agent.busy.value" @select="activateSession" @create="sessionOpen = true" @settings="settingsOpen = true" />
    <ChatWorkspace :title="agent.activeSession.value?.name || '小说企划'" :messages="agent.messages.value" :busy="agent.busy.value" :loading="agent.loading.value" :status-text="agent.statusText.value" :status-kind="agent.statusKind.value" :run-state="agent.runState" @send="agent.send" @stop="agent.stop" @open-inspector="mobileInspectorOpen = true" />
    <InspectorPanel :open="mobileInspectorOpen" :active-tab="inspector.activeTab.value" :workspace="inspector.workspace.value" :memories="inspector.memories.value" :description="inspector.description.value" :loading="inspector.loading.value" :error="inspector.error.value" :selected-file-path="inspector.selectedFilePath.value" :preview-content="inspector.previewContent.value" :preview-loading="inspector.previewLoading.value" :context="inspector.context.value" @close="mobileInspectorOpen = false" @select-tab="inspector.selectTab" @refresh="inspector.refresh(true)" @preview-file="inspector.previewFile" @close-preview="inspector.closePreview" @save-instructions="inspector.saveSessionInstructions" @save-global-memory="inspector.saveGlobalMemory" @remove-memory="inspector.removeMemory" />
  </div>

  <SettingsModal :open="settingsOpen" :settings="agent.bootstrap.value?.settings || null" :required="!agent.isConfigured.value" :saving="settingsSaving" :error="settingsError" @close="settingsOpen = false" @save="saveSettings" />
  <SessionModal :open="sessionOpen" :saving="sessionSaving" :error="sessionError" @close="sessionOpen = false" @create="createSession" />
</template>
