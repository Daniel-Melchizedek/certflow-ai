using CertFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CertFlow.Infrastructure.Persistence;

public static class SeedData
{
    public static async Task ApplyAsync(CertFlowDbContext db)
    {
        await db.Database.MigrateAsync();

        if (await db.ExamPrograms.AnyAsync()) return;  // already seeded

        // --- Exam Programs ---
        var cf204 = new ExamProgram { Id = Guid.NewGuid(), Code = "CF-204", Name = "Azure AI Foundry Associate", DurationMinutes = 120 };
        var cf305 = new ExamProgram { Id = Guid.NewGuid(), Code = "CF-305", Name = "Azure AI Engineer Professional", DurationMinutes = 150 };
        var cf101 = new ExamProgram { Id = Guid.NewGuid(), Code = "CF-101", Name = "Azure AI Fundamentals", DurationMinutes = 90 };
        db.ExamPrograms.AddRange(cf204, cf305, cf101);

        // --- Policies ---
        db.ReschedulePolicies.AddRange(
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = cf204.Id, MinHoursBeforeExam = 24, MaxReschedulesPerVoucher = 3 },
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = cf305.Id, MinHoursBeforeExam = 48, MaxReschedulesPerVoucher = 2 },
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = cf101.Id, MinHoursBeforeExam = 24, MaxReschedulesPerVoucher = 3 });

        // --- Test Centers (20, spread across India) ---
        var centers = new[]
        {
            TC("Bangalore Alpha", "Residency Road", "Bengaluru", "KA"), TC("Bangalore Beta", "Koramangala", "Bengaluru", "KA"),
            TC("Mumbai Central", "Nariman Point", "Mumbai", "MH"),     TC("Mumbai West", "Andheri West", "Mumbai", "MH"),
            TC("Delhi North", "Connaught Place", "New Delhi", "DL"),    TC("Delhi South", "Saket", "New Delhi", "DL"),
            TC("Hyderabad One", "HITEC City", "Hyderabad", "TS"),       TC("Hyderabad Two", "Banjara Hills", "Hyderabad", "TS"),
            TC("Chennai Alpha", "Anna Nagar", "Chennai", "TN"),         TC("Chennai Beta", "Guindy", "Chennai", "TN"),
            TC("Pune Central", "FC Road", "Pune", "MH"),                TC("Pune East", "Kharadi", "Pune", "MH"),
            TC("Kolkata One", "Salt Lake", "Kolkata", "WB"),            TC("Ahmedabad Ctr", "SG Highway", "Ahmedabad", "GJ"),
            TC("Jaipur Alpha", "C-Scheme", "Jaipur", "RJ"),             TC("Noida Ctr", "Sector 62", "Noida", "UP"),
            TC("Gurgaon Alpha", "Cyber City", "Gurgaon", "HR"),         TC("Lucknow Ctr", "Hazratganj", "Lucknow", "UP"),
            TC("Bhopal Ctr", "MP Nagar", "Bhopal", "MP"),               TC("Coimbatore Ctr", "RS Puram", "Coimbatore", "TN")
        };
        db.TestCenters.AddRange(centers);

        // --- Appointment Slots (100 slots, next 60 days) ---
        var rng = new Random(42);
        var slotHours = new[] { 9, 10, 11, 13, 14, 15 };
        var slots = Enumerable.Range(0, 100).Select(_ =>
        {
            var center = centers[rng.Next(centers.Length)];
            var daysAhead = rng.Next(3, 60);
            var hour = slotHours[rng.Next(slotHours.Length)];
            return new AppointmentSlot
            {
                Id = Guid.NewGuid(),
                TestCenterId = center.Id,
                StartUtc = DateTimeOffset.UtcNow.Date.AddDays(daysAhead).AddHours(hour - 5).AddMinutes(-30), // IST offset
                DurationMinutes = 120,
                IsAvailable = true
            };
        }).ToList();
        db.AppointmentSlots.AddRange(slots);

        // --- Candidates (placeholder — real identity comes from Entra ID) ---
        // We seed 3 placeholder candidates. In production, candidates are resolved live via Graph.
        var candidates = new[]
        {
            new Candidate { EntraUserId = "seed-user-1", DisplayName = "Priya Sharma", Email = "priya@demo.onmicrosoft.com", Department = "Engineering" },
            new Candidate { EntraUserId = "seed-user-2", DisplayName = "Arjun Mehta", Email = "arjun@demo.onmicrosoft.com", Department = "Operations" },
            new Candidate { EntraUserId = "seed-user-3", DisplayName = "Kavita Nair", Email = "kavita@demo.onmicrosoft.com", Department = "HR" }
        };
        db.Candidates.AddRange(candidates);

        // --- Vouchers ---
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6));
        var vouchers = candidates.SelectMany(c => new[]
        {
            new ExamVoucher { Id = Guid.NewGuid(), CandidateEntraUserId = c.EntraUserId, ExamProgramId = cf204.Id, ExpiryDate = expiry },
            new ExamVoucher { Id = Guid.NewGuid(), CandidateEntraUserId = c.EntraUserId, ExamProgramId = cf101.Id, ExpiryDate = expiry }
        }).ToList();
        db.ExamVouchers.AddRange(vouchers);

        // --- Appointments (one per candidate/voucher on a near-future slot) ---
        var appointments = candidates.Take(3).Select((c, i) =>
        {
            var slot = slots[i * 10];  // pick distinct slots
            slot.IsAvailable = false;   // mark as booked
            var voucher = vouchers.First(v => v.CandidateEntraUserId == c.EntraUserId && v.ExamProgramId == cf204.Id);
            return new Appointment
            {
                Id = Guid.NewGuid(),
                CandidateEntraUserId = c.EntraUserId,
                SlotId = slot.Id,
                VoucherId = voucher.Id,
                Status = AppointmentStatus.Scheduled,
                OrderNumber = $"CF-{2024000 + i}",
                RegistrationId = $"REG-{100 + i}",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10)
            };
        }).ToList();
        db.Appointments.AddRange(appointments);

        await db.SaveChangesAsync();
    }

    private static TestCenter TC(string name, string addr, string city, string state) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        AddressLine1 = addr,
        City = city,
        State = state,
        Country = "India",
        PostalCode = "000000",
        IanaTimeZone = "Asia/Kolkata"
    };
}
