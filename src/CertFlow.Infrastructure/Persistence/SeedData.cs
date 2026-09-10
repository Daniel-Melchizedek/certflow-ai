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
        // Real-world certifications rather than invented codes, so the demo reads as a genuine
        // certification catalogue: cloud, developer tooling, an AI vendor track, and a language
        // test. Fundamentals-level exams get the lenient reschedule policy; professional-level
        // and the language test are stricter, which is how the real awarding bodies treat them.
        var az900 = new ExamProgram { Id = Guid.NewGuid(), Code = "AZ-900", Name = "Microsoft Certified: Azure Fundamentals", DurationMinutes = 65 };
        var ai102 = new ExamProgram { Id = Guid.NewGuid(), Code = "AI-102", Name = "Microsoft Certified: Azure AI Engineer Associate", DurationMinutes = 100 };
        var gh600 = new ExamProgram { Id = Guid.NewGuid(), Code = "GH-600", Name = "GitHub Certified: Agentic AI Developer", DurationMinutes = 120 };
        var ccaPro = new ExamProgram { Id = Guid.NewGuid(), Code = "CCA-PRO", Name = "Claude Certified Architect – Professional", DurationMinutes = 120 };
        var ielts = new ExamProgram { Id = Guid.NewGuid(), Code = "IELTS-AC", Name = "IELTS Academic (English Language Proficiency)", DurationMinutes = 165 };
        db.ExamPrograms.AddRange(az900, ai102, gh600, ccaPro, ielts);

        // --- Policies ---
        // One rule across the whole catalogue: reschedule freely, as often as needed, as long
        // as the request lands at least 24 hours before the exam starts.
        db.ReschedulePolicies.AddRange(
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = az900.Id, MinHoursBeforeExam = 24 },
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = ai102.Id, MinHoursBeforeExam = 24 },
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = gh600.Id, MinHoursBeforeExam = 24 },
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = ccaPro.Id, MinHoursBeforeExam = 24 },
            new ReschedulePolicy { Id = Guid.NewGuid(), ExamProgramId = ielts.Id, MinHoursBeforeExam = 24 });

        // --- Test Centers (20, spread across India) ---
        var centers = new[]
        {
            TC("Bengaluru North", "Residency Road", "Bengaluru", "KA"),  TC("Bengaluru South", "Koramangala", "Bengaluru", "KA"),
            TC("Mumbai Central", "Nariman Point", "Mumbai", "MH"),      TC("Mumbai West", "Andheri West", "Mumbai", "MH"),
            TC("Delhi North", "Connaught Place", "New Delhi", "DL"),    TC("Delhi South", "Saket", "New Delhi", "DL"),
            TC("Hyderabad West", "HITEC City", "Hyderabad", "TS"),      TC("Hyderabad Central", "Banjara Hills", "Hyderabad", "TS"),
            TC("Chennai North", "Anna Nagar", "Chennai", "TN"),         TC("Chennai South", "Guindy", "Chennai", "TN"),
            TC("Pune Central", "FC Road", "Pune", "MH"),                TC("Pune East", "Kharadi", "Pune", "MH"),
            TC("Kolkata East", "Salt Lake", "Kolkata", "WB"),           TC("Ahmedabad Central", "SG Highway", "Ahmedabad", "GJ"),
            TC("Jaipur Central", "C-Scheme", "Jaipur", "RJ"),           TC("Noida Central", "Sector 62", "Noida", "UP"),
            TC("Gurgaon North", "Cyber City", "Gurgaon", "HR"),         TC("Lucknow Central", "Hazratganj", "Lucknow", "UP"),
            TC("Bhopal Central", "MP Nagar", "Bhopal", "MP"),           TC("Coimbatore Central", "RS Puram", "Coimbatore", "TN")
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

        // Extra guaranteed Bengaluru slots so that a bulk "move all to Bengaluru" request
        // can always offer 3 distinct options per exam without running dry on de-dup.
        // These are added after the random pool and are never strided into by the appointment
        // assignment loop, so they remain available for reschedule proposals.
        var bengaluruNorth = centers.First(c => c.Name == "Bengaluru North");
        var bengaluruSouth = centers.First(c => c.Name == "Bengaluru South");
        var extraBengaluru = new[]
        {
            28, 31, 35, 39, 42, 46, 49, 52, 56, 59   // days ahead
        }.Select((days, i) => new AppointmentSlot
        {
            Id = Guid.NewGuid(),
            TestCenterId = (i % 2 == 0 ? bengaluruNorth : bengaluruSouth).Id,
            StartUtc = DateTimeOffset.UtcNow.Date.AddDays(days)
                           .AddHours(new[] { 3, 4, 8, 3, 9, 4, 8, 3, 4, 9 }[i]),  // IST -5:30 → UTC
            DurationMinutes = 120,
            IsAvailable = true
        }).ToList();
        db.AppointmentSlots.AddRange(extraBengaluru);

        // --- Candidates ---
        // First entry is the real demo user (dmats); the rest are supporting data.
        var candidates = new[]
        {
            new Candidate { EntraUserId = "93b5bc55-4d10-4117-b513-7b752e92ae8b", DisplayName = "Daniel M A T S", Email = "dmats@81c4nz.onmicrosoft.com", Department = "Engineering" },
            new Candidate { EntraUserId = "seed-user-2", DisplayName = "Arjun Mehta", Email = "arjun@demo.onmicrosoft.com", Department = "Operations" },
            new Candidate { EntraUserId = "seed-user-3", DisplayName = "Kavita Nair", Email = "kavita@demo.onmicrosoft.com", Department = "HR" }
        };
        db.Candidates.AddRange(candidates);

        // --- Vouchers ---
        // The demo user holds three certifications across three different vendors. That also
        // exercises the "which exam did you mean?" path in the intent agent, which only has to
        // ask when a candidate has more than one booking.
        var enrolments = new Dictionary<string, ExamProgram[]>
        {
            [candidates[0].EntraUserId] = [az900, gh600, ccaPro],
            [candidates[1].EntraUserId] = [ai102, ielts],
            [candidates[2].EntraUserId] = [az900, ielts]
        };

        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6));
        var vouchers = enrolments
            .SelectMany(kvp => kvp.Value.Select(p => new ExamVoucher
            {
                Id = Guid.NewGuid(),
                CandidateEntraUserId = kvp.Key,
                ExamProgramId = p.Id,
                ExpiryDate = expiry
            }))
            .ToList();
        db.ExamVouchers.AddRange(vouchers);

        // --- Appointments (one per voucher) ---
        // Booked at least 14 days out so every reschedule policy's minimum-notice window is
        // comfortably clear; a slot booked 3 days ahead would be refused by the 48h rules and
        // the demo would never reach slot proposal.
        var bookable = slots
            .Where(s => s.StartUtc > DateTimeOffset.UtcNow.AddDays(14))
            .OrderBy(s => s.StartUtc)
            .ToList();

        var appointments = new List<Appointment>();
        var seq = 0;
        foreach (var voucher in vouchers)
        {
            // Stride through the pool so bookings land on different dates and centres rather
            // than clustering on consecutive slots.
            var slot = bookable[seq * 3 % bookable.Count];
            if (!slot.IsAvailable) continue;
            slot.IsAvailable = false;

            appointments.Add(new Appointment
            {
                Id = Guid.NewGuid(),
                CandidateEntraUserId = voucher.CandidateEntraUserId,
                SlotId = slot.Id,
                VoucherId = voucher.Id,
                Status = AppointmentStatus.Scheduled,
                OrderNumber = $"ORD-{4820000 + seq}",
                RegistrationId = $"REG-{57310 + seq}",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10)
            });
            seq++;
        }
        db.Appointments.AddRange(appointments);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Enrols a real Entra user as a candidate with a voucher and a booked appointment.
    /// Entra is the identity source, so the agent resolves a sender to an object id via Graph
    /// and then looks appointments up by that id — a candidate row keyed on anything else is
    /// invisible to the email flow. Idempotent: re-running leaves an existing candidate alone.
    /// </summary>
    public static async Task EnrolEntraCandidateAsync(
        CertFlowDbContext db,
        string entraUserId,
        string displayName,
        string email,
        CancellationToken ct = default)
    {
        if (await db.Candidates.AnyAsync(c => c.EntraUserId == entraUserId, ct)) return;

        db.Candidates.Add(new Candidate
        {
            EntraUserId = entraUserId,
            DisplayName = displayName,
            Email = email,
            Department = "Certification"
        });

        // Same three-vendor spread the seeded demo user gets, so a candidate enrolled after the
        // initial seed looks identical to one created by it.
        string[] codes = ["AZ-900", "GH-600", "CCA-PRO"];
        var programs = await db.ExamPrograms.Where(p => codes.Contains(p.Code)).ToListAsync(ct);

        // Booked 14+ days out so every policy's minimum-notice window is clear, otherwise the
        // policy engine rejects the request and the demo never reaches slot proposal.
        var slots = await db.AppointmentSlots
            .Where(s => s.IsAvailable && s.StartUtc > DateTimeOffset.UtcNow.AddDays(14))
            .OrderBy(s => s.StartUtc)
            .Take(programs.Count * 3)
            .ToListAsync(ct);

        var seq = 0;
        foreach (var program in programs)
        {
            var voucher = new ExamVoucher
            {
                Id = Guid.NewGuid(),
                CandidateEntraUserId = entraUserId,
                ExamProgramId = program.Id,
                ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6))
            };
            db.ExamVouchers.Add(voucher);

            var slot = slots.ElementAtOrDefault(seq * 3);
            if (slot is null) break;
            slot.IsAvailable = false;

            db.Appointments.Add(new Appointment
            {
                Id = Guid.NewGuid(),
                CandidateEntraUserId = entraUserId,
                SlotId = slot.Id,
                VoucherId = voucher.Id,
                Status = AppointmentStatus.Scheduled,
                OrderNumber = $"ORD-{Random.Shared.Next(4800000, 4899999)}",
                RegistrationId = $"REG-{Random.Shared.Next(50000, 59999)}",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-5)
            });
            seq++;
        }

        await db.SaveChangesAsync(ct);
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
