# Family and Chat Room Governance Specification

Status: **V1 control-plane implementation complete; family-scoped rooms planned for V2**

## Goals

Family Chat has two related but separately owned bounded contexts:

- **FamilyGraph** owns user projections, `PublicId`, families and family membership.
- **Room** owns chat rooms, room membership, moderation, ownership and realtime authorization data.

This specification closes the lifecycle/governance gaps without allowing one service to query another service's database. Family and room state remain independently persisted and are coordinated only through authenticated REST/gRPC contracts and domain events.

## Security invariants

1. User identity is always derived from the JWT `sub`; request bodies never choose the acting user.
2. Only the family `Head` may mutate family metadata or membership.
3. A family always has exactly one `Head` while it exists.
4. The current `Head` cannot leave or be removed; leadership must be transferred first, or the family must be deleted.
5. Only the room owner may transfer room ownership or archive a room.
6. A room owner must be a room member and remains `Admin`.
7. Ownership may only be transferred to an existing room member; the new owner is promoted to `Admin` atomically.
8. The owner cannot leave until ownership is transferred or the room is archived.
9. Non-owner admins may moderate ordinary members but cannot remove/administer another admin in ways reserved for the owner.
10. Family and room member caps are server-side safety limits: 100 family members and 250 room members.

## Family lifecycle — implemented

### List families

`GET /api/v1/families`

Returns the caller's families with:

- `id`
- `name`
- `description`
- caller `role`
- `membersCount`
- `createdAt`

### Create family

`POST /api/v1/families`

Body:

```json
{
  "name": "Casa Sá",
  "description": "Família principal"
}
```

The creator becomes the only `Head`.

### Read family detail

`GET /api/v1/families/{familyId}`

Any family member may read the family. The response includes the family metadata, caller role and ordered member list.

### Update family profile

`PATCH /api/v1/families/{familyId}`

Head-only. Updates name and description using the same 1–100 / 0–1000 limits used at creation.

### List members

`GET /api/v1/families/{familyId}/members`

Any family member may list members. `Head` is returned first, followed by join time.

### Add member by PublicId

`POST /api/v1/families/{familyId}/members`

Head-only. The target must exist in the FamilyGraph projection, must not already belong to the family, and the family must remain within the 100-member cap.

### Remove member

`DELETE /api/v1/families/{familyId}/members/{userId}`

Head-only. The current `Head` cannot be removed through this endpoint.

### Transfer Head

`POST /api/v1/families/{familyId}/head`

Body:

```json
{ "userId": "<existing-family-member-guid>" }
```

Head-only. The transfer is atomic in the FamilyGraph database: the old Head becomes `Member` and the target becomes `Head` in the same transaction/save operation.

### Leave family

`POST /api/v1/families/{familyId}/leave`

Members may leave themselves. A Head receives `409 Conflict` and must transfer leadership or delete the family first.

### Delete family

`DELETE /api/v1/families/{familyId}`

Head-only. Deletes the FamilyGraph aggregate and memberships via the existing database cascade.

**Important V1 boundary:** rooms are not yet attached to a `FamilyId`, so deleting a family does not archive unrelated Room aggregates. V2 below closes that boundary explicitly instead of introducing cross-database writes.

## Room lifecycle — implemented

The existing Room service already supports create/list/read/update, room audit, member list, add by `PublicId`, Admin/Member/Muted roles, leave, removal, archive, realtime authorization and member-removal propagation.

### Transfer room ownership

`POST /api/v1/rooms/{roomId}/owner`

Body:

```json
{ "userId": "<existing-room-member-guid>" }
```

Rules:

- only the current owner may call it;
- target must already be a room member;
- transferring to the current owner returns conflict;
- target is promoted to `Admin`;
- previous owner remains `Admin`;
- `OwnerId` changes atomically;
- an audit row `room.owner_transferred` records actor and target.

### Room member capacity

Adding a member is rejected when a room already contains 250 members. This is a hard safety cap, not a billing/plan limit.

## V2 — family-scoped rooms

The next implementation slice should connect the two bounded contexts without database coupling.

### Data model

Add `FamilyId` to the Room aggregate. Existing rooms should be migrated as nullable/legacy rooms first; after the web migration is complete, new production rooms should require a family.

### FamilyGraph gRPC contract

Add a read-only authorization contract such as:

```text
GetFamilyAccess(familyId, userId)
  -> found
  -> isMember
  -> role: Head | Member
  -> roomCreationPolicy
  -> roomInvitePolicy
```

Room uses this contract when creating a family room and when applying family-level invite policies. Room never queries the FamilyGraph PostgreSQL database.

### Family domain events

FamilyGraph should publish through a transactional outbox:

- `FamilyMemberRemovedEvent`
- `FamilyHeadTransferredEvent`
- `FamilyDeletedEvent`
- optionally `FamilySettingsChangedEvent`

Room consumes these events idempotently:

- member removal removes the user from every room belonging to that family and emits the existing `RoomMemberRemovedEvent` per affected room;
- family deletion archives all rooms in the family and emits `RoomArchivedEvent`;
- settings changes update only a small local policy projection if synchronous gRPC lookup would be too expensive for the operation.

This event path is mandatory before the UI presents family membership as equivalent to room authorization; otherwise a removed family member could incorrectly retain chat access.

## V2 family settings

Recommended persisted family settings:

- `roomCreationPolicy`: `HeadOnly` | `Members`
- `roomInvitePolicy`: `FamilyOnly` | `RoomAdminsAnyUser`
- `defaultRoomMemberRole`: `Member` | `Muted`

These settings belong to FamilyGraph. Room enforces them through the gRPC authorization contract or an event-fed local projection. Do not copy FamilyGraph tables into the Room database.

## UI behavior

The web client should expose two control planes:

- **Family settings:** create/select family, edit profile, add/remove members, transfer Head, leave/delete.
- **Room settings:** edit room metadata, moderate roles, transfer owner, archive/leave.

Destructive actions require confirmation. Controls must be hidden when the local role obviously lacks permission, while the backend remains authoritative and still returns `403/409` when state changed concurrently.

## Validation matrix

At minimum, automated tests cover:

- only Head manages a family;
- transfer Head produces exactly one Head;
- non-member cannot become Head;
- Head cannot leave/remove self;
- only room owner transfers ownership;
- ownership target must be a room member;
- transferred owner becomes Admin and old owner remains Admin;
- family/room capacity guards;
- existing Admin/Member/Muted moderation rules remain unchanged;
- full distributed E2E remains green.
