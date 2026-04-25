using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using Proj1.DTOs;

namespace Proj1.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
                            ?? throw new InvalidOperationException("Connection string not found.");
    }
    
    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var result = new List<AppointmentListDto>();
        await using var connection = new SqlConnection(_connectionString);
        
        const string query = @"
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@Status", SqlDbType.NVarChar).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar).Value = (object?)patientLastName ?? DBNull.Value;

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32("IdAppointment"),
                AppointmentDate = reader.GetDateTime("AppointmentDate"),
                Status = reader.GetString("Status"),
                Reason = reader.GetString("Reason"),
                PatientFullName = reader.GetString("PatientFullName"),
                PatientEmail = reader.GetString("PatientEmail")
            });
        }

        return Ok(result);
    }
    
    
    [HttpGet("{idAppointment}")]
    public async Task<IActionResult> GetAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        
        const string query = @"
            SELECT
                a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                p.FirstName + N' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail, p.PhoneNumber AS PatientPhone,
                d.FirstName + N' ' + d.LastName AS DoctorFullName, d.LicenseNumber AS DoctorLicenseNumber,
                s.Name AS SpecializationName
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
            JOIN dbo.Specializations s ON d.IdSpecialization = s.IdSpecialization
            WHERE a.IdAppointment = @IdAppointment;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return NotFound(new ErrorResponseDto { Message = $"Wizyta o ID {idAppointment} nie została znaleziona." });
        }

        var result = new AppointmentDetailsDto
        {
            IdAppointment = Convert.ToInt32(reader["IdAppointment"]),
            AppointmentDate = Convert.ToDateTime(reader["AppointmentDate"]),
            Status = reader["Status"].ToString()!,
            Reason = reader["Reason"].ToString()!,
            InternalNotes = reader["InternalNotes"] != DBNull.Value ? reader["InternalNotes"].ToString() : null,
            CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
            PatientFullName = reader["PatientFullName"].ToString()!,
            PatientEmail = reader["PatientEmail"].ToString()!,
            PatientPhone = reader["PatientPhone"].ToString()!,
            DoctorFullName = reader["DoctorFullName"].ToString()!,
            DoctorLicenseNumber = reader["DoctorLicenseNumber"].ToString()!,
            SpecializationName = reader["SpecializationName"].ToString()!
        };

        return Ok(result);
    }
    
    
    
    [HttpPost]
    public async Task<IActionResult> AddAppointment(CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate < DateTime.Now)
        {
            return BadRequest(new ErrorResponseDto { Message = "Termin wizyty nie może być w przeszłości." });
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        
        const string checkPatientQuery = "SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient";
        await using var checkPatientCmd = new SqlCommand(checkPatientQuery, connection);
        checkPatientCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        var patientActiveObj = await checkPatientCmd.ExecuteScalarAsync();

        if (patientActiveObj == null)
            return NotFound(new ErrorResponseDto { Message = "Podany pacjent nie istnieje." });
        if (!(bool)patientActiveObj)
            return BadRequest(new ErrorResponseDto { Message = "Podany pacjent jest nieaktywny." });
        
        const string checkDoctorQuery = "SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor";
        await using var checkDoctorCmd = new SqlCommand(checkDoctorQuery, connection);
        checkDoctorCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        var doctorActiveObj = await checkDoctorCmd.ExecuteScalarAsync();

        if (doctorActiveObj == null)
            return NotFound(new ErrorResponseDto { Message = "Podany lekarz nie istnieje." });
        if (!(bool)doctorActiveObj)
            return BadRequest(new ErrorResponseDto { Message = "Podany lekarz jest nieaktywny." });
        
        const string checkConflictQuery = "SELECT COUNT(1) FROM dbo.Appointments WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate";
        await using var checkConflictCmd = new SqlCommand(checkConflictQuery, connection);
        checkConflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        checkConflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        
        var conflictCount = Convert.ToInt32(await checkConflictCmd.ExecuteScalarAsync());
        if (conflictCount > 0)
        {
            return Conflict(new ErrorResponseDto { Message = "Lekarz ma już zaplanowaną wizytę dokładnie w tym terminie." });
        }
        
        const string insertQuery = @"
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, @Status, @Reason);
            SELECT SCOPE_IDENTITY();";

        await using var insertCmd = new SqlCommand(insertQuery, connection);
        insertCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        insertCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        insertCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        insertCmd.Parameters.Add("@Status", SqlDbType.NVarChar).Value = "Scheduled"; // Domyślny status
        insertCmd.Parameters.Add("@Reason", SqlDbType.NVarChar).Value = request.Reason;

        var newId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());
        
        return Created($"/api/appointments/{newId}", new { IdAppointment = newId });
    }
    
    
    [HttpPut("{idAppointment}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, UpdateAppointmentRequestDto request)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        
        const string getApptQuery = "SELECT AppointmentDate, Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment";
        await using var getApptCmd = new SqlCommand(getApptQuery, connection);
        getApptCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        
        await using var reader = await getApptCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." });
        }
        
        var currentStatus = reader.GetString("Status");
        var currentDate = reader.GetDateTime("AppointmentDate");
        await reader.CloseAsync();
        
        if (request.Status != "Scheduled" && request.Status != "Completed" && request.Status != "Cancelled")
        {
            return BadRequest(new ErrorResponseDto { Message = "Nieprawidłowy status wizyty." });
        }
        
        if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
        {
            return BadRequest(new ErrorResponseDto { Message = "Nie można zmienić terminu zakończonej wizyty." });
        }
        
        const string checkPatientQuery = "SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient";
        await using var checkPatientCmd = new SqlCommand(checkPatientQuery, connection);
        checkPatientCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        var patientActiveObj = await checkPatientCmd.ExecuteScalarAsync();

        if (patientActiveObj == null)
            return NotFound(new ErrorResponseDto { Message = "Podany pacjent nie istnieje." });
        if (!(bool)patientActiveObj)
            return BadRequest(new ErrorResponseDto { Message = "Podany pacjent jest nieaktywny." });
        
        const string checkDoctorQuery = "SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor";
        await using var checkDoctorCmd = new SqlCommand(checkDoctorQuery, connection);
        checkDoctorCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        var doctorActiveObj = await checkDoctorCmd.ExecuteScalarAsync();

        if (doctorActiveObj == null)
            return NotFound(new ErrorResponseDto { Message = "Podany lekarz nie istnieje." });
        if (!(bool)doctorActiveObj)
            return BadRequest(new ErrorResponseDto { Message = "Podany lekarz jest nieaktywny." });
        
        const string checkConflictQuery = @"
            SELECT COUNT(1) FROM dbo.Appointments 
            WHERE IdDoctor = @IdDoctor 
              AND AppointmentDate = @AppointmentDate 
              AND IdAppointment != @IdAppointment";
        await using var checkConflictCmd = new SqlCommand(checkConflictQuery, connection);
        checkConflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        checkConflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        checkConflictCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        if (Convert.ToInt32(await checkConflictCmd.ExecuteScalarAsync()) > 0)
        {
            return Conflict(new ErrorResponseDto { Message = "Lekarz ma już zaplanowaną wizytę w tym terminie." });
        }
        
        const string updateQuery = @"
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient,
                IdDoctor = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status = @Status,
                Reason = @Reason,
                InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment";
            
        await using var updateCmd = new SqlCommand(updateQuery, connection);
        updateCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        updateCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        updateCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        updateCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        updateCmd.Parameters.Add("@Status", SqlDbType.NVarChar).Value = request.Status;
        updateCmd.Parameters.Add("@Reason", SqlDbType.NVarChar).Value = request.Reason;
        updateCmd.Parameters.Add("@InternalNotes", SqlDbType.NVarChar).Value = (object?)request.InternalNotes ?? DBNull.Value;

        await updateCmd.ExecuteNonQueryAsync();

        return Ok();
    }
    
    
    [HttpDelete("{idAppointment}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        
        const string checkQuery = "SELECT Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment";
        await using var checkCmd = new SqlCommand(checkQuery, connection);
        checkCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        
        var statusObj = await checkCmd.ExecuteScalarAsync();
        if (statusObj == null)
        {
            return NotFound(new ErrorResponseDto { Message = "Wizyta nie została znaleziona." });
        }

        if (statusObj.ToString() == "Completed")
        {
            return Conflict(new ErrorResponseDto { Message = "Nie można usunąć zakończonej wizyty." });
        }
        
        const string deleteQuery = "DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment";
        await using var deleteCmd = new SqlCommand(deleteQuery, connection);
        deleteCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        
        await deleteCmd.ExecuteNonQueryAsync();

        return NoContent();
    }
}