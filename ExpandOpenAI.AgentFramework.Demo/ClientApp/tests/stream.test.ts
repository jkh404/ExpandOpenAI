import { describe, expect, it } from 'vitest'
import { streamChat, subscribeRun } from '../src/lib/api'

describe('streamChat', () => {
  it('parses fragmented NDJSON events including heartbeat and tool activity', async () => {
    const encoder = new TextEncoder()
    const chunks = [
      '{"type":"status","content":"准备中"}\n{"type":"heart',
      'beat","content":"仍在运行"}\n{"type":"tool_call","content":"调用文件工具","toolCallId":"c1","toolName":"write_workspace_file","toolArguments":"{}"}\n',
      '{"type":"delta","content":"第一段"}\n',
    ]
    const response = new Response(new ReadableStream({
      start(controller) {
        chunks.forEach(chunk => controller.enqueue(encoder.encode(chunk)))
        controller.close()
      },
    }), { status: 200 })
    const originalFetch = globalThis.fetch
    globalThis.fetch = async () => response
    const events: string[] = []

    try {
      await streamChat({ message: 'test' }, new AbortController().signal, event => events.push(event.type))
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(events).toEqual(['status', 'heartbeat', 'tool_call', 'delta'])
  })

  it('accepts PascalCase events from an older server build', async () => {
    const response = new Response('{"Type":"status","Content":"准备中"}\n', { status: 200 })
    const originalFetch = globalThis.fetch
    globalThis.fetch = async () => response
    const events: string[] = []
    try {
      await streamChat({ message: 'test' }, new AbortController().signal, event => events.push(event.type))
    } finally {
      globalThis.fetch = originalFetch
    }
    expect(events).toEqual(['status'])
  })

  it('cancels the response body when event parsing fails', async () => {
    let cancelled = false
    const encoder = new TextEncoder()
    const response = new Response(new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode('{"unexpected":true}\n'))
      },
      cancel() {
        cancelled = true
      },
    }), { status: 200 })
    const originalFetch = globalThis.fetch
    globalThis.fetch = async () => response
    try {
      await expect(streamChat({ message: 'test' }, new AbortController().signal, () => {})).rejects.toThrow('无效')
    } finally {
      globalThis.fetch = originalFetch
    }
    expect(cancelled).toBe(true)
  })
})

describe('subscribeRun', () => {
  it('parses persisted run events and requests only events after the cursor', async () => {
    const response = new Response(
      '{"runId":"run-1","sequence":4,"occurredAt":"2026-07-15T00:00:00Z","type":"delta","content":"续写内容"}\n',
      { status: 200 },
    )
    const originalFetch = globalThis.fetch
    let requestedUrl = ''
    globalThis.fetch = async input => {
      requestedUrl = String(input)
      return response
    }
    const sequences: number[] = []
    try {
      await subscribeRun('run-1', 3, new AbortController().signal, event => sequences.push(event.sequence))
    } finally {
      globalThis.fetch = originalFetch
    }

    expect(requestedUrl).toContain('after=3')
    expect(sequences).toEqual([4])
  })
})
