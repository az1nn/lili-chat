using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FamilyChat.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Room.API.Tests;

public class JwtKeyFactoryTests
{
    [Fact]
    public void InternalServiceTokenRequiresMinimumEntropy()
    {
        var configuration = Config(("InternalAuth:Token", "short"));
        Assert.Throws<InvalidOperationException>(() =>
            InternalServiceAuth.RequiredToken(configuration, "InternalAuth:Token"));
    }

    [Fact]
    public void InternalServiceTokenUsesExactComparison()
    {
        var token = new string('t', 32);
        Assert.True(InternalServiceAuth.IsValid(token, token));
        Assert.False(InternalServiceAuth.IsValid(token + "x", token));
        Assert.False(InternalServiceAuth.IsValid(new string('x', 32), token));
        Assert.False(InternalServiceAuth.IsValid(null, token));
    }

    [Fact]
    public void ProductionRejectsSymmetricFallback()
    {
        var configuration = Config(("JWT:Secret", new string('x', 32)));

        Assert.Throws<InvalidOperationException>(() =>
            JwtKeyFactory.ValidationKey(configuration, Environment("Production")));
    }

    [Fact]
    public void DevelopmentRejectsShortSymmetricSecret()
    {
        var configuration = Config(("JWT:Secret", "too-short"));

        Assert.Throws<InvalidOperationException>(() =>
            JwtKeyFactory.ValidationKey(configuration, Environment("Development")));
    }

    [Fact]
    public void PublicRsaKeyValidatesIdentitySignature()
    {
        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var configuration = Config(
            ("JWT:PrivateKeyBase64", Encode(privatePem)),
            ("JWT:PublicKeyBase64", Encode(publicPem)));
        var environment = Environment("Production");
        var signingKey = JwtKeyFactory.SigningKey(configuration, environment);
        var validationKey = JwtKeyFactory.ValidationKey(configuration, environment);
        var token = new JwtSecurityToken(
            claims: [new Claim("sub", Guid.NewGuid().ToString())],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey, JwtKeyFactory.Algorithm(signingKey)));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var encoded = handler.WriteToken(token);

        var principal = handler.ValidateToken(encoded, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = validationKey
        }, out _);

        Assert.NotNull(principal.FindFirst("sub"));
        Assert.IsType<RsaSecurityKey>(signingKey);
        var publicKey = Assert.IsType<RsaSecurityKey>(validationKey);
        Assert.Throws<CryptographicException>(() => publicKey.Rsa.ExportParameters(true));
    }

    static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    static string Encode(string pem) => Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));

    static IHostEnvironment Environment(string name) => new TestEnvironment { EnvironmentName = name };

    sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
