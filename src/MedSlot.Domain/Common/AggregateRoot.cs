namespace MedSlot.Domain.Common;

public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <summary>События, произошедшие с агрегатом и ещё не сохранённые в outbox.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
