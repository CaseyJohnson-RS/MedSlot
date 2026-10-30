using MedSlot.Application.Patients;
using MedSlot.Domain.Patients;

namespace MedSlot.Api.Contracts;

/// <summary>Карточка пациента — персональные данные.</summary>
public sealed record PatientCardResponse(Guid PatientId, string FullName, string Phone, string? Email)
{
    internal static PatientCardResponse Create(PatientCardDto dto) => new(dto.PatientId.Value, dto.FullName, dto.Phone, dto.Email);
}

/// <summary>Запись журнала доступа. <c>ActorName</c> — ФИО врача или имя сервиса; для администратора пусто.</summary>
public sealed record CardAccessResponse(DateTimeOffset AccessedAt, CardAccessorKind AccessorKind, string? ActorName, string Purpose)
{
    internal static CardAccessResponse Create(CardAccessDto dto) =>
        new(dto.AccessedAt.ToDateTimeOffset(), dto.AccessorKind, dto.ActorName, dto.Purpose);
}
