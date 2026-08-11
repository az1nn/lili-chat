import { expect, test, type BrowserContext, type Page } from '@playwright/test'

async function register(context: BrowserContext, username: string, email: string) {
  const page = await context.newPage()
  await page.goto('/')
  await page.getByRole('button', { name: 'Criar uma conta' }).click()
  await page.getByPlaceholder('Username').fill(username)
  await page.getByPlaceholder('Email').fill(email)
  await page.getByPlaceholder('Senha').fill('StrongPassword!123')
  await page.getByRole('button', { name: 'Criar conta', exact: true }).click()
  await expect(page.locator('.profile')).toContainText(username)
  await expect.poll(async () => page.locator('.profile code').textContent(), { timeout: 30_000 })
    .toMatch(/^[A-HJ-NP-Z2-9]{8}$/)
  return page
}

async function openRoom(page: Page, roomName: string) {
  await page.getByRole('button', { name: new RegExp(roomName) }).click()
  await expect(page.locator('.chat-header h2')).toHaveText(roomName)
}

test('two users exchange, persist, synchronize roles, and revoke room access', async ({ browser }) => {
  const suffix = `${Date.now()}-${Math.random().toString(16).slice(2)}`
  const roomName = `Sala E2E ${suffix}`
  const archivedRoomName = `Sala Arquivada ${suffix}`
  const deletionRoomName = `Sala Exclusão ${suffix}`
  const message = `Mensagem persistida ${suffix}`
  const deletedMessage = `Mensagem a apagar ${suffix}`
  const password = 'StrongPassword!123'
  const emailB = `bob-${suffix}@example.test`
  const contextA = await browser.newContext()
  const contextB = await browser.newContext()

  try {
    const pageA = await register(contextA, `alice-${suffix}`, `alice-${suffix}@example.test`)
    const pageB = await register(contextB, `bob-${suffix}`, emailB)
    const publicIdB = (await pageB.locator('.profile code').textContent())?.trim()
    expect(publicIdB).toBeTruthy()

    await pageA.getByPlaceholder('Nova sala').fill(roomName)
    await pageA.locator('.new-room button').click()
    await expect(pageA.locator('.chat-header h2')).toHaveText(roomName)
    await pageA.getByPlaceholder('PublicId do familiar').fill(publicIdB!)
    await pageA.locator('.invite button').click()
    await expect(pageA.locator('.member-strip')).toContainText(`bob-${suffix}`)

    await pageB.reload()
    await openRoom(pageB, roomName)
    await expect(pageB.locator('.chat-header')).toContainText('connected')

    await pageA.getByPlaceholder('Digite uma mensagem...').fill(message)
    await pageA.locator('.composer button').click()
    await expect(pageB.locator('.messages')).toContainText(message)
    await expect(pageA.locator('.message.mine').filter({ hasText: message }))
      .toContainText('persisted', { timeout: 30_000 })

    await expect.poll(async () => {
      await pageB.reload()
      const roomButton = pageB.getByRole('button', { name: new RegExp(roomName) })
      if (await roomButton.count() === 0) return false
      await roomButton.click()
      return (await pageB.locator('.messages').textContent())?.includes(message) ?? false
    }, { timeout: 30_000 }).toBe(true)

    await pageA.getByRole('button', { name: 'silenciar', exact: true }).click()
    await expect(pageB.getByPlaceholder('Você está silenciado nesta sala'))
      .toBeDisabled({ timeout: 30_000 })
    await expect(pageB.getByRole('button', { name: 'Enviar' })).toBeDisabled()

    await pageA.getByRole('button', { name: 'desmutar', exact: true }).click()
    await expect(pageB.getByPlaceholder('Digite uma mensagem...'))
      .toBeEnabled({ timeout: 30_000 })

    pageA.once('dialog', dialog => dialog.accept())
    await pageA.getByRole('button', { name: 'remover', exact: true }).click()
    await expect(pageA.locator('.member-strip')).not.toContainText(`bob-${suffix}`)

    await expect(pageB.getByRole('button', { name: new RegExp(roomName) }))
      .toHaveCount(0, { timeout: 30_000 })
    await expect(pageB.locator('.welcome')).toContainText('Selecione ou crie uma sala')

    await pageB.reload()
    await expect(pageB.getByRole('button', { name: new RegExp(roomName) })).toHaveCount(0)

    await pageA.getByPlaceholder('Nova sala').fill(archivedRoomName)
    await pageA.locator('.new-room button').click()
    await pageA.getByPlaceholder('PublicId do familiar').fill(publicIdB!)
    await pageA.locator('.invite button').click()
    await expect(pageA.locator('.member-strip')).toContainText(`bob-${suffix}`)

    await pageB.reload()
    await openRoom(pageB, archivedRoomName)
    await expect(pageB.locator('.chat-header')).toContainText('connected')

    pageA.once('dialog', dialog => dialog.accept())
    await pageA.getByRole('button', { name: 'Arquivar', exact: true }).click()
    await expect(pageA.getByRole('button', { name: new RegExp(archivedRoomName) }))
      .toHaveCount(0)
    await expect(pageB.getByRole('button', { name: new RegExp(archivedRoomName) }))
      .toHaveCount(0, { timeout: 30_000 })
    await expect(pageB.locator('.welcome')).toContainText('Selecione ou crie uma sala')

    await pageA.getByPlaceholder('Nova sala').fill(deletionRoomName)
    await pageA.locator('.new-room button').click()
    await pageA.getByPlaceholder('PublicId do familiar').fill(publicIdB!)
    await pageA.locator('.invite button').click()
    await expect(pageA.locator('.member-strip')).toContainText(`bob-${suffix}`)

    await pageB.reload()
    await openRoom(pageB, deletionRoomName)
    await pageB.getByPlaceholder('Digite uma mensagem...').fill(deletedMessage)
    await pageB.locator('.composer button').click()
    await expect(pageB.locator('.message.mine').filter({ hasText: deletedMessage }))
      .toContainText('persisted', { timeout: 30_000 })

    await pageB.getByText('Excluir conta', { exact: true }).click()
    await pageB.getByPlaceholder('Senha atual').fill(password)
    pageB.once('dialog', dialog => dialog.accept())
    await pageB.getByRole('button', { name: 'Excluir', exact: true }).click()
    await expect(pageB.getByRole('button', { name: 'Entrar', exact: true }))
      .toBeVisible({ timeout: 30_000 })

    await expect(pageA.locator('.member-strip')).not.toContainText(`bob-${suffix}`, {
      timeout: 30_000,
    })
    await expect.poll(async () => {
      await pageA.reload()
      await openRoom(pageA, deletionRoomName)
      return (await pageA.locator('.messages').textContent())?.includes(deletedMessage) ?? false
    }, { timeout: 30_000 }).toBe(false)

    await pageB.getByPlaceholder('Email').fill(emailB)
    await pageB.getByPlaceholder('Senha').fill(password)
    await pageB.getByRole('button', { name: 'Entrar', exact: true }).click()
    await expect(pageB.locator('.error')).toBeVisible()
  } finally {
    await contextA.close()
    await contextB.close()
  }
})
