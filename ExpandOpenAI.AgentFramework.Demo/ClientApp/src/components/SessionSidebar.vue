<script setup lang="ts">
import { formatTime } from '../lib/format'
import type { NovelSessionSummary } from '../types'

defineProps<{
  sessions: NovelSessionSummary[]
  rootWorkspacePath: string
  workspacePath: string
  busy: boolean
}>()

const emit = defineEmits<{
  select: [id: string]
  create: []
  settings: []
}>()
</script>

<template>
  <aside class="sidebar">
    <div class="brand"><span class="brand-mark">墨</span><div><p class="eyebrow">EXPANDOPENAI</p><h1>墨痕</h1></div></div>
    <div class="sidebar-heading"><span>小说会话</span><button type="button" :disabled="busy" title="新建小说会话" @click="emit('create')">＋</button></div>
    <nav class="session-list" aria-label="小说会话">
      <button v-for="session in sessions" :key="session.id" type="button" class="session-item" :class="{ 'is-active': session.isActive }" :disabled="busy" @click="emit('select', session.id)">
        <span>{{ session.name }}</span><small>{{ formatTime(session.lastOpenedAt) }}</small>
      </button>
    </nav>
    <div class="sidebar-footer">
      <div class="workspace-location">
        <small>根工作区</small><p :title="rootWorkspacePath">{{ rootWorkspacePath || '尚未配置' }}</p>
        <small>当前会话工作区</small><p :title="workspacePath">{{ workspacePath || '尚未选择会话' }}</p>
      </div>
      <button type="button" :disabled="busy" @click="emit('settings')">连接与工作区设置</button>
    </div>
  </aside>
</template>
