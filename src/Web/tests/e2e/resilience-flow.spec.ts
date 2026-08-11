import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { expect, test, type Page } from '@playwright/test'

const exec = promisify(execFile)
const repositoryRoot = new URL('../../../..', import.meta.url).pathname

async function compose(...args: string[]) {
  await exec('docker', ['compose', ...args], { cwd: repositoryRoot, timeout: 60_000 })
}

async function waitForService(service: string, healthCommand: string[]) {
  await expect.poll(async () => {
    try {
      await compose('exec', '-T', service, ...healthCommand)
      return true
    } catch {
      return false
    }
  }, { timeout: 60_000, intervals: [1_000, 2_000, 3_000] }).toBe(true)
}

async function register(page: Page, suffix: string) {
  await page.goto('/')
  await page.getByRole('button', { name: 'Criar uma conta' }).click()
  await page.getByPlaceholder('Username').fill(`resilience-${suffix}`)
  await page.getByPlaceholder('Email').fill(`resilience-${suffix}@example.test`)
  await page.getByPlaceholder('Senha').fill('StrongPassword!123')
  await page.getByRole('button', { name: 'Criar conta', exact: true }).click()
  await expect(page.locator('.profile code')).toContainText(/^[A-HJ-NP-Z2-9]{8}$/, {
    timeout: 30_000,
  })
}

async function sendAndExpectFailed(page: Page, content: string) {
  await page.getByPlaceholder('Digite uma mensagem...').fill(content)
  await page.getByRole('button', { name: 'Enviar', exact: true }).click()
  const message = page.locator('.message.mine').filter({ hasText: content })
  await expect(message).toContainText('failed', { timeout: 15_000 })
  return message
}

async function retryUntilPersisted(message: ReturnType<Page['locator']>) {
  await expect.poll(async () => {
    const retry = message.getByRole('button', { name: 'tentar novamente' })
    if (await retry.isVisible()) await retry.click()
    return (await message.textContent())?.includes('persisted') ?? false
  }, { timeout: 60_000, intervals: [2_000, 3_000, 5_000] }).toBe(true)
}

test('dependencies fail closed and recover without losing manual retries', async ({ page }) => {
  test.setTimeout(240_000)
  const suffix = `${Date.now()}-${Math.random().toString(16).slice(2)}`
  const roomName = `Resilience ${suffix}`

  await register(page, suffix)
  await page.getByPlaceholder('Nova sala').fill(roomName)
  await page.locator('.new-room button').click()
  await expect(page.locator('.chat-header')).toContainText('connected', { timeout: 30_000 })

  await compose('stop', 'rabbitmq')
  const rabbitMessage = await sendAndExpectFailed(page, `Rabbit retry ${suffix}`)
  await compose('start', 'rabbitmq')
  await waitForService('rabbitmq', ['rabbitmq-diagnostics', '-q', 'ping'])
  await retryUntilPersisted(rabbitMessage)

  await compose('restart', 'redis', 'realtime-hub')
  await waitForService('redis', ['redis-cli', 'ping'])
  await waitForService('realtime-hub', ['curl', '--fail', '--silent', 'http://localhost:8080/health'])
  await expect(page.locator('.chat-header')).toContainText('connected', { timeout: 60_000 })
  await page.getByPlaceholder('Digite uma mensagem...').fill(`Reconnect ${suffix}`)
  await page.getByRole('button', { name: 'Enviar', exact: true }).click()
  await expect(page.locator('.message.mine').filter({ hasText: `Reconnect ${suffix}` }))
    .toContainText('persisted', { timeout: 30_000 })

  await compose('stop', 'room-svc')
  const roomMessage = await sendAndExpectFailed(page, `Room retry ${suffix}`)
  await compose('start', 'room-svc')
  await waitForService('room-svc', ['curl', '--fail', '--silent', 'http://localhost:8080/health'])
  await retryUntilPersisted(roomMessage)
})
