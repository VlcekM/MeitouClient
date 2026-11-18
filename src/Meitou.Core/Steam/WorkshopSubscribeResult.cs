namespace Meitou.Steam;

/// <summary>
/// How subscribing to one Workshop item ended: <paramref name="Problem"/> is the reason (in English) when Steam did not subscribe the account,
/// else null, with whether the item was <paramref name="Installed"/> before the wait ended (Steam goes on downloading by itself).
/// </summary>
public sealed record WorkshopSubscription(ulong Id, string? Problem, bool Installed);

/// <summary>
/// How a <see cref="WorkshopSubscribe"/> ended: <paramref name="Problem"/> is the reason (in English) when Steam could not be asked at all,
/// else one entry of <paramref name="Items"/> per item asked for.
/// </summary>
public sealed record WorkshopSubscribeResult(string? Problem, IReadOnlyList<WorkshopSubscription> Items);
