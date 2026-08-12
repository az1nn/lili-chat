export type AuthUser = { id: string; username: string; email: string }
export type UserProfile = AuthUser & { publicId: string }
export type RoomRole = 'Admin' | 'Member' | 'Muted'
export type FamilyRole = 'Head' | 'Member'

export type AuthResponse = {
  accessToken: string
  accessExpiresAt: string
  user: AuthUser
}

export type Family = {
  id: string
  name: string
  description?: string
  role: FamilyRole
  membersCount: number
  createdAt: string
}

export type FamilyMember = {
  userId: string
  publicId: string
  username: string
  role: FamilyRole
  joinedAt: string
}

export type FamilyDetails = Family & {
  members: FamilyMember[]
}

export type Room = {
  id: string
  name: string
  description?: string
  ownerId: string
  membersCount: number
  role: RoomRole
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
  role: RoomRole
  joinedAt: string
}
