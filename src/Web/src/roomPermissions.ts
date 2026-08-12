import type { Room, RoomMember } from './types'

export function canManageRoom(room: Room) {
  return room.role === 'Admin'
}

export function canSendMessages(room: Room) {
  return room.role !== 'Muted'
}

export function canModerateMember(room: Room, actorId: string, target: RoomMember) {
  if (!canManageRoom(room) || target.userId === room.ownerId || target.userId === actorId) return false
  return actorId === room.ownerId || target.role !== 'Admin'
}

export function canChangeAdminRole(room: Room, actorId: string, target: RoomMember) {
  return actorId === room.ownerId
    && target.userId !== room.ownerId
    && target.userId !== actorId
}

export function canTransferOwnership(room: Room, actorId: string, target: RoomMember) {
  return actorId === room.ownerId && target.userId !== actorId
}
