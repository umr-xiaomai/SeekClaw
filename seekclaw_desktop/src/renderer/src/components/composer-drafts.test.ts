import { describe, expect, it } from 'vitest'
import {
  clearTaskDraft,
  getTaskDraft,
  saveTaskDraft,
  taskDrafts
} from '../composer-drafts'

describe('Composer per-task drafts', () => {
  it('manages task drafts correctly across session transitions', () => {
    taskDrafts.clear()

    const taskA = 'session-task-a'
    const taskB = 'session-task-b'

    // 1. Simulate user typing in session A
    saveTaskDraft(
      taskA,
      'Draft for Task A',
      [{ id: 'img1', name: 'test.png', mediaType: 'image/png', data: 'abc', sizeBytes: 100 }],
      [{ id: 'f1', name: 'code.ts', path: '/code.ts', sizeBytes: 200, extension: 'ts' }]
    )

    expect(taskDrafts.has(taskA)).toBe(true)
    const draftA = getTaskDraft(taskA)
    expect(draftA?.value).toBe('Draft for Task A')
    expect(draftA?.images).toHaveLength(1)
    expect(draftA?.files).toHaveLength(1)

    // 2. Session B starts empty
    expect(getTaskDraft(taskB)).toBeUndefined()

    // 3. User types in Session B
    saveTaskDraft(taskB, 'Draft for Task B', [], [])
    expect(taskDrafts.has(taskB)).toBe(true)

    // 4. Switching back to Session A: Task A draft is intact
    expect(getTaskDraft(taskA)?.value).toBe('Draft for Task A')

    // 5. Sending message in Task A clears Task A draft
    clearTaskDraft(taskA)
    expect(getTaskDraft(taskA)).toBeUndefined()

    // 6. Task B draft remains intact
    expect(getTaskDraft(taskB)?.value).toBe('Draft for Task B')

    // 7. Saving empty draft deletes it
    saveTaskDraft(taskB, '   ', [], [])
    expect(getTaskDraft(taskB)).toBeUndefined()
  })
})
