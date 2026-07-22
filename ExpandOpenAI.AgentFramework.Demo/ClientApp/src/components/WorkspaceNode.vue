<script setup lang="ts">
import { formatFileSize, formatTime } from '../lib/format'
import type { NovelWorkspaceFileEntry } from '../types'

export interface WorkspaceTreeNode {
  name: string
  path: string
  folders: WorkspaceTreeNode[]
  files: NovelWorkspaceFileEntry[]
}

defineProps<{
  node: WorkspaceTreeNode
  selectedPath: string | null
}>()

const emit = defineEmits<{ select: [path: string] }>()
</script>

<template>
  <details class="workspace-folder" open>
    <summary>{{ node.name }}</summary>
    <div class="workspace-folder-children">
      <WorkspaceNode v-for="folder in node.folders" :key="folder.path" :node="folder" :selected-path="selectedPath" @select="emit('select', $event)" />
      <button v-for="file in node.files" :key="file.path" type="button" class="workspace-file" :class="{ 'is-selected': file.path === selectedPath }" :title="`${file.path}\n更新于 ${formatTime(file.lastModifiedAt)}`" @click="emit('select', file.path)">
        <span class="file-badge">{{ file.extension.toUpperCase() }}</span>
        <span><strong>{{ file.name }}</strong><small>{{ formatFileSize(file.size) }} · {{ formatTime(file.lastModifiedAt) }}</small></span>
      </button>
    </div>
  </details>
</template>
