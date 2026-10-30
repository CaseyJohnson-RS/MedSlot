using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;

namespace MedSlot.Domain.Services;

/// <summary>Тип приёма с фиксированной длительностью.</summary>
public sealed class Service : AggregateRoot<ServiceId>
{
    // Для EF Core.
    private Service()
        : this(default, null!, default)
    {
    }

    private Service(ServiceId id, string name, Duration duration)
        : base(id)
    {
        Name = name;
        Duration = duration;
    }

    public string Name { get; private set; }

    public Duration Duration { get; private set; }

    public static Result<Service> Create(ServiceId id, string name, Duration duration, ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(name))
        {
            return ServiceErrors.NameRequired;
        }

        if (duration <= Duration.Zero)
        {
            return ServiceErrors.DurationMustBePositive;
        }

        if (duration.BclCompatibleTicks % policy.SlotGridStep.BclCompatibleTicks != 0)
        {
            return ServiceErrors.DurationNotMultipleOfGrid(duration, policy.SlotGridStep);
        }

        return new Service(id, name.Trim(), duration);
    }
}
