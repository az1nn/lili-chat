const endpoint = process.env.MAILPIT_API_URL ?? 'http://localhost:8025/api/v1/messages'
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))

for (let attempt = 1; attempt <= 30; attempt++) {
  try {
    const response = await fetch(endpoint)
    if (!response.ok) throw new Error(`Mailpit returned HTTP ${response.status}`)
    const mailbox = await response.json()
    if (mailbox.total > 0) {
      const serialized = JSON.stringify(mailbox)
      if (serialized.includes('Mensagem persistida')) {
        throw new Error('Notification leaked message content while content previews are disabled')
      }
      console.log(`Verified ${mailbox.total} captured SMTP notification(s) without message content`)
      process.exit(0)
    }
  } catch (error) {
    if (attempt === 30) throw error
  }
  await delay(1000)
}

throw new Error('No SMTP notification was captured within 30 seconds')
