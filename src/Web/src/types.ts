export type AuthUser = { id: string; username: string; email: string }
export type UserProfile = AuthUser & { publicId: string }

export type AuthResponse = {
  accessToken: string
  accessExpiresAt: string
  user: AuthUser
}

export type Room = {
  id: string
  name: string
  description?: string
  ownerId: string
  membersCount: number
  role: 'Admin' | 'Member' | 'Muted'
  createdAt: string
}

export type Message = {
  id: string
  roomId: string
  senderId: string
  content: string
  sentAt: string
  clientMessageId?: string
  status?: 'sending' | 'accepted' | 'persisted' | 'failed'
}

export type RoomMember = {
  userId: string
  publicId: string
  username: string
  role: string
  joinedAt: string
}
