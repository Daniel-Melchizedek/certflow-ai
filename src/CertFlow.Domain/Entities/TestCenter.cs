namespace CertFlow.Domain.Entities;

public class TestCenter
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string AddressLine1 { get; set; } = default!;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = default!;
    public string State { get; set; } = default!;
    public string Country { get; set; } = default!;
    public string PostalCode { get; set; } = default!;
    public string IanaTimeZone { get; set; } = "Asia/Kolkata";
    public ICollection<AppointmentSlot> Slots { get; set; } = [];
}
