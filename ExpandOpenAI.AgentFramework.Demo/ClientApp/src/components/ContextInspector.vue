<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { formatTime } from '../lib/format'
import type { NovelContextInspectorResponse } from '../types'

const props = defineProps<{
  data: NovelContextInspectorResponse | null
}>()

const emit = defineEmits<{
  saveInstructions: [instructions: string]
}>()
const instructions = ref('')

watch(() => props.data?.context.sessionInstructions, value => {
  instructions.value = value || ''
}, { immediate: true })

const tokenPercent = computed(() => {
  const context = props.data?.context
  if (!context || context.compressionTokenThreshold <= 0) return 0
  return Math.min(100, Math.round(context.activeHistoryTokenEstimate / context.compressionTokenThreshold * 100))
})

function runStatus(status: string): string {
  return {
    running: '运行中',
    stopping: '正在停止',
    completed: '已完成',
    stopped: '已停止',
    failed: '失败',
    interrupted: '服务重启中断',
  }[status] || status
}
</script>

<template>
  <p v-if="!data" class="empty-inspector">正在计算上下文状态…</p>
  <div v-else class="context-inspector">
    <section class="context-meter">
      <header><strong>活动上下文</strong><span>{{ tokenPercent }}%</span></header>
      <div class="context-meter-track"><i :style="{ width: `${tokenPercent}%` }" /></div>
      <p>约 {{ data.context.activeHistoryTokenEstimate.toLocaleString() }} / {{ data.context.compressionTokenThreshold.toLocaleString() }} tokens</p>
    </section>

    <dl class="context-facts">
      <div><dt>活动消息</dt><dd>{{ data.context.activeHistoryMessageCount }}</dd></div>
      <div><dt>单次输出上限</dt><dd>{{ data.context.maximumOutputTokens.toLocaleString() }}</dd></div>
      <div><dt>会话记忆</dt><dd>{{ data.context.sessionMemoryCount }}</dd></div>
      <div><dt>全局偏好</dt><dd>{{ data.context.globalMemoryCount }}</dd></div>
    </dl>

    <section class="session-instructions">
      <header><strong>会话补充提示词</strong><small>基础提示词 v{{ data.context.systemPromptVersion }}</small></header>
      <textarea v-model="instructions" rows="5" placeholder="只对当前小说生效，例如叙事视角、禁用元素或章节格式。" />
      <button type="button" @click="emit('saveInstructions', instructions)">保存到当前会话</button>
    </section>

    <section class="context-history">
      <header><strong>最近压缩</strong><small>{{ data.context.compressionHistory.length }} 次记录</small></header>
      <p v-if="data.context.compressionHistory.length === 0" class="empty-inspector">尚未触发上下文压缩。</p>
      <article v-for="item in data.context.compressionHistory" :key="item.id">
        <div><strong>{{ item.beforeTokenEstimate.toLocaleString() }} → {{ item.afterTokenEstimate.toLocaleString() }}</strong><time>{{ formatTime(item.occurredAt) }}</time></div>
        <p>{{ item.beforeMessageCount }} → {{ item.afterMessageCount }} 条消息 · 新增 {{ item.sessionMemoriesCreated }} 条会话记忆 · {{ item.reason }}</p>
      </article>
    </section>

    <section class="context-history run-history">
      <header><strong>后台任务</strong><small>刷新页面后可恢复</small></header>
      <p v-if="data.recentRuns.length === 0" class="empty-inspector">还没有后台创作任务。</p>
      <article v-for="run in data.recentRuns" :key="run.id">
        <div><strong>{{ runStatus(run.status) }}</strong><time>{{ formatTime(run.startedAt) }}</time></div>
        <p>{{ run.command }}</p>
      </article>
    </section>
  </div>
</template>
