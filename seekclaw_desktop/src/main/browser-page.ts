/** Fixed code in an isolated JS world; no arbitrary agent JavaScript or app bridge is exposed. */
export function browserPageScript(command: Record<string, unknown>, snapshotId: string): string {
  return `(() => { try { return (${pageCommand.toString()})(${JSON.stringify(command)}, ${JSON.stringify(snapshotId)}) } catch (error) { return { browser_error: error instanceof Error ? error.message : String(error) } } })()`
}

function pageCommand(command: Record<string, unknown>, snapshotId: string) {
  const root = globalThis as typeof globalThis & { seekclawPage?: { snapshot: string; elements: Map<string, Element> } }
  const previous = root.seekclawPage
  if (command.element_id) {
    if (!previous || previous.snapshot !== command.snapshot_id) throw new Error('页面快照已过期，请重新读取 snapshot')
    const element = previous.elements.get(String(command.element_id))
    if (!element?.isConnected) throw new Error('页面元素已变化，请重新读取 snapshot')
    if (command.action === 'click') {
      element.scrollIntoView({ block: 'center', inline: 'center' })
      if (!(element instanceof HTMLElement)) throw new Error('该元素不能点击')
      element.click()
    } else if (command.action === 'fill') {
      if (!(element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement)) throw new Error('请选择输入框')
      if (element instanceof HTMLInputElement && element.type === 'file') throw new Error('不支持自动文件选择')
      element.focus({ preventScroll: true })
      const prototype = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
      Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(element, String(command.text ?? ''))
      element.dispatchEvent(new Event('input', { bubbles: true }))
      element.dispatchEvent(new Event('change', { bubbles: true }))
    }
  }
  if (command.actionOnly) return { action: command.action }
  const elements = new Map<string, Element>()
  const lines: string[] = []
  let count = 0
  const visible = (element: Element) => {
    const style = getComputedStyle(element)
    return style.display !== 'none' && style.visibility !== 'hidden' && element.getClientRects().length > 0
  }
  const label = (element: Element) => (element.getAttribute('aria-label')
    || (element as HTMLInputElement).labels?.[0]?.textContent || element.getAttribute('placeholder')
    || element.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 180)
  const candidates = document.querySelectorAll('h1,h2,h3,button,a[href],input,textarea,select,[role="button"],[role="link"],[role="checkbox"],[contenteditable="true"],iframe')
  for (const element of candidates) {
    if (!visible(element) || count >= 250) continue
    const id = String(++count)
    elements.set(id, element)
    const tag = element.tagName.toLowerCase()
    const type = element.getAttribute('type') ?? ''
    const password = type === 'password'
    const value = !password && (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement)
      ? ` value=${JSON.stringify(element.value.slice(0, 160))}` : ''
    lines.push('[' + id + '] ' + (element.getAttribute('role') || tag) + (type ? ':' + type : '') + ' ' + label(element) + value
      + (element.hasAttribute('disabled') ? ' [disabled]' : '')
      + (tag === 'iframe' ? ' [use screenshot coordinates for frame contents]' : ''))
  }
  root.seekclawPage = { snapshot: snapshotId, elements }
  return { snapshot_id: snapshotId, title: document.title, url: location.href,
    viewport: { width: innerWidth, height: innerHeight },
    text: (document.body?.innerText ?? '').slice(0, 16000), elements: lines }
}
