using FamilyChat.Contracts.Family;
using FamilyChat.Contracts.Events;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Notification.API.Tests;

public class NotificationDeliveryTests
{
    [Fact]
    public void DisabledProvider_NeedsNoSmtpSecrets()
    {
        var options = NotificationOptions.Load(Configuration());

        Assert.False(options.Enabled);
    }

    [Fact]
    public void SmtpProvider_RequiresValidCompleteConfiguration()
    {
        var missingHost = Configuration(new() { ["Notifications:Provider"] = "Smtp" });
        Assert.Throws<InvalidOperationException>(() => NotificationOptions.Load(missingHost));

        var unpairedCredential = Configuration(new()
        {
            ["Notifications:Provider"] = "Smtp",
            ["Notifications:Smtp:Host"] = "smtp.example.test",
            ["Notifications:Smtp:From"] = "chat@example.test",
            ["Notifications:Smtp:Username"] = "user"
        });
        Assert.Throws<InvalidOperationException>(() => NotificationOptions.Load(unpairedCredential));
    }

    [Fact]
    public void RecipientSelection_ExcludesSenderInvalidEmailsAndDuplicates()
    {
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var users = new[]
        {
            User(sender, "sender@example.test"),
            User(recipient, "recipient@example.test"),
            User(recipient, "duplicate@example.test"),
            User(Guid.NewGuid(), "not-an-email"),
            new UserInfoResponse { Found = false, UserId = Guid.NewGuid().ToString(), Email = "x@example.test" }
        };

        var selected = NotificationRecipients.Valid(users, sender);

        var delivery = Assert.Single(selected);
        Assert.Equal(recipient, delivery.UserId);
        Assert.Equal("recipient@example.test", delivery.Email);
    }

    [Fact]
    public void Email_HidesMessageContentByDefaultAndCarriesIdempotencyHeader()
    {
        var options = new NotificationOptions(true, "smtp.example.test", 25, false,
            "chat@example.test", null, null, IncludeContent: false);
        var id = Guid.NewGuid();

        using var message = SmtpNotificationSender.CreateMessage(options,
            new NotificationEnvelope(id, "recipient@example.test", "Família", "segredo"));

        Assert.DoesNotContain("segredo", message.Body);
        Assert.Contains("Família", message.Subject);
        Assert.Equal(id.ToString(), message.Headers["X-FamilyChat-MessageId"]);
        Assert.Equal("recipient@example.test", Assert.Single(message.To).Address);
    }

    [Fact]
    public void FailureMessage_IsSingleLineAndBounded()
    {
        var result = NotificationFailure.SafeMessage(new InvalidOperationException(
            new string('x', 600) + "\r\nsecret"));

        Assert.Equal(500, result.Length);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
    }

    [Fact]
    public async Task SmtpSender_DeliversToARealProtocolEndpoint()
    {
        await using var server = new FakeSmtpServer();
        var options = new NotificationOptions(true, "127.0.0.1", server.Port, false,
            "chat@example.test", null, null, IncludeContent: false);
        var id = Guid.NewGuid();
        var sender = new SmtpNotificationSender(options);

        await sender.SendAsync(new NotificationEnvelope(
            id, "recipient@example.test", "Família", "segredo"), CancellationToken.None);
        var payload = await server.Message.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains($"X-FamilyChat-MessageId: {id}", payload);
        Assert.Contains("recipient@example.test", payload);
        Assert.DoesNotContain("segredo", payload);
    }

    [Fact]
    public void EventSnapshot_PreservesRecipientsFromAuthorizedSendTime()
    {
        var recipient = Guid.NewGuid().ToString();
        var message = new MessageCreatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "olá",
            DateTimeOffset.UtcNow, Guid.NewGuid(), " Família ", [recipient]);

        var found = NotificationTargetSnapshot.TryRead(message, out var roomName, out var users);

        Assert.True(found);
        Assert.Equal("Família", roomName);
        Assert.Equal([recipient], users);
    }

    static UserInfoResponse User(Guid id, string email) => new()
    {
        Found = true,
        UserId = id.ToString(),
        Email = email,
        Username = "user"
    };

    static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();

    sealed class FakeSmtpServer : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource cancellation = new();
        readonly TaskCompletionSource<string> message = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Task serverTask;

        public FakeSmtpServer()
        {
            listener.Start();
            serverTask = RunAsync(cancellation.Token);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Task<string> Message => message.Task;

        async Task RunAsync(CancellationToken ct)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };
                await writer.WriteLineAsync("220 localhost ESMTP");
                var payload = new StringBuilder();
                var readingData = false;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (readingData)
                    {
                        if (line == ".")
                        {
                            message.TrySetResult(payload.ToString());
                            readingData = false;
                            await writer.WriteLineAsync("250 queued");
                        }
                        else payload.AppendLine(line);
                    }
                    else if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        readingData = true;
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    }
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    }
                    else await writer.WriteLineAsync("250 OK");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            try { await serverTask; } catch (SocketException) when (cancellation.IsCancellationRequested) { }
            cancellation.Dispose();
        }
    }
}
