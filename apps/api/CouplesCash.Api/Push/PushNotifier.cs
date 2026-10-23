using System.Threading.Channels;
using CouplesCash.Api.Data;

namespace CouplesCash.Api.Push;

/// <summary>Who should get a payload: one user, or a household's members (optionally only those with activity pushes on, minus the actor).</summary>
public sealed record PushJob(PushPayload Payload, Guid? UserId, Guid? HouseholdId, Guid? ExcludeUserId, bool OnlyActivityEnabled);

/// <summary>
/// Fire-and-forget queue in front of the push service. Requests enqueue and return immediately;
/// <see cref="PushDispatchService"/> resolves recipients, sends, and prunes dead subscriptions.
/// Never throws into the caller: when the queue is full the notification is dropped and logged.
/// </summary>
public sealed class PushNotifier(ILogger<PushNotifier> logger)
{
    private readonly Channel<PushJob> _queue = Channel.CreateBounded<PushJob>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public ChannelReader<PushJob> Reader => _queue.Reader;

    public void ToUser(Guid userId, PushPayload payload) =>
        Enqueue(new PushJob(payload, userId, null, null, OnlyActivityEnabled: false));

    /// <summary>Partner-activity push: every member except the actor who has <c>activityEnabled</c>.</summary>
    public void ToHousehold(Guid householdId, Guid? exceptUserId, PushPayload payload) =>
        Enqueue(new PushJob(payload, null, householdId, exceptUserId, OnlyActivityEnabled: true));

    private void Enqueue(PushJob job)
    {
        if (!_queue.Writer.TryWrite(job))
        {
            logger.LogWarning("Push queue full; dropping notification {Tag}", job.Payload.Tag);
        }
    }

    public void Complete() => _queue.Writer.TryComplete();
}

/// <summary>Consumes <see cref="PushNotifier"/>: 404/410 delete the subscription, other failures count up and delete at <see cref="MaxFailures"/>.</summary>
public sealed class PushDispatchService(
    PushNotifier notifier,
    IPushSender sender,
    PushRepository pushes,
    TimeProvider time,
    ILogger<PushDispatchService> logger) : BackgroundService
{
    public const int MaxFailures = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in notifier.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DispatchAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Push dispatch failed for {Tag}", job.Payload.Tag);
            }
        }
    }

    public async Task DispatchAsync(PushJob job, CancellationToken ct)
    {
        if (!sender.Enabled)
        {
            return;
        }

        IReadOnlyList<Guid> recipients = job switch
        {
            { UserId: { } userId } => [userId],
            { HouseholdId: { } householdId, OnlyActivityEnabled: true } => await pushes.ActivityRecipientsAsync(householdId, job.ExcludeUserId, ct),
            _ => [],
        };

        var subscriptions = await pushes.SubscriptionsForUsersAsync(recipients, ct);
        foreach (var subscription in subscriptions)
        {
            var status = await sender.SendAsync(subscription, job.Payload, ct);
            switch (status)
            {
                case PushSendStatus.Delivered:
                    await pushes.MarkDeliveredAsync(subscription.Id, time.GetUtcNow().UtcDateTime, ct);
                    break;
                case PushSendStatus.Gone:
                    await pushes.DeleteSubscriptionAsync(subscription.Id, ct);
                    break;
                default:
                    var failures = await pushes.RecordFailureAsync(subscription.Id, ct);
                    if (failures >= MaxFailures)
                    {
                        logger.LogInformation("Push subscription {SubscriptionId} failed {Failures} times; removing", subscription.Id, failures);
                        await pushes.DeleteSubscriptionAsync(subscription.Id, ct);
                    }

                    break;
            }
        }
    }
}
