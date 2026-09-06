using CertFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CertFlow.Infrastructure.Persistence;

public class CertFlowDbContext(DbContextOptions<CertFlowDbContext> options) : DbContext(options)
{
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<ExamProgram> ExamPrograms => Set<ExamProgram>();
    public DbSet<ReschedulePolicy> ReschedulePolicies => Set<ReschedulePolicy>();
    public DbSet<ExamVoucher> ExamVouchers => Set<ExamVoucher>();
    public DbSet<TestCenter> TestCenters => Set<TestCenter>();
    public DbSet<AppointmentSlot> AppointmentSlots => Set<AppointmentSlot>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<RescheduleRequest> RescheduleRequests => Set<RescheduleRequest>();
    public DbSet<BulkRescheduleSession> BulkRescheduleSessions => Set<BulkRescheduleSession>();
    public DbSet<ProcessedInboundMessage> ProcessedInboundMessages => Set<ProcessedInboundMessage>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Candidate>(e =>
        {
            e.HasKey(c => c.EntraUserId);
            e.Property(c => c.EntraUserId).HasMaxLength(36);
            e.Property(c => c.Email).HasMaxLength(256).IsRequired();
            e.Property(c => c.DisplayName).HasMaxLength(256).IsRequired();
        });

        m.Entity<ExamProgram>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Code).HasMaxLength(20).IsRequired();
            e.HasOne(p => p.Policy).WithOne(pol => pol.ExamProgram)
                .HasForeignKey<ReschedulePolicy>(pol => pol.ExamProgramId);
        });

        m.Entity<ExamVoucher>(e =>
        {
            e.HasKey(v => v.Id);
            e.HasOne(v => v.Candidate).WithMany(c => c.Vouchers)
                .HasForeignKey(v => v.CandidateEntraUserId);
            e.HasOne(v => v.ExamProgram).WithMany(p => p.Vouchers)
                .HasForeignKey(v => v.ExamProgramId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<TestCenter>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.City).HasMaxLength(100);
            e.Property(t => t.Country).HasMaxLength(100);
        });

        m.Entity<AppointmentSlot>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.RowVersion).IsRowVersion();
            e.HasOne(s => s.TestCenter).WithMany(t => t.Slots)
                .HasForeignKey(s => s.TestCenterId);
        });

        m.Entity<Appointment>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.OrderNumber).HasMaxLength(50);
            e.Property(a => a.RegistrationId).HasMaxLength(50);
            e.HasOne(a => a.Candidate).WithMany(c => c.Appointments)
                .HasForeignKey(a => a.CandidateEntraUserId);
            // Restrict: Candidate already cascades to Appointments directly, so a second
            // cascade path via ExamVoucher/Slot would create multiple cascade paths (SQL 1785).
            e.HasOne(a => a.Slot).WithMany().HasForeignKey(a => a.SlotId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Voucher).WithMany().HasForeignKey(a => a.VoucherId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<RescheduleRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.CorrelationToken).HasMaxLength(20).IsRequired();
            e.HasIndex(r => r.CorrelationToken).IsUnique();
            e.Property(r => r.IdempotencyKey).HasMaxLength(64);
            e.HasIndex(r => r.IdempotencyKey).IsUnique();
            e.Property(r => r.ProposedSlotIds).HasConversion(
                v => string.Join(',', v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(Guid.Parse).ToList(),
                new ValueComparer<List<Guid>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (acc, id) => HashCode.Combine(acc, id.GetHashCode())),
                    v => v.ToList()));
            e.HasOne(r => r.Appointment).WithMany()
                .HasForeignKey(r => r.AppointmentId)
                .OnDelete(DeleteBehavior.Restrict);
            // Cascade: deleting a bulk session should take its children with it, since a
            // child has no meaning on its own.
            e.HasOne(r => r.BulkSession).WithMany(b => b.ChildRequests)
                .HasForeignKey(r => r.BulkSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<BulkRescheduleSession>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.CorrelationToken).HasMaxLength(20).IsRequired();
            e.HasIndex(b => b.CorrelationToken).IsUnique();
            e.Property(b => b.IdempotencyKey).HasMaxLength(64).IsRequired();
            e.HasIndex(b => b.IdempotencyKey).IsUnique();
            e.Property(b => b.CandidateEntraUserId).HasMaxLength(36).IsRequired();
            e.Property(b => b.DisplayName).HasMaxLength(256).IsRequired();
            e.Property(b => b.SourceMessageId).HasMaxLength(512).IsRequired();
        });

        m.Entity<ProcessedInboundMessage>(e =>
        {
            e.HasKey(p => p.IdempotencyKey);
            e.Property(p => p.IdempotencyKey).HasMaxLength(64);
            e.Property(p => p.SenderEmail).HasMaxLength(256).IsRequired();
            e.Property(p => p.SourceMessageId).HasMaxLength(512).IsRequired();
        });

        m.Entity<AuditEvent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.CorrelationId).HasMaxLength(50);
            e.Property(a => a.EventType).HasMaxLength(100);
            e.Property(a => a.ActorType).HasMaxLength(50);
        });
    }
}
