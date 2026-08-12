import { expect, test, type BrowserContext } from '@playwright/test'

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

test('family Head can add a member, transfer leadership, and lifecycle continues safely', async ({ browser }) => {
  const suffix = `${Date.now()}-${Math.random().toString(16).slice(2)}`
  const familyName = `Família E2E ${suffix}`
  const contextHead = await browser.newContext()
  const contextMember = await browser.newContext()

  try {
    const head = await register(
      contextHead,
      `head-${suffix}`,
      `head-${suffix}@example.test`,
    )
    const member = await register(
      contextMember,
      `member-${suffix}`,
      `member-${suffix}@example.test`,
    )
    const memberPublicId = (await member.locator('.profile code').textContent())?.trim()
    expect(memberPublicId).toBeTruthy()

    await head.getByPlaceholder('Nova família').fill(familyName)
    await head.getByPlaceholder('Nova família').press('Enter')
    await expect(head.locator('.family-header h2')).toHaveText(familyName)
    await expect(head.locator('.family-header')).toContainText('Head')

    await head.getByPlaceholder('PublicId do familiar').fill(memberPublicId!)
    await head.getByRole('button', { name: 'Adicionar familiar' }).click()
    await expect(head.locator('.family-members')).toContainText(`member-${suffix}`)

    await member.reload()
    await member.getByRole('button', { name: new RegExp(familyName) }).click()
    await expect(member.locator('.family-header h2')).toHaveText(familyName)
    await expect(member.locator('.family-header')).toContainText('Member')
    await expect(member.getByRole('button', { name: 'Sair da família' })).toBeVisible()
    await expect(member.getByRole('button', { name: 'Editar' })).toHaveCount(0)

    head.once('dialog', dialog => dialog.accept())
    await head.getByRole('button', { name: 'Tornar Head' }).click()
    await expect(head.locator('.family-header')).toContainText('Member')
    await expect(head.getByRole('button', { name: 'Sair da família' })).toBeVisible()

    await member.reload()
    await member.getByRole('button', { name: new RegExp(familyName) }).click()
    await expect(member.locator('.family-header')).toContainText('Head')
    await expect(member.getByRole('button', { name: 'Editar' })).toBeVisible()
    await expect(member.getByRole('button', { name: 'Excluir família' })).toBeVisible()

    head.once('dialog', dialog => dialog.accept())
    await head.getByRole('button', { name: 'Sair da família' }).click()
    await expect(head.getByRole('button', { name: new RegExp(familyName) })).toHaveCount(0)
    await expect(head.locator('.welcome')).toContainText('Selecione uma família ou sala')

    await member.reload()
    await member.getByRole('button', { name: new RegExp(familyName) }).click()
    member.once('dialog', dialog => dialog.accept())
    await member.getByRole('button', { name: 'Excluir família' }).click()
    await expect(member.getByRole('button', { name: new RegExp(familyName) })).toHaveCount(0)
    await expect(member.locator('.welcome')).toContainText('Selecione uma família ou sala')
  } finally {
    await contextHead.close()
    await contextMember.close()
  }
})