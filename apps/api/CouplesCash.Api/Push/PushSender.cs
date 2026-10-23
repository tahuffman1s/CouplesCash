using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CouplesCash.Api.Data;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace CouplesCash.Api.Push;

/// <summary>What the service worker receives: it shows a notification and opens <see cref="Url"/> on click.</summary>
public sealed record PushPayload(string Title, string Body, string Url, string Tag, string Icon = PushPayload.DefaultIcon)
{
    public const string DefaultIcon = "/icons/icon-192.png";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

public enum PushSendStatus
{
    /// <summary>Accepted by the push service.</summary>
    Delivered,

    /// <summary>404 / 410: the subscription no longer exists and must be deleted.</summary>
    Gone,

    /// <summary>Any other failure; counts towards the subscription's failure budget.</summary>
    Failed,
}

public interface IPushSender
{
    bool Enabled { get; }

    Task<PushSendStatus> SendAsync(PushSubscriptionRow subscription, PushPayload payload, CancellationToken ct);
}

/// <summary>Lib.Net.Http.WebPush (RFC 8291 aes128gcm, RFC 8292 VAPID).</summary>
public sealed class WebPushSender : IPushSender
{
    private readonly PushServiceClient? _client;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(PushOptions options, HttpClient httpClient, ILogger<WebPushSender> logger)
    {
        _logger = logger;
        if (options.Enabled)
        {
            _client = new PushServiceClient(httpClient)
            {
                DefaultAuthentication = new VapidAuthentication(options.VapidPublicKey!, options.VapidPrivateKey!) { Subject = options.EffectiveSubject },
                DefaultTimeToLive = (int)TimeSpan.FromHours(12).TotalSeconds,
            };
        }
    }

    public bool Enabled => _client is not null;

    public async Task<PushSendStatus> SendAsync(PushSubscriptionRow subscription, PushPayload payload, CancellationToken ct)
    {
        if (_client is null)
        {
            return PushSendStatus.Failed;
        }

        var target = new PushSubscription { Endpoint = subscription.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

        var message = new PushMessage(payload.ToJson()) { Topic = Topic(payload.Tag), Urgency = PushMessageUrgency.Normal };
        try
        {
            await _client.RequestPushMessageDeliveryAsync(target, message, ct);
            return PushSendStatus.Delivered;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _logger.LogInformation("Push subscription {SubscriptionId} is gone ({Status}); removing", subscription.Id, (int)ex.StatusCode);
            return PushSendStatus.Gone;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Push to subscription {SubscriptionId} failed", subscription.Id);
            return PushSendStatus.Failed;
        }
    }

    /// <summary>Topic must be ≤ 32 URL-safe base64 characters; collapses repeated notifications of the same thing.</summary>
    private static string? Topic(string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        var safe = new string(tag.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return safe.Length == 0 ? null : safe.Length <= 32 ? safe : safe[..32];
    }
}
