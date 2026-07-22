import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useInspector } from '../src/composables/useInspector'

const api = vi.hoisted(() => ({ requestJson: vi.fn() }))

vi.mock('../src/lib/api', () => ({ requestJson: api.requestJson }))

afterEach(() => {
  vi.useRealTimers()
  api.requestJson.mockReset()
})

describe('useInspector', () => {
  it('ends file preview loading with a retryable timeout instead of spinning forever', async () => {
    vi.useFakeTimers()
    api.requestJson.mockImplementation((url: string, options?: RequestInit) => {
      if (url.startsWith('/api/workspace/file')) {
        return new Promise((_, reject) => {
          options?.signal?.addEventListener('abort', () => {
            reject(new DOMException('Aborted', 'AbortError'))
          })
        })
      }

      return Promise.resolve({ rootPath: 'D:/Novel', files: [] })
    })

    let inspector!: ReturnType<typeof useInspector>
    const Harness = defineComponent({
      setup() {
        inspector = useInspector(ref(true), ref('session-1'))
        return () => h('div')
      },
    })
    const wrapper = mount(Harness)
    await flushPromises()

    void inspector.previewFile('chapter-01.md')
    expect(inspector.previewLoading.value).toBe(true)
    await vi.advanceTimersByTimeAsync(12_000)
    await flushPromises()

    expect(inspector.previewLoading.value).toBe(false)
    expect(inspector.previewContent.value).toContain('超时')
    wrapper.unmount()
  })
})
