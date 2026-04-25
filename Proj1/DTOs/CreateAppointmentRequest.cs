namespace Proj1.DTOs;

public class CreateAppointmentRequest
{
    public int PatientId { get; set; }
    public int DoctorId { get; set; }
    public DateTime Date { get; set; }
    public string Reason { get; set; }
}