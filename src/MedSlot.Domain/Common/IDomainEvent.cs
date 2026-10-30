namespace MedSlot.Domain.Common;

public interface IDomainEvent
{
    Instant OccurredAt { get; }
}
