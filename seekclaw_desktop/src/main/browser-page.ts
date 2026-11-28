/** Fixed code in an isolated JS world; no arbitrary app bridge is exposed. */
export function browserPageScript(command: Record<string, unknown>, snapshotId: string): string {
  return `(() => { try { return (${pageCommand.toString()})(${JSON.stringify(command)}, ${JSON.stringify(snapshotId)}) } catch (error) { return { browser_error: error instanceof Error ? error.message : String(error) } } })()`
}

function pageCommand(command: Record<string, unknown>, snapshotId: string) {
  const root = globalThis as typeof globalThis & { seekclawPage?: { snapshot: string; elements: Map<string, Element> } }
  const previous = root.seekclawPage

  const selectOption = (element: HTMLSelectElement, rawTarget: string) => {
    const target = rawTarget.trim()
    const options = Array.from(element.options)
    let matched = options.find((opt) => opt.value === rawTarget || opt.value === target)
      ?? options.find((opt) => (opt.textContent ?? '').trim() === target)
      ?? options.find((opt) => opt.value.toLowerCase() === target.toLowerCase() || (opt.textContent ?? '').trim().toLowerCase() === target.toLowerCase())
    if (!matched) {
      throw new Error(`下拉框中找不到匹配选项: ${rawTarget}`)
    }
    if (matched.disabled) {
      throw new Error(`目标选项已禁用: ${rawTarget}`)
    }
    element.focus({ preventScroll: true })
    element.value = matched.value
    matched.selected = true
    element.dispatchEvent(new Event('input', { bubbles: true }))
    element.dispatchEvent(new Event('change', { bubbles: true }))
  }

  const resolveElement = (elementId: unknown): Element => {
    if (!previous || previous.snapshot !== command.snapshot_id) {
      throw new Error('页面快照已过期，请重新读取 snapshot')
    }
    const element = previous.elements.get(String(elementId))
    if (!element?.isConnected) {
      throw new Error('页面元素已变化，请重新读取 snapshot')
    }
    return element
  }

  if (command.action === 'locate' && command.element_id) {
    const element = resolveElement(command.element_id)
    element.scrollIntoView({ block: 'center', inline: 'center' })
    const rect = element.getBoundingClientRect()
    const tag = element.tagName.toLowerCase()
    const isSelect = element instanceof HTMLSelectElement
    const isTextInput =
      element instanceof HTMLInputElement
      || element instanceof HTMLTextAreaElement
      || (element instanceof HTMLElement && element.isContentEditable)
    const draggable = element instanceof HTMLElement && element.draggable === true
    return {
      x: Math.round(rect.left + rect.width / 2),
      y: Math.round(rect.top + rect.height / 2),
      width: Math.round(rect.width),
      height: Math.round(rect.height),
      tag,
      isTextInput,
      isSelect,
      draggable
    }
  }

  if (command.action === 'drag_html5' && command.element_id && command.target_element_id) {
    const source = resolveElement(command.element_id)
    const target = resolveElement(command.target_element_id)
    const dataTransfer = new DataTransfer()
    source.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }))
    target.dispatchEvent(new DragEvent('dragenter', { bubbles: true, cancelable: true, dataTransfer }))
    target.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }))
    target.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }))
    source.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer }))
    return { action: 'drag_html5', ok: true }
  }

  if (command.element_id) {
    const element = resolveElement(command.element_id)
    if (command.action === 'click') {
      element.scrollIntoView({ block: 'center', inline: 'center' })
      if (!(element instanceof HTMLElement)) throw new Error('该元素不能点击')
      element.click()
    } else if (command.action === 'select_option') {
      if (!(element instanceof HTMLSelectElement)) throw new Error('请选择 <select> 下拉框元素')
      element.scrollIntoView({ block: 'center', inline: 'center' })
      selectOption(element, String(command.value ?? command.text ?? ''))
    } else if (command.action === 'fill') {
      if (element instanceof HTMLSelectElement) {
        element.scrollIntoView({ block: 'center', inline: 'center' })
        selectOption(element, String(command.text ?? command.value ?? ''))
      } else if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) {
        if (element instanceof HTMLInputElement && element.type === 'file') throw new Error('不支持自动文件选择')
        element.focus({ preventScroll: true })
        const prototype = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
        Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(element, String(command.text ?? ''))
        element.dispatchEvent(new Event('input', { bubbles: true }))
        element.dispatchEvent(new Event('change', { bubbles: true }))
      } else if (element instanceof HTMLElement && element.isContentEditable) {
        element.focus({ preventScroll: true })
        element.textContent = String(command.text ?? '')
        element.dispatchEvent(new Event('input', { bubbles: true }))
      } else {
        throw new Error('请选择输入框或下拉框')
      }
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
    let selectOptions = ''
    if (element instanceof HTMLSelectElement) {
      const opts = Array.from(element.options).slice(0, 15).map((opt) => {
        const optText = (opt.textContent ?? '').trim().slice(0, 40)
        return optText && optText !== opt.value ? `${opt.value}:${optText}` : opt.value
      })
      if (opts.length) selectOptions = ` options=${JSON.stringify(opts)}`
    }
    lines.push('[' + id + '] ' + (element.getAttribute('role') || tag) + (type ? ':' + type : '') + ' ' + label(element) + value + selectOptions
      + (element.hasAttribute('disabled') ? ' [disabled]' : '')
      + (tag === 'iframe' ? ' [use screenshot coordinates for frame contents]' : ''))
  }
  root.seekclawPage = { snapshot: snapshotId, elements }
  return { snapshot_id: snapshotId, title: document.title, url: location.href,
    viewport: { width: innerWidth, height: innerHeight },
    text: (document.body?.innerText ?? '').slice(0, 16000), elements: lines }
}
