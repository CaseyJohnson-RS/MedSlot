using MedSlot.Domain.Common;
using MedSlot.Domain.Waitlist;
using static MedSlot.Domain.Tests.TestData;

namespace MedSlot.Domain.Tests.Waitlist;

public class WaitlistEntryTests
{
    private static readonly Instant Now = Msk(Monday, 9);

    private static WaitlistEntry Join(LocalDate? from = null, LocalDate? to = null) =>
        WaitlistEntry.Join(
            WaitlistEntryId.New(), PatientId.New(), DoctorId.New(), ServiceId.New(),
            new DateInterval(from ?? Monday, to ?? Monday.PlusDays(6)), Now, Policy).Value;

    private static WaitlistEntry Offered(out AppointmentId offer)
    {
        var entry = Join();
        offer = AppointmentId.New();
        entry.MarkOffered(offer).IsSuccess.ShouldBeTrue();
        return entry;
    }

    [Fact]
    public void Join_puts_entry_in_queue()
    {
        var entry = Join();

        entry.Status.ShouldBe(WaitlistEntryStatus.Active);
        entry.QueuedAt.ShouldBe(Now);
        entry.CreatedAt.ShouldBe(Now);
        entry.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void Dates_cannot_start_in_the_past_or_beyond_horizon()
    {
        static Result<WaitlistEntry> Join(LocalDate from) => WaitlistEntry.Join(
            WaitlistEntryId.New(), PatientId.New(), DoctorId.New(), ServiceId.New(), new DateInterval(from, from.PlusDays(1)), Now, Policy);

        Join(Monday.PlusDays(-1)).Error.ShouldBe(WaitlistErrors.DatesInPast);
        Join(Monday).IsSuccess.ShouldBeTrue();
        Join(Monday.PlusDays(61)).Error.ShouldBe(WaitlistErrors.DatesBeyondHorizon);
    }

    [Theory]
    [InlineData(1, 10, 30, true)] // вторник, 30 мин помещаются в 30
    [InlineData(1, 10, 15, true)] // короткая услуга помещается в длинный интервал
    [InlineData(1, 10, 45, false)] // услуга длиннее интервала
    [InlineData(7, 10, 30, false)] // дата вне диапазона заявки
    public void Fits_freed_interval(int dayOffset, int hour, int serviceMinutes, bool expected)
    {
        var entry = Join(Monday, Monday.PlusDays(6));
        var freed = TimeRange.FromStart(Msk(Monday.PlusDays(dayOffset), hour), Duration.FromMinutes(30));

        entry.Fits(freed, Duration.FromMinutes(serviceMinutes), Moscow).ShouldBe(expected);
    }

    [Fact]
    public void Offered_entry_does_not_fit_another_interval()
    {
        var entry = Offered(out _);

        entry.Fits(TimeRange.FromStart(Msk(Monday.PlusDays(1), 10), Duration.FromMinutes(30)), Duration.FromMinutes(30), Moscow)
            .ShouldBeFalse();
    }

    [Fact]
    public void Accepted_offer_fulfills_entry()
    {
        var entry = Offered(out var offer);
        entry.CurrentOfferId.ShouldBe(offer);

        entry.Fulfill().IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(WaitlistEntryStatus.Fulfilled);
        entry.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void Declined_offer_moves_entry_to_back_of_queue()
    {
        var entry = Offered(out _);
        var later = Now + Duration.FromMinutes(10);

        entry.OfferDeclined(later).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(WaitlistEntryStatus.Active);
        entry.CurrentOfferId.ShouldBeNull();
        entry.QueuedAt.ShouldBe(later);
        entry.ExpiredOffersCount.ShouldBe(0);
    }

    [Fact]
    public void Missed_offers_expire_entry_after_limit()
    {
        var entry = Join();
        for (var i = 1; i < Policy.MaxExpiredOffersPerEntry; i++)
        {
            entry.MarkOffered(AppointmentId.New());
            entry.OfferExpired(Now + Duration.FromMinutes(i), Policy).IsSuccess.ShouldBeTrue();
            entry.Status.ShouldBe(WaitlistEntryStatus.Active);
            entry.QueuedAt.ShouldBe(Now + Duration.FromMinutes(i));
        }

        entry.MarkOffered(AppointmentId.New());
        entry.OfferExpired(Now + Duration.FromHours(1), Policy).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(WaitlistEntryStatus.Expired);
        entry.ExpiredOffersCount.ShouldBe(Policy.MaxExpiredOffersPerEntry);
        entry.CurrentOfferId.ShouldBeNull();
    }

    [Fact]
    public void Open_entry_can_be_cancelled_once()
    {
        var entry = Offered(out _);

        entry.Cancel().IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(WaitlistEntryStatus.Cancelled);
        entry.Cancel().Error.Code.ShouldBe("waitlist.invalid_status");
    }

    [Fact]
    public void Transitions_require_matching_status()
    {
        var active = Join();

        active.Fulfill().Error.Code.ShouldBe("waitlist.invalid_status");
        active.OfferDeclined(Now).Error.Code.ShouldBe("waitlist.invalid_status");
        active.OfferExpired(Now, Policy).Error.Code.ShouldBe("waitlist.invalid_status");

        var offered = Offered(out _);
        offered.MarkOffered(AppointmentId.New()).Error.Code.ShouldBe("waitlist.invalid_status");
    }
}
