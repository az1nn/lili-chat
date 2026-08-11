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
}
