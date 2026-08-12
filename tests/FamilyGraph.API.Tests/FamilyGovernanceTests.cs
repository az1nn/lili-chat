public class FamilyGovernanceTests
{
    readonly Guid headId = Guid.NewGuid();
    readonly Guid memberId = Guid.NewGuid();

    [Theory]
    [InlineData("Head", true)]
    [InlineData("Member", false)]
    [InlineData(null, false)]
    public void OnlyHeadCanManageFamily(string? role, bool expected) =>
        Assert.Equal(expected, FamilyGovernance.CanManage(role));

    [Fact]
    public void HeadCanTransferLeadershipToExistingMember()
    {
        var family = Family("Head", "Member");

        var decision = FamilyGovernance.TransferHead(family, headId, memberId);

        Assert.Equal(FamilyGovernanceDecision.Allowed, decision);
        Assert.Equal("Member", family.Members.Single(m => m.UserId == headId).Role);
        Assert.Equal("Head", family.Members.Single(m => m.UserId == memberId).Role);
        Assert.Single(family.Members.Where(m => m.Role == "Head"));
    }

    [Fact]
    public void MemberCannotTransferLeadership()
    {
        var family = Family("Member", "Head");

        var decision = FamilyGovernance.TransferHead(family, headId, memberId);

        Assert.Equal(FamilyGovernanceDecision.Forbidden, decision);
        Assert.Equal("Member", family.Members.Single(m => m.UserId == headId).Role);
        Assert.Equal("Head", family.Members.Single(m => m.UserId == memberId).Role);
    }

    [Fact]
    public void TransferRequiresTargetMembership()
    {
        var family = Family("Head", "Member");

        var decision = FamilyGovernance.TransferHead(family, headId, Guid.NewGuid());

        Assert.Equal(FamilyGovernanceDecision.TargetNotMember, decision);
        Assert.Equal("Head", family.Members.Single(m => m.UserId == headId).Role);
    }

    [Fact]
    public void TransferToCurrentHeadIsRejected()
    {
        var family = Family("Head", "Member");

        var decision = FamilyGovernance.TransferHead(family, headId, headId);

        Assert.Equal(FamilyGovernanceDecision.TargetAlreadyHead, decision);
        Assert.Equal("Head", family.Members.Single(m => m.UserId == headId).Role);
    }

    [Fact]
    public void FamilyMemberLimitIsExplicitAndBounded() =>
        Assert.Equal(100, FamilyPolicy.MaxMembers);

    FamilyEntity Family(string headRole, string memberRole) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Casa",
        Members =
        [
            new FamilyMember { Id = Guid.NewGuid(), UserId = headId, Role = headRole },
            new FamilyMember { Id = Guid.NewGuid(), UserId = memberId, Role = memberRole }
        ]
    };
}
