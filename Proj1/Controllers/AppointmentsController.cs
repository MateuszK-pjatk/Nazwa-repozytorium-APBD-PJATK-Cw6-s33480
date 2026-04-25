using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Proj1.DTOs;

namespace Proj1.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AppointmentsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AppointmentsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments(int? patientId)
    {
        var appointments = new List<AppointmentResponse>();
        string connectionString = _configuration.GetConnectionString("DefaultConnection");

        using (var connection = new SqlConnection(connectionString))
        {
            var query = @"
                SELECT a.AppointmentDate, a.Status, 
                       p.FirstName AS PatientFirstName, p.LastName AS PatientLastName, 
                       d.FirstName AS DoctorFirstName, d.LastName AS DoctorLastName, 
                       s.Name AS SpecializationName
                FROM Appointments a
                JOIN Patients p ON a.IdPatient = p.IdPatient
                JOIN Doctors d ON a.IdDoctor = d.IdDoctor
                JOIN Specializations s ON d.IdSpecialization = s.IdSpecialization
                WHERE (@PatientId IS NULL OR a.IdPatient = @PatientId)";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@PatientId", (object)patientId ?? DBNull.Value);
                
                await connection.OpenAsync();
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        appointments.Add(new AppointmentResponse
                        {
                            AppointmentDate = reader.GetDateTime(0),
                            Status = reader.GetString(1),
                            PatientFirstName = reader.GetString(2),
                            PatientLastName = reader.GetString(3),
                            DoctorFirstName = reader.GetString(4),
                            DoctorLastName = reader.GetString(5),
                            SpecializationName = reader.GetString(6)
                        });
                    }
                }
            }
        }

        return Ok(appointments);
    }
}