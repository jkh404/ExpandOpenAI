<script setup lang="ts">
import { computed } from 'vue'
import type { NovelWorkspaceFileEntry, WorkspaceExplorerResponse } from '../types'
import WorkspaceNode, { type WorkspaceTreeNode } from './WorkspaceNode.vue'

const props = defineProps<{
  workspace: WorkspaceExplorerResponse
  selectedPath: string | null
  previewContent: string
  previewLoading: boolean
}>()

const emit = defineEmits<{
  refresh: []
  select: [path: string]
  closePreview: []
}>()

const tree = computed(() => buildTree(props.workspace.files))

function buildTree(files: NovelWorkspaceFileEntry[]): WorkspaceTreeNode {
  const root: WorkspaceTreeNode = { name: '工作区', path: '', folders: [], files: [] }
  for (const file of files) {
    const segments = file.path.split('/')
    let current = root
    let currentPath = ''
    for (const segment of segments.slice(0, -1)) {
      currentPath = currentPath ? `${currentPath}/${segment}` : segment
      let folder = current.folders.find(item => item.name === segment)
      if (!folder) {
        folder = { name: segment, path: currentPath, folders: [], files: [] }
        current.folders.push(folder)
      }
      current = folder
    }
    current.files.push(file)
  }
  sortNode(root)
  return root
}

function sortNode(node: WorkspaceTreeNode): void {
  node.folders.sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'))
  node.files.sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'))
  node.folders.forEach(sortNode)
}
</script>

<template>
  <div class="workspace-toolbar">
    <div><strong>{{ workspace.files.length }} 个文件</strong><small :title="workspace.rootPath">{{ workspace.rootPath }}</small></div>
    <button type="button" title="立即刷新" @click="emit('refresh')">↻</button>
  </div>
  <p v-if="workspace.files.length === 0" class="empty-inspector">工作区中还没有 .txt 或 .md 文件。智能体创建文件后会自动出现。</p>
  <div v-else class="workspace-tree">
    <WorkspaceNode :node="tree" :selected-path="selectedPath" @select="emit('select', $event)" />
  </div>
  <section v-if="selectedPath" class="workspace-preview">
    <header><strong>{{ selectedPath }}</strong><span><button v-if="!previewLoading" type="button" title="重新读取" @click="emit('select', selectedPath)">↻</button><button type="button" title="关闭预览" @click="emit('closePreview')">×</button></span></header>
    <pre>{{ previewLoading ? '正在读取文件…' : previewContent }}</pre>
  </section>
</template>
