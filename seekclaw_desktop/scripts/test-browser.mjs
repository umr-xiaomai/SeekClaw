// Run against real Electron/Chromium without a daemon or the user's browser profile.
import { mkdtemp, readFile, writeFile, rm, realpath } from 'node:fs/promises'
import { join, dirname } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import { spawn } from 'node:child_process'
import electron from 'electron'
import ts from 'typescript'

const root = dirname(dirname(fileURLToPath(import.meta.url)))
const temporaryParent = await realpath(tmpdir())
const temporary = await mkdtemp(join(temporaryParent, 'seekclaw-browser-native-'))
if (dirname(await realpath(temporary)) !== temporaryParent) throw new Error('Unexpected temporary browser directory')
try {
  await writeFile(join(temporary, 'package.json'), '{"type":"module"}')
  for (const name of ['browser-policy', 'browser-page', 'conversation-browser']) {
    const source = await readFile(join(root, 'src', 'main', `${name}.ts`), 'utf8')
    const { outputText } = ts.transpileModule(source, { compilerOptions: {
      target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext,
    } })
    await writeFile(join(temporary, `${name}.js`), outputText)
  }
  await writeFile(join(temporary, 'runner.mjs'), String.raw`
import { app, BrowserWindow, session } from 'electron'
import { createServer } from 'node:http'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'
import { ConversationBrowser } from './conversation-browser.js'

app.setPath('userData', fileURLToPath(new URL('./profile', import.meta.url)))
const timeout = setTimeout(() => { console.error('Browser integration test timed out'); app.exit(1) }, 45000)
let browser, window, foreground
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))
async function waitFor(check, message) {
  const deadline = Date.now() + 3000
  while (!check()) {
    assert.ok(Date.now() < deadline, message)
    await delay(20)
  }
}
const server = createServer((_req, res) => {
  res.setHeader('Content-Type', 'text/html; charset=utf-8')
  res.end('<!doctype html><title>Browser integration</title><h1>Isolated page</h1>' +
    '<form onsubmit="event.preventDefault();document.getElementById(\'result\').textContent=document.getElementById(\'name\').value">' +
    '<input id="name" aria-label="Name"><button type="submit">Apply</button></form><p id="result"></p><input type="file" aria-label="Upload">')
})
async function run() {
try {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  await app.whenReady()
  console.log('Electron ready')
  window = new BrowserWindow({ show: false, x: 460, y: 80, width: 1100, height: 800, webPreferences: { sandbox: true } })
  await window.loadURL('data:text/html,<title>Browser host</title><textarea id="composer">Conversation input</textarea>')
  // A real foreground input window makes the no-activation contract observable.
  // With no foreground peer, Windows can activate the first shown app window as
  // part of startup, independently of the browser command under test.
  foreground = new BrowserWindow({ show: false, x: 40, y: 80, width: 360, height: 200, webPreferences: { sandbox: true } })
  await foreground.loadURL('data:text/html,<title>Browser focus guard</title><textarea autofocus>Typing continues here</textarea>')
  foreground.show()
  foreground.focus()
  await waitFor(() => foreground.isFocused(), 'Focus reference window must be active before testing')
  let watchActivation = true, unexpectedActivations = 0
  window.on('focus', () => { if (watchActivation) unexpectedActivations++ })
  const assertBackground = () => {
    assert.equal(window.isFocused(), false, 'Browser must not take focus')
    assert.equal(foreground.isFocused(), true, 'Foreground input window must keep focus')
    assert.equal(unexpectedActivations, 0, 'Browser must never activate the host, even temporarily')
  }
  app.on('web-contents-created', (_event, contents) => {
    contents.on('did-finish-load', () => console.log('Page loaded'))
    contents.on('render-process-gone', (_event, detail) => console.error(detail))
  })
  browser = new ConversationBrowser(window, () => {})
  const scope = 'a'.repeat(64), other = 'b'.repeat(64)
  const url = 'http://127.0.0.1:' + server.address().port
  let snapshot = await browser.execute(scope, 'session-a', { action: 'open', url })
  console.log('Opened isolated page')
  assert.equal(snapshot.title, 'Browser integration')
  assert.match(snapshot.text, /Isolated page/)
  const elementId = (snapshot, label) => {
    const line = snapshot.elements.find(line => line.includes(label))
    assert.ok(line, 'Missing element: ' + label)
    return line.match(/^\[(\d+)\]/)[1]
  }
  const oldSnapshot = snapshot
  snapshot = await browser.execute(scope, 'session-a', { action: 'fill', snapshot_id: snapshot.snapshot_id,
    element_id: elementId(snapshot, 'Name'), text: 'SeekClaw integration passed' })
  await assert.rejects(browser.execute(scope, 'session-a', { action: 'fill', snapshot_id: oldSnapshot.snapshot_id,
    element_id: elementId(oldSnapshot, 'Name'), text: 'stale' }), /快照已过期/)
  snapshot = await browser.execute(scope, 'session-a', { action: 'click', snapshot_id: snapshot.snapshot_id,
    element_id: elementId(snapshot, 'Apply') })
  assert.match(snapshot.text, /SeekClaw integration passed/)
  console.log('Completed element actions')
  await assert.rejects(browser.execute(scope, 'session-a', { action: 'fill', snapshot_id: snapshot.snapshot_id,
    element_id: elementId(snapshot, 'Upload'), text: 'private.txt' }), /不支持自动文件选择/)
  await browser.execute(scope, 'session-a', { action: 'press', key: 'Tab' })
  assert.equal(window.isVisible(), false, 'Agent must not show the application window')
  assertBackground()
  // Native Chromium needs a composited viewport for image capture. Showing this
  // disposable test window is a host action; browser commands never activate it.
  window.showInactive()
  browser.setLayout({ scope, visible: true, bounds: { x: 0, y: 48, width: 1000, height: 700 } })
  const page = window.contentView.children[0].webContents
  assertBackground()
  const screenshot = await browser.execute(scope, 'session-a', { action: 'screenshot' })
  console.log('Captured native viewport')
  assert.ok(Buffer.from(screenshot.image.data, 'base64').length > 500)
  assertBackground()
  snapshot = await browser.execute(scope, 'session-a', { action: 'snapshot' }, 'visible-snapshot')
  snapshot = await browser.execute(scope, 'session-a', { action: 'fill', snapshot_id: snapshot.snapshot_id,
    element_id: elementId(snapshot, 'Name'), text: 'Background input passed' }, 'visible-fill')
  await browser.execute(scope, 'session-a', { action: 'press', key: 'Enter' }, 'visible-keyboard')
  assertBackground()
  // When the user is composing in the same app, browser-local DOM focus and
  // protocol input must also leave the conversation's native input focus intact.
  watchActivation = false
  window.show()
  window.focus()
  window.webContents.focus()
  await waitFor(() => window.isFocused() && window.webContents.isFocused(), 'Conversation input must be active before testing')
  await window.webContents.executeJavaScript('document.getElementById("composer").focus()')
  snapshot = await browser.execute(scope, 'session-a', { action: 'snapshot' }, 'composer-snapshot')
  snapshot = await browser.execute(scope, 'session-a', { action: 'fill', snapshot_id: snapshot.snapshot_id,
    element_id: elementId(snapshot, 'Name'), text: 'Composer input passed' }, 'composer-fill')
  await browser.execute(scope, 'session-a', { action: 'press', key: 'Enter' }, 'composer-keyboard')
  await browser.execute(scope, 'session-a', { action: 'screenshot' }, 'composer-screenshot')
  assert.equal(window.webContents.isFocused(), true, 'Browser must not steal conversation input focus')
  assert.equal(await window.webContents.executeJavaScript('document.activeElement?.id'), 'composer')
  window.hide()
  await session.fromPartition('persist:seekclaw-browser-' + scope).cookies.set({ url, name: 'owner', value: 'a' })
  await browser.execute(other, 'session-b', { action: 'open', url })
  assert.equal((await session.fromPartition('persist:seekclaw-browser-' + other).cookies.get({ url, name: 'owner' })).length, 0)
  browser.pause(scope, true)
  await assert.rejects(browser.execute(scope, 'session-a', { action: 'snapshot' }, 'agent-request'), /用户已暂停/)
  await browser.execute(other, 'session-b', { action: 'snapshot' }, 'other-agent-request')
  await assert.rejects(browser.execute(other, 'session-b', { action: 'open', url: 'file:///private.txt' }), /HTTP/)
  await browser.execute(scope, 'session-a', { action: 'close' })
  assert.equal(browser.state(scope).tabs.length, 0)
  console.log('PASS: real Chromium snapshot, fill, click, keyboard, screenshot, stale snapshots, file guard, isolated cookies, pause, no window activation, cleanup')
} catch (error) {
  console.error(error)
  process.exitCode = 1
} finally {
  clearTimeout(timeout)
  browser?.dispose()
  window?.destroy()
  foreground?.destroy()
  server.closeAllConnections()
  server.close()
  app.exit(process.exitCode || 0)
}
}
void run()
`)
  const env = { ...process.env }
  delete env.ELECTRON_RUN_AS_NODE
  const child = spawn(electron, [join(temporary, 'runner.mjs')], { cwd: root, env, windowsHide: true, stdio: 'inherit' })
  const exitCode = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve) })
  if (exitCode !== 0) throw new Error(`Browser integration test exited with ${exitCode}`)
} finally {
  await rm(temporary, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 })
}
