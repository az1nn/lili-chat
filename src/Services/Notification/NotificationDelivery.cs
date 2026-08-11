using System.Net;
using System.Net.Mail;
using FamilyChat.Contracts.Family;
using FamilyChat.Contracts.Events;

record NotificationOptions(
    bool Enabled,
    string Host,
    int Port,
    bool EnableSsl,
    string From,
    string? Username,
    string? Password,
    bool IncludeContent)
{
    public static NotificationOptions Load(IConfiguration configuration)
    {
        var provider = configuration["Notifications:Provider"]?.Trim() ?? "Disabled";
        if (provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            return new(false, "", 25, false, "", null, null, false);
        if (!provider.Equals("Smtp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Notifications:Provider must be Disabled or Smtp.");

        var host = configuration["Notifications:Smtp:Host"]?.Trim();
        var from = configuration["Notifications:Smtp:From"]?.Trim();
        var username = NullIfBlank(configuration["Notifications:Smtp:Username"]);
        var password = NullIfBlank(configuration["Notifications:Smtp:Password"]);
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Notifications:Smtp:Host is required for the Smtp provider.");
        if (!MailAddress.TryCreate(from, out _))
            throw new InvalidOperationException("Notifications:Smtp:From must be a valid email address.");
        if ((username is null) != (password is null))
            throw new InvalidOperationException("SMTP username and password must be configured together.");

        var port = configuration.GetValue("Notifications:Smtp:Port", 587);
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Notifications:Smtp:Port must be between 1 and 65535.");
        return new(true, host, port,
            configuration.GetValue("Notifications:Smtp:EnableSsl", true), from!,
            username, password,
            configuration.GetValue("Notifications:IncludeContent", false));
    }

    static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

record NotificationEnvelope(Guid MessageId, string Recipient, string RoomName, string Content);

interface INotificationSender
{
    Task SendAsync(NotificationEnvelope notification, CancellationToken cancellationToken);
}

sealed class SmtpNotificationSender(NotificationOptions options) : INotificationSender
{
    public async Task SendAsync(NotificationEnvelope notification, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        using var message = CreateMessage(options, notification);

        using var smtp = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };
        if (options.Username is not null)
            smtp.Credentials = new NetworkCredential(options.Username, options.Password);
        await smtp.SendMailAsync(message, cancellationToken);
    }

    internal static MailMessage CreateMessage(
        NotificationOptions options, NotificationEnvelope notification)
    {
        var message = new MailMessage
        {
            From = new MailAddress(options.From),
            Subject = $"Nova mensagem em {notification.RoomName}",
            Body = options.IncludeContent
                ? $"Você recebeu uma nova mensagem em {notification.RoomName}:\n\n{notification.Content}"
                : $"Você recebeu uma nova mensagem em {notification.RoomName}.",
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(notification.Recipient));
        message.Headers.Add("X-FamilyChat-MessageId", notification.MessageId.ToString());
        return message;
    }
}

record NotificationRecipient(Guid UserId, string Email);

static class NotificationRecipients
{
    public static IReadOnlyList<NotificationRecipient> Valid(
        IEnumerable<UserInfoResponse> users, Guid senderId) => users
        .Where(user => user.Found && Guid.TryParse(user.UserId, out var id) && id != senderId)
        .Select(user => new NotificationRecipient(Guid.Parse(user.UserId), user.Email.Trim()))
        .Where(user => MailAddress.TryCreate(user.Email, out _))
        .DistinctBy(user => user.UserId)
        .ToArray();
}

static class NotificationTargetSnapshot
{
    public static bool TryRead(
        MessageCreatedEvent message,
        out string roomName,
        out IReadOnlyCollection<string> userIds)
    {
        roomName = message.RoomName?.Trim() ?? "";
        userIds = message.NotificationUserIds ?? [];
        return roomName.Length > 0 && userIds.Count > 0;
    }
}

static class NotificationFailure
{
    public static string SafeMessage(Exception exception)
    {
        var value = $"{exception.GetType().Name}: {exception.Message}";
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 500 ? value : value[..500];
    }
}

static class NotificationStatuses
{
    public const string Queued = "queued";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string ProviderDisabled = "provider-disabled";
    public const string NoRecipients = "no-recipients";
}
