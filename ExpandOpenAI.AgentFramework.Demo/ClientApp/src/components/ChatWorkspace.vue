<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { formatDuration } from '../lib/format'
import type { RunState, UiMessage } from '../types'

const props = defineProps<{
  title: string
  messages: UiMessage[]
  busy: boolean
  loading: boolean
  statusText: string
  statusKind: 'ready' | 'busy' | 'error'
  runState: RunState
}>()

const emit = defineEmits<{
  send: [message: string]
  stop: []
  openInspector: []
}>()

const draft = ref('')
const conversation = ref<HTMLElement | null>(null)
const autoFollow = ref(true)
const showJumpToLatest = ref(false)

const phaseTitle = computed(() => {
  switch (props.runState.phase) {
    case 'preparing': return '准备任务'
    case 'waiting': return props.runState.receivedFirstDelta ? '等待后续响应' : '等待模型首个响应'
    case 'streaming': return '正在接收内容'
    case 'tool': return '正在执行工具链'
    case 'stopping': return '正在停止'
    case 'stopped': return '任务已停止'
    case 'error': return '执行异常'
    default: return '等待指令'
  }
})

watch(
  () => [props.messages, props.runState.lastActivityAt] as const,
  async () => {
    await nextTick()
    if (autoFollow.value) scrollToLatest()
    else updateFollowButton()
  },
  { deep: true, flush: 'post' },
)

function submit(): void {
  if (props.busy) {
    emit('stop')
    return
  }
  const message = draft.value.trim()
  if (!message) return
  autoFollow.value = true
  draft.value = ''
  emit('send', message)
  void nextTick(scrollToLatest)
}

function handleKeydown(event: KeyboardEvent): void {
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
    event.preventDefault()
    submit()
  }
}

function handleScroll(): void {
  autoFollow.value = isNearBottom()
  updateFollowButton()
}

function isNearBottom(): boolean {
  const element = conversation.value
  if (!element) return true
  return element.scrollHeight - element.scrollTop - element.clientHeight < 56
}

function updateFollowButton(): void {
  showJumpToLatest.value = !autoFollow.value && !isNearBottom()
}

function scrollToLatest(): void {
  const element = conversation.value
  if (!element) return
  autoFollow.value = true
  element.scrollTop = element.scrollHeight
  showJumpToLatest.value = false
}

function labelForMessage(message: UiMessage): string {
  if (message.role === 'user') return '你的创作指令'
  if (message.state === 'error') return '执行异常'
  if (message.state === 'stopped') return '已停止的输出'
  return '小说撰写智能体'
}
</script>

<template>
  <main class="writing-desk">
    <header class="desk-header">
      <div class="desk-heading">
        <p class="eyebrow">持续创作空间</p>
        <h2>{{ title }}</h2>
      </div>
      <div class="header-actions">
        <button class="inspector-toggle" type="button" @click="emit('openInspector')">资料</button>
        <div class="live-status" :class="[`is-${statusKind}`, { 'is-running': busy }]">
          <span class="status-dot" aria-hidden="true" />
          <span>{{ statusText }}</span>
          <time v-if="busy">{{ formatDuration(runState.elapsedSeconds) }}</time>
        </div>
      </div>
    </header>

    <div class="conversation-shell">
      <section ref="conversation" class="conversation" aria-live="polite" @scroll.passive="handleScroll">
        <div v-if="loading" class="empty-state"><span>✦</span><h3>正在恢复写作现场</h3><p>加载会话、记忆与工作区资料…</p></div>
        <div v-else-if="messages.length === 0" class="empty-state"><span>✦</span><h3>从一条创作指令开始</h3><p>智能体会读取资料、查询信息、调用工具并持续写作。</p></div>

        <article v-for="message in messages" :key="message.id" class="message" :class="[message.role, `is-${message.state}`]">
          <p class="message-role">{{ labelForMessage(message) }}</p>

          <div v-if="message.role === 'assistant' && message.state === 'streaming'" class="run-observer" data-test="run-observer">
            <span class="run-spinner" aria-hidden="true" />
            <div>
              <strong>{{ phaseTitle }}</strong>
              <p>{{ runState.detail }}</p>
              <small>已运行 {{ formatDuration(runState.elapsedSeconds) }} · 每 5 秒更新连接心跳 · 可随时停止</small>
            </div>
          </div>

          <div v-if="message.content" class="message-body">{{ message.content }}</div>

          <section v-if="message.tools.length" class="tool-trace" aria-label="工具活动">
            <header><span>工具活动</span><small>{{ message.tools.length }} 次</small></header>
            <details v-for="tool in message.tools" :key="tool.callId" :open="tool.state === 'running'" class="tool-entry">
              <summary>
                <span class="tool-state" :class="`is-${tool.state}`" />
                <strong>{{ tool.name }}</strong>
                <small>{{ tool.state === 'running' ? '执行中' : tool.state === 'success' ? '已完成' : '失败' }}</small>
              </summary>
              <div class="tool-detail">
                <label>参数</label><pre>{{ tool.arguments }}</pre>
                <template v-if="tool.result"><label>结果</label><pre>{{ tool.result }}</pre></template>
              </div>
            </details>
          </section>
        </article>
      </section>

      <button v-show="showJumpToLatest" class="jump-to-latest" type="button" title="回到最新消息" aria-label="回到最新消息" @click="scrollToLatest">↓</button>
    </div>

    <form class="composer" @submit.prevent="submit">
      <label class="sr-only" for="message">创作指令</label>
      <textarea id="message" v-model="draft" rows="3" :disabled="busy" placeholder="交代下一步创作：阅读、研究、写作，并自行连续调用工具…" @keydown="handleKeydown" />
      <div class="composer-footer">
        <span>{{ busy ? '任务执行中，可点击停止' : 'Enter 换行 · Ctrl / ⌘ + Enter 执行' }}</span>
        <button data-test="run-button" type="submit" :class="{ 'is-stopping': busy }">
          {{ busy ? '停止执行' : '流式执行' }} <span>{{ busy ? '■' : '↗' }}</span>
        </button>
      </div>
    </form>
  </main>
</template>
