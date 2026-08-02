import { describe, expect, it } from 'vitest'
import { isForbiddenProjectPath } from './project-paths'

const HOME = 'C:\\Users\\MECHREVO'

describe('isForbiddenProjectPath', () => {
  it('rejects the user profile directory itself', () => {
    expect(isForbiddenProjectPath(HOME, HOME)).toBe(true)
  })

  it('rejects the SeekClaw state directory itself', () => {
    expect(isForbiddenProjectPath(`${HOME}\\.seekclaw`, HOME)).toBe(true)
  })

  it('rejects anything inside the SeekClaw state directory', () => {
    expect(isForbiddenProjectPath(`${HOME}\\.seekclaw\\sessions`, HOME)).toBe(true)
    expect(isForbiddenProjectPath(`${HOME}\\.seekclaw\\skills\\my-skill`, HOME)).toBe(true)
  })

  it('accepts real project folders under the profile', () => {
    expect(isForbiddenProjectPath(`${HOME}\\Documents\\projA`, HOME)).toBe(false)
    expect(isForbiddenProjectPath(`${HOME}\\Desktop\\projB`, HOME)).toBe(false)
  })

  it('handles forward slashes, trailing separators and case differences', () => {
    expect(isForbiddenProjectPath('c:/users/mechrevo/', 'C:\\Users\\MECHREVO')).toBe(true)
    expect(isForbiddenProjectPath('C:\\Users\\MECHREVO\\.SEEKCLAW\\', 'c:\\users\\mechrevo')).toBe(true)
  })

  it('returns false when the home path is unknown', () => {
    expect(isForbiddenProjectPath(HOME, undefined)).toBe(false)
  })
})
