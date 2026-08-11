import { describe, expect, it } from 'vitest'
import { canChangeAdminRole, canManageRoom, canModerateMember, canSendMessages } from './roomPermissions'
import type { Room, RoomMember, RoomRole } from './types'

const ownerId = 'owner'
const room = (role: RoomRole): Room => ({
  id: 'room',
  name: 'Família',
  ownerId,
  membersCount: 3,
  role,
  createdAt: '2026-08-11T00:00:00Z',
})
const member = (userId: string, role: RoomRole): RoomMember => ({
  userId,
  publicId: 'ABCDEFGH',
  username: userId,
  role,
  joinedAt: '2026-08-11T00:00:00Z',
})

describe('room UI permissions', () => {
  it.each([
    ['Admin', true, true],
    ['Member', false, true],
    ['Muted', false, false],
  ] as const)('maps %s capabilities', (role, manages, sends) => {
    expect(canManageRoom(room(role))).toBe(manages)
    expect(canSendMessages(room(role))).toBe(sends)
  })

  it('lets an admin moderate members but protects owner, self, and peer admins', () => {
    const adminRoom = room('Admin')
    expect(canModerateMember(adminRoom, 'actor', member('member', 'Member'))).toBe(true)
    expect(canModerateMember(adminRoom, 'actor', member(ownerId, 'Admin'))).toBe(false)
    expect(canModerateMember(adminRoom, 'actor', member('actor', 'Admin'))).toBe(false)
    expect(canModerateMember(adminRoom, 'actor', member('peer-admin', 'Admin'))).toBe(false)
  })

  it('reserves promotion and demotion of admins to the owner', () => {
    expect(canChangeAdminRole(room('Admin'), ownerId, member('member', 'Member'))).toBe(true)
    expect(canChangeAdminRole(room('Admin'), 'actor', member('member', 'Member'))).toBe(false)
  })
})
