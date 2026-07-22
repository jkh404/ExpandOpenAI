import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { describe, expect, it } from 'vitest'
import ChatWorkspace from '../src/components/ChatWorkspace.vue'
import type { RunState, UiMessage } from '../src/types'

const runState: RunState = {
  phase: 'waiting',
  detail: '模型仍在处理首个响应 · 任务仍在运行',
  elapsedSeconds: 65,
  startedAt: Date.now() - 65000,
  lastActivityAt: Date.now(),
  receivedFirstDelta: false,
}

const messages: UiMessage[] = [{
  id: 'assistant-1',
  role: 'assistant',
  content: '',
  state: 'streaming',
  tools: [{
    callId: 'call-1',
    name: 'write_workspace_file',
    arguments: '{"relativePath":"chapter.md"}',
    result: '',
    state: 'running',
  }],
}]

function mountWorkspace(
  workspaceMessages: UiMessage[] = messages,
  workspaceRunState: RunState = runState,
) {
  return mount(ChatWorkspace, {
    attachTo: document.body,
    props: {
      title: '规则怪谈：游乐园',
      messages: workspaceMessages,
      busy: true,
      loading: false,
      statusText: '等待模型响应',
      statusKind: 'busy',
      runState: workspaceRunState,
    },
  })
}

describe('ChatWorkspace', () => {
  it('shows elapsed progress, tool activity, and a stop control while running', async () => {
    const wrapper = mountWorkspace()
    expect(wrapper.get('[data-test="run-observer"]').text()).toContain('1:05')
    expect(wrapper.text()).toContain('write_workspace_file')
    expect(wrapper.get('[data-test="run-button"]').text()).toContain('停止执行')

    await wrapper.get('[data-test="run-button"]').trigger('click')
    expect(wrapper.emitted('stop')).toHaveLength(1)
    wrapper.unmount()
  })

  it('stops following when the user scrolls up and resumes from the latest button', async () => {
    const wrapper = mountWorkspace()
    const scroll = wrapper.get('.conversation').element as HTMLElement
    Object.defineProperty(scroll, 'scrollHeight', { configurable: true, value: 1000 })
    Object.defineProperty(scroll, 'clientHeight', { configurable: true, value: 400 })
    scroll.scrollTop = 100
    await wrapper.get('.conversation').trigger('scroll')

    const latest = wrapper.get('.jump-to-latest')
    expect(latest.attributes('style') || '').not.toContain('display: none')
    await latest.trigger('click')
    expect(scroll.scrollTop).toBe(1000)
    wrapper.unmount()
  })

  it('keeps following later streaming chunks after the user clicks back to latest', async () => {
    const streamingMessages: UiMessage[] = [{
      id: 'assistant-stream',
      role: 'assistant',
      content: '第一段',
      state: 'streaming',
      tools: [],
    }]
    const wrapper = mountWorkspace(streamingMessages)
    const scroll = wrapper.get('.conversation').element as HTMLElement
    let scrollHeight = 1000
    Object.defineProperty(scroll, 'scrollHeight', { configurable: true, get: () => scrollHeight })
    Object.defineProperty(scroll, 'clientHeight', { configurable: true, value: 400 })

    scroll.scrollTop = 100
    await wrapper.get('.conversation').trigger('scroll')
    await wrapper.get('.jump-to-latest').trigger('click')

    // 浏览器会把 scrollTop 限制在 scrollHeight - clientHeight，并触发 scroll。
    scroll.scrollTop = 600
    await wrapper.get('.conversation').trigger('scroll')

    scrollHeight = 1300
    streamingMessages[0].content += '\n第二段流式内容'
    await wrapper.setProps({
      runState: { ...runState, lastActivityAt: (runState.lastActivityAt ?? 0) + 1 },
    })
    await nextTick()

    expect(scroll.scrollTop).toBe(1300)
    wrapper.unmount()
  })
})
