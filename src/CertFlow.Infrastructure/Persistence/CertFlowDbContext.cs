using CertFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

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
                .HasForeignKey(v => v.ExamProgramId);
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
            e.HasOne(a => a.Slot).WithMany().HasForeignKey(a => a.SlotId);
            e.HasOne(a => a.Voucher).WithMany().HasForeignKey(a => a.VoucherId);
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
                      .Select(Guid.Parse).ToList());
            e.HasOne(r => r.Appointment).WithMany()
                .HasForeignKey(r => r.AppointmentId);
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
