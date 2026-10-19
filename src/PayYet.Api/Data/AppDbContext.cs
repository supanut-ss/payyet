using Microsoft.EntityFrameworkCore;

namespace PayYet.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ChatGroup> Groups => Set<ChatGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillItem> BillItems => Set<BillItem>();
    public DbSet<BillCharge> BillCharges => Set<BillCharge>();
    public DbSet<ChargeShare> ChargeShares => Set<ChargeShare>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ConversationState> States => Set<ConversationState>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<RecurringBill> RecurringBills => Set<RecurringBill>();
    public DbSet<LocationPing> LocationPings => Set<LocationPing>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<BillStatus>().HaveConversion<string>();
        b.Properties<ItemStatus>().HaveConversion<string>();
        b.Properties<SplitMode>().HaveConversion<string>();
        b.Properties<PaymentStatus>().HaveConversion<string>();
        b.Properties<MemberSource>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.LineUserId).IsUnique();
            e.Property(x => x.PromptPayId).HasColumnName("promptpay_id");   // snake_case would give prompt_pay_id
        });
        m.Entity<ChatGroup>(e => { e.ToTable("groups"); e.HasIndex(x => x.LineGroupId).IsUnique(); });
        m.Entity<GroupMember>(e => { e.ToTable("group_members"); e.HasKey(x => new { x.GroupId, x.UserId }); });

        m.Entity<Bill>(e =>
        {
            e.ToTable("bills");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.ScopeKey).IsUnique();
            e.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatorId);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId);
        });

        m.Entity<LocationPing>(e =>
        {
            e.ToTable("location_pings");
            e.HasIndex(x => new { x.Name, x.RecordedAt });
        });

        m.Entity<RecurringBill>(e =>
        {
            e.ToTable("recurring_bills");
            e.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatorId);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId);
            e.HasIndex(x => new { x.Active, x.NextRunOn });
        });

        m.Entity<BillItem>(e =>
        {
            e.ToTable("bill_items");
            e.HasOne(x => x.Bill).WithMany(b => b.Items).HasForeignKey(x => x.BillId);
            e.HasOne(x => x.Debtor).WithMany().HasForeignKey(x => x.DebtorId);
            e.HasIndex(x => new { x.BillId, x.DebtorId }).IsUnique();
            e.Ignore(x => x.Remaining);
            e.Ignore(x => x.IsSettled);
        });

        m.Entity<BillCharge>(e =>
        {
            e.ToTable("bill_charges");
            e.HasOne(x => x.Bill).WithMany(b => b.Charges).HasForeignKey(x => x.BillId);
        });

        m.Entity<ChargeShare>(e =>
        {
            e.ToTable("charge_shares");
            e.HasKey(x => new { x.ChargeId, x.BillItemId });
            e.HasOne(x => x.Charge).WithMany(c => c.Shares).HasForeignKey(x => x.ChargeId);
            e.HasOne(x => x.BillItem).WithMany().HasForeignKey(x => x.BillItemId);
        });

        m.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasOne(x => x.BillItem).WithMany(i => i.Payments).HasForeignKey(x => x.BillItemId);
            e.HasIndex(x => x.SlipHash).IsUnique();
            e.HasIndex(x => x.MediaToken).IsUnique();
        });

        m.Entity<ConversationState>(e =>
        {
            e.ToTable("conversation_state");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
        });

        m.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.WebhookEventId);
        });

        m.Entity<AppUser>().Ignore(x => x.Name);
    }
}
