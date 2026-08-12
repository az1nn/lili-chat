namespace Room.API.Tests;

public class RoomAuthorizationTests
{
    readonly Guid ownerId = Guid.NewGuid();
    readonly Guid adminId = Guid.NewGuid();
    readonly Guid memberId = Guid.NewGuid();

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Member", false)]
    [InlineData("Muted", false)]
    [InlineData(null, false)]
    public void OnlyAdminRoleCanManageRoom(string? role, bool expected) =>
        Assert.Equal(expected, RoomAuthorization.CanManageRoom(role));

    [Fact]
    public void OwnerCanAssignAdminWhenAddingMember() =>
        Assert.True(RoomAuthorization.CanAssignRole(ownerId, "Admin", ownerId, "Admin"));

    [Fact]
    public void NonOwnerAdminCannotAssignAdminWhenAddingMember() =>
        Assert.False(RoomAuthorization.CanAssignRole(adminId, "Admin", ownerId, "Admin"));

    [Theory]
    [InlineData("Member")]
    [InlineData("Muted")]
    public void AdminCanAssignNonAdminRoleWhenAddingMember(string role) =>
        Assert.True(RoomAuthorization.CanAssignRole(adminId, "Admin", ownerId, role));

    [Fact]
    public void NonAdminCannotAssignAnyRoleWhenAddingMember() =>
        Assert.False(RoomAuthorization.CanAssignRole(memberId, "Member", ownerId, "Member"));

    [Fact]
    public void OwnerCanPromoteMemberToAdmin() =>
        Assert.Equal(AuthorizationDecision.Allowed,
            RoomAuthorization.CanChangeRole(ownerId, "Admin", ownerId, memberId, "Member", "Admin"));

    [Fact]
    public void AdminCannotPromoteAnotherMemberToAdmin() =>
        Assert.Equal(AuthorizationDecision.Forbidden,
            RoomAuthorization.CanChangeRole(adminId, "Admin", ownerId, memberId, "Member", "Admin"));

    [Fact]
    public void AdminCanMuteMember() =>
        Assert.Equal(AuthorizationDecision.Allowed,
            RoomAuthorization.CanChangeRole(adminId, "Admin", ownerId, memberId, "Member", "Muted"));

    [Fact]
    public void OwnerRoleCannotBeChanged() =>
        Assert.Equal(AuthorizationDecision.OwnerProtected,
            RoomAuthorization.CanChangeRole(ownerId, "Admin", ownerId, ownerId, "Admin", "Member"));

    [Fact]
    public void AdminCannotRemoveAnotherAdmin() =>
        Assert.Equal(AuthorizationDecision.Forbidden,
            RoomAuthorization.CanRemove(adminId, "Admin", ownerId, Guid.NewGuid(), "Admin"));

    [Fact]
    public void OwnerCanRemoveAdmin() =>
        Assert.Equal(AuthorizationDecision.Allowed,
            RoomAuthorization.CanRemove(ownerId, "Admin", ownerId, adminId, "Admin"));

    [Fact]
    public void OwnerCannotBeRemoved() =>
        Assert.Equal(AuthorizationDecision.OwnerProtected,
            RoomAuthorization.CanRemove(ownerId, "Admin", ownerId, ownerId, "Admin"));

    [Theory]
    [InlineData("Member")]
    [InlineData("Muted")]
    public void NonAdminCannotRemoveMember(string actorRole) =>
        Assert.Equal(AuthorizationDecision.Forbidden,
            RoomAuthorization.CanRemove(Guid.NewGuid(), actorRole, ownerId, memberId, "Member"));

    [Fact]
    public void OwnerCanTransferOwnershipToExistingMember()
    {
        var room = Room();

        var decision = RoomOwnership.Transfer(room, ownerId, memberId);

        Assert.Equal(RoomOwnershipDecision.Allowed, decision);
        Assert.Equal(memberId, room.OwnerId);
        Assert.Equal("Admin", room.Members.Single(m => m.UserId == memberId).Role);
        Assert.Equal("Admin", room.Members.Single(m => m.UserId == ownerId).Role);
    }

    [Fact]
    public void NonOwnerCannotTransferOwnership()
    {
        var room = Room();

        var decision = RoomOwnership.Transfer(room, adminId, memberId);

        Assert.Equal(RoomOwnershipDecision.Forbidden, decision);
        Assert.Equal(ownerId, room.OwnerId);
    }

    [Fact]
    public void OwnershipTransferRequiresTargetMembership()
    {
        var room = Room();

        var decision = RoomOwnership.Transfer(room, ownerId, Guid.NewGuid());

        Assert.Equal(RoomOwnershipDecision.TargetNotMember, decision);
        Assert.Equal(ownerId, room.OwnerId);
    }

    [Fact]
    public void OwnershipTransferToCurrentOwnerIsRejected()
    {
        var room = Room();

        var decision = RoomOwnership.Transfer(room, ownerId, ownerId);

        Assert.Equal(RoomOwnershipDecision.TargetAlreadyOwner, decision);
        Assert.Equal(ownerId, room.OwnerId);
    }

    [Fact]
    public void RoomMemberLimitIsExplicitAndBounded() =>
        Assert.Equal(250, RoomPolicy.MaxMembers);

    RoomEntity Room() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Geral",
        OwnerId = ownerId,
        Members =
        [
            new RoomMember { Id = Guid.NewGuid(), UserId = ownerId, Role = "Admin" },
            new RoomMember { Id = Guid.NewGuid(), UserId = adminId, Role = "Admin" },
            new RoomMember { Id = Guid.NewGuid(), UserId = memberId, Role = "Member" }
        ]
    };
}
