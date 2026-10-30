using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;

namespace MedSlot.Application.Patients;

/// <summary>Карточка пациента: персональные данные.</summary>
public sealed record PatientCardDto(PatientId PatientId, string FullName, string Phone, string? Email);

/// <summary>Кто читает карточку: каждое такое чтение попадает в журнал доступа.</summary>
public abstract record CardViewer
{
    private CardViewer()
    {
    }

    /// <summary>Врач видит только своих пациентов — тех, у кого есть или была запись к нему.</summary>
    public sealed record Doctor(UserId User, DoctorId DoctorId) : CardViewer;

    public sealed record Admin(UserId User) : CardViewer;

    /// <summary>Внутренний сервис, например отправка уведомлений.</summary>
    public sealed record Service(string Name) : CardViewer;
}

/// <summary>
/// Прочитать карточку пациента с записью в журнал доступа (ADR 0012). Команда, а не запрос:
/// чтение и запись в журнал — одна транзакция, и данные отдаются только если журнал сохранён.
/// </summary>
public sealed record ViewPatientCard(PatientId PatientId, CardViewer Viewer, string Purpose) : ICommand<PatientCardDto>;

/// <summary>Пациент смотрит свою карточку — в журнал не пишется.</summary>
public sealed record GetOwnPatientCard(PatientId PatientId) : IQuery<PatientCardDto>;

/// <param name="ActorName">ФИО врача, имя сервиса; для администратора — <c>null</c>.</param>
public sealed record CardAccessDto(Instant AccessedAt, CardAccessorKind AccessorKind, string? ActorName, string Purpose);

/// <summary>Кто и когда смотрел карточку пациента.</summary>
public sealed record GetCardAccessLog(PatientId PatientId) : IQuery<IReadOnlyList<CardAccessDto>>;

internal sealed class ViewPatientCardHandler(
    IPatientRepository patients,
    IAppointmentRepository appointments,
    IPatientCardAccessRepository journal,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<ViewPatientCard, PatientCardDto>
{
    public async Task<Result<PatientCardDto>> Handle(ViewPatientCard command, CancellationToken cancellationToken)
    {
        var patient = await patients.GetByIdAsync(command.PatientId, cancellationToken);

        // Чужой пациент для врача неотличим от несуществующего.
        if (patient is null
            || (command.Viewer is CardViewer.Doctor doctor
                && !await appointments.DoctorHasPatientAsync(doctor.DoctorId, patient.Id, cancellationToken)))
        {
            return ApplicationErrors.NotFound("patient", command.PatientId);
        }

        var now = clock.GetCurrentInstant();
        var access = command.Viewer switch
        {
            CardViewer.Doctor d => PatientCardAccess.ByDoctor(patient.Id, d.User, d.DoctorId, command.Purpose, now),
            CardViewer.Admin a => PatientCardAccess.ByAdmin(patient.Id, a.User, command.Purpose, now),
            CardViewer.Service s => PatientCardAccess.ByService(patient.Id, s.Name, command.Purpose, now),
            _ => throw new InvalidOperationException($"Неизвестный читатель карточки: {command.Viewer}."),
        };
        if (access.IsFailure)
        {
            return access.Error;
        }

        journal.Add(access.Value);
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? PatientCards.ToDto(patient) : saved.Error;
    }
}

internal sealed class GetOwnPatientCardHandler(IPatientRepository patients) : IQueryHandler<GetOwnPatientCard, PatientCardDto>
{
    public async Task<Result<PatientCardDto>> Handle(GetOwnPatientCard query, CancellationToken cancellationToken)
    {
        var patient = await patients.GetByIdAsync(query.PatientId, cancellationToken);
        return patient is null ? ApplicationErrors.NotFound("patient", query.PatientId) : PatientCards.ToDto(patient);
    }
}

internal sealed class GetCardAccessLogHandler(IPatientCardAccessRepository journal, IDoctorRepository doctors)
    : IQueryHandler<GetCardAccessLog, IReadOnlyList<CardAccessDto>>
{
    public async Task<Result<IReadOnlyList<CardAccessDto>>> Handle(GetCardAccessLog query, CancellationToken cancellationToken)
    {
        var entries = await journal.GetForPatientAsync(query.PatientId, cancellationToken);
        var doctorNames = new Dictionary<DoctorId, string>();
        var result = new List<CardAccessDto>();

        foreach (var entry in entries.OrderByDescending(e => e.AccessedAt))
        {
            var actor = entry.AccessorKind switch
            {
                CardAccessorKind.Doctor when entry.DoctorId is { } id => await DoctorNameAsync(id),
                CardAccessorKind.Service => entry.ServiceName,
                _ => null,
            };
            result.Add(new CardAccessDto(entry.AccessedAt, entry.AccessorKind, actor, entry.Purpose));
        }

        return Result.Success<IReadOnlyList<CardAccessDto>>(result);

        async Task<string?> DoctorNameAsync(DoctorId id)
        {
            if (!doctorNames.TryGetValue(id, out var name))
            {
                name = (await doctors.GetByIdAsync(id, cancellationToken))?.FullName ?? string.Empty;
                doctorNames[id] = name;
            }

            return name.Length > 0 ? name : null;
        }
    }
}

internal static class PatientCards
{
    public static PatientCardDto ToDto(Patient patient) =>
        new(patient.Id, patient.FullName, patient.Phone.Value, patient.Email?.Value);
}
