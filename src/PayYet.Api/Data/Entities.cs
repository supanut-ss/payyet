namespace PayYet.Data;

// Enum member names are upper-case on purpose: they are stored as strings and match the MySQL ENUM values.
public enum BillStatus { OPEN, CLOSED, CANCELLED }
public enum ItemStatus { PENDING, SUBMITTED, PAID, REJECTED, WAIVED }
public enum SplitMode { EQUAL, CUSTOM }
public enum PaymentStatus { SUBMITTED, APPROVED, REJECTED }
public enum MemberSource { LISTED, JOINED, SEEN }

public class AppUser
{
    public long Id { get; set; }
    public string LineUserId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? PictureUrl { get; set; }            // LINE profile picture; null = not fetched yet, "" = has none
    public string? PromptPayId { get; set; }
    public bool IsFriend { get; set; }
    public long? LastGroupId { get; set; }
    public int RemindEveryDays { get; set; } = 1;    // default for the bills this user creates: 0 = never, 1 = daily, 3, 7
    public DateTime CreatedAt { get; set; }

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? "(ไม่ทราบชื่อ)" : DisplayName!;
}

public class ChatGroup
{
    public long Id { get; set; }
    public string LineGroupId { get; set; } = "";
    public string? Name { get; set; }
    public string? PictureUrl { get; set; }            // LINE group picture; null = not fetched yet, "" = has none
    public int? MemberCount { get; set; }
    public DateTime? MemberCountAt { get; set; }
    public bool Active { get; set; } = true;
}

public class GroupMember
{
    public long GroupId { get; set; }
    public long UserId { get; set; }
    public DateTime JoinedAt { get; set; }
    public MemberSource Source { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public ChatGroup Group { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}

public class Bill
{
    public long Id { get; set; }
    public string Token { get; set; } = "";             // public id used in LIFF links
    public long CreatorId { get; set; }
    public AppUser Creator { get; set; } = null!;
    public long? GroupId { get; set; }                  // null = individual bill
    public ChatGroup? Group { get; set; }
    public string Title { get; set; } = "";
    public DateTime? DueAt { get; set; }
    public DateOnly? LastRemindedOn { get; set; }
    public int RemindEveryDays { get; set; } = 1;       // 0 = no reminders for this bill
    public BillStatus Status { get; set; } = BillStatus.OPEN;
    public string? ScopeKey { get; set; }               // set only while OPEN → one open bill per scope
    public string? InviteCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<BillItem> Items { get; set; } = new();
    public List<BillCharge> Charges { get; set; } = new();
}

/// <summary>A bill the collector asked to be created again every week or month (created by the daily job).</summary>
public class RecurringBill
{
    public long Id { get; set; }
    public long CreatorId { get; set; }
    public AppUser Creator { get; set; } = null!;
    public long? GroupId { get; set; }                  // null = private bills
    public ChatGroup? Group { get; set; }
    public string Dest { get; set; } = "G";             // G or P, same meaning as Draft.Dest
    public string Title { get; set; } = "";
    public long AmountSatang { get; set; }
    public bool PerPerson { get; set; }
    public string UserIdsJson { get; set; } = "[]";     // debtors, as user ids
    public string? AmountsJson { get; set; }            // optional {userId: satang} when the amounts were edited per person
    public string Frequency { get; set; } = "MONTHLY";  // WEEKLY | MONTHLY
    public int AnchorDay { get; set; }                  // day of month for MONTHLY (clamped to the month length)
    public DateOnly NextRunOn { get; set; }
    public bool Active { get; set; } = true;
    public DateOnly? LastRunOn { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BillItem
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public Bill Bill { get; set; } = null!;
    public long? DebtorId { get; set; }
    public AppUser? Debtor { get; set; }
    public long AmountSatang { get; set; }
    public long PaidSatang { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.PENDING;
    public DateTime? PaidAt { get; set; }
    public List<Payment> Payments { get; set; } = new();

    public long Remaining => Math.Max(0, AmountSatang - PaidSatang);
    public bool IsSettled => Status is ItemStatus.PAID or ItemStatus.WAIVED;
}

public class BillCharge
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public Bill Bill { get; set; } = null!;
    public string Title { get; set; } = "";
    public long TotalSatang { get; set; }
    public SplitMode SplitMode { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ChargeShare> Shares { get; set; } = new();
}

public class ChargeShare
{
    public long ChargeId { get; set; }
    public BillCharge Charge { get; set; } = null!;
    public long BillItemId { get; set; }
    public BillItem BillItem { get; set; } = null!;
    public long AmountSatang { get; set; }
}

public class Payment
{
    public long Id { get; set; }
    public long BillItemId { get; set; }
    public BillItem BillItem { get; set; } = null!;
    public string SlipPath { get; set; } = "";          // relative to App_Data/slips
    public string? SlipRef { get; set; }
    public string SlipHash { get; set; } = "";
    public string MediaToken { get; set; } = "";
    public long AmountSatang { get; set; }
    public string? VerifiedBy { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.SUBMITTED;
    public DateTime CreatedAt { get; set; }
}

public class ConversationState
{
    public long UserId { get; set; }
    public string Flow { get; set; } = "";
    public string Step { get; set; } = "";
    public string? Data { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class ProcessedEvent
{
    public string WebhookEventId { get; set; } = "";
    public DateTime ProcessedAt { get; set; }
}
