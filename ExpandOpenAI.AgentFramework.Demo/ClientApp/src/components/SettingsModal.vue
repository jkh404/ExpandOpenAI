<script setup lang="ts">
import { reactive, watch } from 'vue'
import type { NovelPublicSettings, NovelSettingsRequest } from '../types'

const props = defineProps<{
  open: boolean
  settings: NovelPublicSettings | null
  required: boolean
  saving: boolean
  error: string
}>()

const emit = defineEmits<{
  close: []
  save: [settings: NovelSettingsRequest]
}>()

const form = reactive<NovelSettingsRequest>({
  endpoint: '',
  apiKey: '',
  chatModel: '',
  chatRequestPath: 'chat/completions',
  novelWorkspacePath: '',
  compressionTokenThreshold: 100000,
  maximumOutputTokens: 16384,
  systemPrompt: '',
})

watch(
  () => [props.open, props.settings] as const,
  ([open, settings]) => {
    if (!open) return
    form.endpoint = settings?.endpoint || ''
    form.apiKey = ''
    form.chatModel = settings?.chatModel || ''
    form.chatRequestPath = settings?.chatRequestPath || 'chat/completions'
    form.novelWorkspacePath = settings?.novelWorkspacePath || ''
    form.compressionTokenThreshold = settings?.compressionTokenThreshold || 100000
    form.maximumOutputTokens = settings?.maximumOutputTokens || 16384
    form.systemPrompt = settings?.systemPrompt || settings?.defaultSystemPrompt || ''
  },
  { immediate: true },
)
</script>

<template>
  <div v-if="open" class="modal-backdrop" role="presentation" @mousedown.self="!required && emit('close')">
    <section class="modal modal-settings" role="dialog" aria-modal="true" aria-labelledby="settings-title">
      <header><div><p class="eyebrow">本机连接</p><h2 id="settings-title">小说撰写智能体</h2></div><button v-if="!required" type="button" aria-label="关闭设置" @click="emit('close')">×</button></header>
      <p class="modal-copy">API Key 只保存在本机，不会返回浏览器。保存后会重新载入模型连接、工作区和压缩配置。</p>
      <form @submit.prevent="emit('save', { ...form })">
        <label>服务 URL<input v-model.trim="form.endpoint" required placeholder="https://example.com/v1"></label>
        <label>API Key<input v-model="form.apiKey" type="password" :placeholder="settings?.endpoint ? '留空则保留当前密钥或使用环境变量' : '请输入 API Key'" :required="!settings?.endpoint"></label>
        <label>Chat 模型<input v-model.trim="form.chatModel" required placeholder="your-chat-model"></label>
        <label>Chat 请求路径<input v-model.trim="form.chatRequestPath" required></label>
        <label>小说根工作区<input v-model.trim="form.novelWorkspacePath" required placeholder="D:\Writing\Novels"></label>
        <label>上下文压缩阈值（tokens）<input v-model.number="form.compressionTokenThreshold" type="number" required min="8000" max="2000000" step="1000"><small>120k 模型建议 90k–100k；1M 模型可按需要设置约 800k。</small></label>
        <label>单次模型输出上限（tokens）<input v-model.number="form.maximumOutputTokens" type="number" required min="1000" max="131072" step="1000"><small>长章节的文件工具参数也占输出 tokens。默认 16,384；模型不支持时请按服务能力调低。</small></label>
        <label class="prompt-field">
          <span class="prompt-field-heading"><span>系统提示词</span><button type="button" @click="form.systemPrompt = settings?.defaultSystemPrompt || ''">恢复默认</button></span>
          <textarea v-model="form.systemPrompt" required rows="14" spellcheck="false" />
          <small>支持动态占位符：<code v-pre>{{assistant.name}}</code>、<code v-pre>{{utcNow}}</code>、<code v-pre>{{workspace.root}}</code>。保存后会重建智能体，但不会清除已有会话。</small>
        </label>
        <p v-if="error" class="form-error">{{ error }}</p>
        <footer><button type="submit" :disabled="saving">{{ saving ? '正在保存…' : '保存并打开写作台' }}</button></footer>
      </form>
    </section>
  </div>
</template>
