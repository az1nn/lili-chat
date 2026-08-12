using System.Security.Cryptography;
using System.Text;
using FamilyChat.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Gateway.Tests;

public class ProductionSecurityTests
{
    [Fact]
    public void Production_DoesNotFallBackToDevelopmentJwtSecret()
    {
        var configuration = Configuration(("JWT:Secret", new string('x', 64)));
        var environment = Environment("Production");

        Assert.Throws<InvalidOperationException>(() =>
            JwtKeyFactory.SigningKey(configuration, environment));
        Assert.Throws<InvalidOperationException>(() =>
            JwtKeyFactory.ValidationKey(configuration, environment));
    }

    [Fact]
    public void Development_RequiresAtLeast32BytesForSymmetricJwtSecret()
    {
        var environment = Environment("Development");

        Assert.Throws<InvalidOperationException>(() => JwtKeyFactory.SigningKey(
            Configuration(("JWT:Secret", new string('x', 31))), environment));

        var key = JwtKeyFactory.SigningKey(
            Configuration(("JWT:Secret", new string('x', 32))), environment);

        Assert.IsType<SymmetricSecurityKey>(key);
        Assert.Equal(SecurityAlgorithms.HmacSha256, JwtKeyFactory.Algorithm(key));
    }

    [Fact]
    public void Production_RsaSigningAndValidationKeysUseRs256AndStaySeparated()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = Encode(rsa.ExportPkcs8PrivateKeyPem());
        var publicKey = Encode(rsa.ExportSubjectPublicKeyInfoPem());
        var environment = Environment("Production");

        var signingKey = Assert.IsType<RsaSecurityKey>(JwtKeyFactory.SigningKey(
            Configuration(("JWT:PrivateKeyBase64", privateKey)), environment));
        var validationKey = Assert.IsType<RsaSecurityKey>(JwtKeyFactory.ValidationKey(
            Configuration(("JWT:PublicKeyBase64", publicKey)), environment));

        Assert.Equal(SecurityAlgorithms.RsaSha256, JwtKeyFactory.Algorithm(signingKey));
        Assert.Equal(SecurityAlgorithms.RsaSha256, JwtKeyFactory.Algorithm(validationKey));
        Assert.NotNull(signingKey.Rsa);
        Assert.NotNull(validationKey.Rsa);
        Assert.NotEmpty(signingKey.Rsa!.ExportParameters(true).D!);
        Assert.Throws<CryptographicException>(() => validationKey.Rsa!.ExportParameters(true));
    }

    [Fact]
    public void Production_SigningRejectsPublicOnlyRsaKey()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Encode(rsa.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<InvalidOperationException>(() => JwtKeyFactory.SigningKey(
            Configuration(("JWT:PrivateKeyBase64", publicKey)), Environment("Production")));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("bm90IGEgcGVt")]
    public void Production_RejectsMalformedRsaMaterial(string encoded)
    {
        var environment = Environment("Production");
        Assert.Throws<InvalidOperationException>(() => JwtKeyFactory.SigningKey(
            Configuration(("JWT:PrivateKeyBase64", encoded)), environment));
        Assert.Throws<InvalidOperationException>(() => JwtKeyFactory.ValidationKey(
            Configuration(("JWT:PublicKeyBase64", encoded)), environment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567890123456789012345678901")]
    public void InternalServiceToken_RejectsMissingOrShortValues(string? token)
    {
        Assert.Throws<InvalidOperationException>(() => InternalServiceAuth.RequiredToken(
            Configuration(("InternalAuth:Token", token)), "InternalAuth:Token"));
    }

    [Fact]
    public void InternalServiceToken_RequiresExactConstantTimeMatch()
    {
        var expected = new string('a', 32);
        var configuration = Configuration(("InternalAuth:Token", expected));

        Assert.Equal(expected, InternalServiceAuth.RequiredToken(configuration, "InternalAuth:Token"));
        Assert.True(InternalServiceAuth.IsValid(expected, expected));
        Assert.False(InternalServiceAuth.IsValid(null, expected));
        Assert.False(InternalServiceAuth.IsValid(new string('b', 32), expected));
        Assert.False(InternalServiceAuth.IsValid(expected + "x", expected));
    }

    static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(value => value.Key, value => value.Value)).Build();

    static IHostEnvironment Environment(string name) => new TestHostEnvironment
    {
        EnvironmentName = name
    };

    static string Encode(string pem) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));

    sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "security-tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
