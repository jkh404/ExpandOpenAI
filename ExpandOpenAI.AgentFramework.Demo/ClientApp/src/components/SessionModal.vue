<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'

const props = defineProps<{ open: boolean; saving: boolean; error: string }>()
const emit = defineEmits<{ close: []; create: [name: string] }>()
const name = ref('')
const input = ref<HTMLInputElement | null>(null)

watch(() => props.open, async open => {
  if (!open) return
  name.value = ''
  await nextTick()
  input.value?.focus()
})
</script>

<template>
  <div v-if="open" class="modal-backdrop" @mousedown.self="emit('close')">
    <section class="modal modal-small" role="dialog" aria-modal="true" aria-labelledby="session-title">
      <header><div><p class="eyebrow">新作品</p><h2 id="session-title">创建小说会话</h2></div><button type="button" aria-label="关闭" @click="emit('close')">×</button></header>
      <form @submit.prevent="emit('create', name)">
        <label>会话名称<input ref="input" v-model.trim="name" placeholder="例如：规则怪谈：游乐园"></label>
        <p class="modal-copy">每个小说会话拥有独立历史和会话记忆，不会与其他作品串设定。</p>
        <p v-if="error" class="form-error">{{ error }}</p>
        <footer><button type="button" class="secondary-button" @click="emit('close')">取消</button><button type="submit" :disabled="saving">{{ saving ? '正在创建…' : '创建会话' }}</button></footer>
      </form>
    </section>
  </div>
</template>
