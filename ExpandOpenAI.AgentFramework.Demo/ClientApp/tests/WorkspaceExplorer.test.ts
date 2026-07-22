import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import WorkspaceExplorer from '../src/components/WorkspaceExplorer.vue'

describe('WorkspaceExplorer', () => {
  it('renders nested folders and emits a safe relative path for preview', async () => {
    const wrapper = mount(WorkspaceExplorer, {
      props: {
        workspace: {
          rootPath: 'D:/Novel',
          files: [{
            path: 'chapters/volume-1/chapter-01.md',
            name: 'chapter-01.md',
            extension: 'md',
            size: 2048,
            lastModifiedAt: '2026-07-15T00:00:00Z',
          }],
        },
        selectedPath: null,
        previewContent: '',
        previewLoading: false,
      },
    })

    expect(wrapper.text()).toContain('chapters')
    expect(wrapper.text()).toContain('volume-1')
    await wrapper.get('.workspace-file').trigger('click')
    expect(wrapper.emitted('select')?.[0]).toEqual(['chapters/volume-1/chapter-01.md'])
  })
})
