import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SettingsModal from '../src/components/SettingsModal.vue'

describe('SettingsModal', () => {
  it('edits, resets, and saves the system prompt and output token limit', async () => {
    const wrapper = mount(SettingsModal, {
      attachTo: document.body,
      props: {
        open: true,
        required: false,
        saving: false,
        error: '',
        settings: {
          endpoint: 'https://example.com/v1',
          chatModel: 'novel-model',
          chatRequestPath: 'chat/completions',
          novelWorkspacePath: 'D:\\Novels',
          compressionTokenThreshold: 100000,
          maximumOutputTokens: 16384,
          systemPrompt: '自定义提示词',
          defaultSystemPrompt: '默认提示词',
          systemPromptVersion: 1,
        },
      },
    })

    const prompt = wrapper.get('.prompt-field textarea')
    expect((prompt.element as HTMLTextAreaElement).value).toBe('自定义提示词')
    await wrapper.get('.prompt-field-heading button').trigger('click')
    expect((prompt.element as HTMLTextAreaElement).value).toBe('默认提示词')

    await prompt.setValue('新的系统提示词')
    const outputTokens = wrapper.findAll('input[type="number"]')[1]
    await outputTokens.setValue('24000')
    await wrapper.get('form').trigger('submit')

    const saved = wrapper.emitted('save')?.[0]?.[0] as { systemPrompt: string; maximumOutputTokens: number }
    expect(saved.systemPrompt).toBe('新的系统提示词')
    expect(saved.maximumOutputTokens).toBe(24000)
    wrapper.unmount()
  })
})
