using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using static MedSlot.Domain.Tests.TestData;

namespace MedSlot.Domain.Tests.Appointments;

public class AppointmentTests
{
    private static readonly TimeRange Time = TimeRange.FromStart(Msk(Monday, 10), Duration.FromMinutes(30));
    private static readonly Instant BookedAt = Time.Start - Duration.FromDays(3);

    private static Appointment Booked(BookingSource source = BookingSource.Patient)
    {
        var appointment = Appointment.Book(
            AppointmentId.New(), DoctorId.New(), PatientId.New(), ServiceId.New(), Time, source, BookedAt, Policy).Value;
        appointment.ClearDomainEvents();
        return appointment;
    }

    private static Appointment Held(Instant? now = null)
    {
        var appointment = Appointment.Hold(
            AppointmentId.New(), WaitlistEntryId.New(), DoctorId.New(), PatientId.New(), ServiceId.New(), Time, now ?? BookedAt, Policy).Value;
        appointment.ClearDomainEvents();
        return appointment;
    }

    public class Booking
    {
        [Fact]
        public void Book_creates_booked_appointment_and_raises_event()
        {
            var appointment = Appointment.Book(
                AppointmentId.New(), DoctorId.New(), PatientId.New(), ServiceId.New(), Time, BookingSource.Patient, BookedAt, Policy).Value;

            appointment.Status.ShouldBe(AppointmentStatus.Booked);
            appointment.IsActive.ShouldBeTrue();
            appointment.StatusChangedAt.ShouldBe(BookedAt);
            var booked = appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AppointmentBooked>();
            booked.AppointmentId.ShouldBe(appointment.Id);
            booked.DoctorId.ShouldBe(appointment.DoctorId);
            booked.StartsAt.ShouldBe(Time.Start);
            booked.EndsAt.ShouldBe(Time.End);
            booked.BookedBy.ShouldBe(BookingSource.Patient);
            booked.OccurredAt.ShouldBe(BookedAt);
        }

        [Fact]
        public void Lead_time_boundary_is_inclusive()
        {
            var exactly = Time.Start - Policy.MinBookingLeadTime;

            Book(exactly).IsSuccess.ShouldBeTrue();
            Book(exactly + Duration.FromSeconds(1)).Error.ShouldBe(AppointmentErrors.StartTooSoon);
        }

        [Fact]
        public void Horizon_boundary_is_inclusive()
        {
            var exactly = Time.Start - Policy.BookingHorizon;

            Book(exactly).IsSuccess.ShouldBeTrue();
            Book(exactly - Duration.FromSeconds(1)).Error.ShouldBe(AppointmentErrors.BeyondBookingHorizon);
        }

        [Fact]
        public void Waitlist_booking_goes_through_hold() =>
            Should.Throw<ArgumentException>(() => Appointment.Book(
                AppointmentId.New(), DoctorId.New(), PatientId.New(), ServiceId.New(), Time, BookingSource.Waitlist, BookedAt, Policy));

        private static Result<Appointment> Book(Instant now) =>
            Appointment.Book(AppointmentId.New(), DoctorId.New(), PatientId.New(), ServiceId.New(), Time, BookingSource.Patient, now, Policy);
    }

    public class PatientCancellation
    {
        [Fact]
        public void Cancellation_exactly_at_window_boundary_is_free()
        {
            var appointment = Booked();
            var now = Time.Start - Policy.FreeCancellationWindow;

            appointment.CancelByPatient(now, Policy).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.Cancelled);
            appointment.IsViolation.ShouldBeFalse();
            appointment.IsActive.ShouldBeFalse();
            appointment.CancelledBy.ShouldBe(CancellationInitiator.Patient);
            var cancelled = appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AppointmentCancelled>();
            cancelled.IsLate.ShouldBeFalse();
            cancelled.InitiatedBy.ShouldBe(CancellationInitiator.Patient);
        }

        [Fact]
        public void Cancellation_one_second_after_boundary_is_late()
        {
            var appointment = Booked();
            var now = Time.Start - Policy.FreeCancellationWindow + Duration.FromSeconds(1);

            appointment.CancelByPatient(now, Policy).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.LateCancelled);
            appointment.IsViolation.ShouldBeTrue();
            appointment.StatusChangedAt.ShouldBe(now);
            appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AppointmentCancelled>().IsLate.ShouldBeTrue();
        }

        [Fact]
        public void Cannot_cancel_after_start()
        {
            var appointment = Booked();

            appointment.CancelByPatient(Time.Start, Policy).Error.ShouldBe(AppointmentErrors.AlreadyStarted);
            appointment.Status.ShouldBe(AppointmentStatus.Booked);
            appointment.DomainEvents.ShouldBeEmpty();
        }
    }

    public class ClinicCancellation
    {
        [Fact]
        public void Clinic_cancellation_is_never_a_violation()
        {
            var appointment = Booked();
            var now = Time.Start - Duration.FromMinutes(5);

            appointment.CancelByClinic(now, "doctor_sick").IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.Cancelled);
            appointment.IsViolation.ShouldBeFalse();
            appointment.CancellationReason.ShouldBe("doctor_sick");
            var cancelled = appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AppointmentCancelled>();
            cancelled.InitiatedBy.ShouldBe(CancellationInitiator.Clinic);
            cancelled.IsLate.ShouldBeFalse();
            cancelled.ReasonCode.ShouldBe("doctor_sick");
        }

        [Fact]
        public void Reason_is_required() =>
            Booked().CancelByClinic(BookedAt, " ").Error.ShouldBe(AppointmentErrors.CancellationReasonRequired);

        [Fact]
        public void Cannot_cancel_after_start() =>
            Booked().CancelByClinic(Time.Start, "doctor_sick").Error.ShouldBe(AppointmentErrors.AlreadyStarted);
    }

    public class Visit
    {
        [Fact]
        public void Complete_after_start()
        {
            var appointment = Booked();

            appointment.Complete(Time.Start).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.Completed);
            appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AppointmentCompleted>();
        }

        [Fact]
        public void Cannot_complete_or_mark_no_show_before_start()
        {
            var appointment = Booked();
            var beforeStart = Time.Start - Duration.FromSeconds(1);

            appointment.Complete(beforeStart).Error.ShouldBe(AppointmentErrors.NotStartedYet);
            appointment.MarkNoShow(beforeStart).Error.ShouldBe(AppointmentErrors.NotStartedYet);
        }

        [Fact]
        public void Doctor_marks_no_show()
        {
            var appointment = Booked();

            appointment.MarkNoShow(Time.Start + Duration.FromMinutes(10)).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.NoShow);
            appointment.IsViolation.ShouldBeTrue();
            appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AppointmentNoShow>().Auto.ShouldBeFalse();
        }

        [Fact]
        public void Auto_no_show_after_delay()
        {
            var appointment = Booked();
            var due = Time.End + Policy.NoShowAutoMarkDelay;

            appointment.AutoMarkNoShow(due - Duration.FromSeconds(1), Policy).Error.ShouldBe(AppointmentErrors.AutoNoShowTooEarly);
            appointment.AutoMarkNoShow(due, Policy).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.NoShow);
            appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AppointmentNoShow>().Auto.ShouldBeTrue();
        }

        [Fact]
        public void Auto_no_show_does_not_touch_completed_appointment()
        {
            var appointment = Booked();
            appointment.Complete(Time.Start);

            appointment.AutoMarkNoShow(Time.End + Duration.FromDays(1), Policy).Error.Code.ShouldBe("appointment.invalid_status");
            appointment.Status.ShouldBe(AppointmentStatus.Completed);
        }
    }

    public class TerminalStatuses
    {
        public static TheoryData<string> Terminal => ["Cancelled", "LateCancelled", "Completed", "NoShow", "Released"];

        [Theory]
        [MemberData(nameof(Terminal))]
        public void No_transitions_from_terminal_status(string status)
        {
            var appointment = InStatus(status);
            var afterStart = Time.Start + Duration.FromMinutes(1);
            var beforeStart = Time.Start - Duration.FromDays(2);

            Result[] attempts =
            [
                appointment.CancelByPatient(beforeStart, Policy),
                appointment.CancelByClinic(beforeStart, "reason"),
                appointment.Complete(afterStart),
                appointment.MarkNoShow(afterStart),
                appointment.AutoMarkNoShow(Time.End + Duration.FromDays(1), Policy),
                appointment.AcceptOffer(beforeStart),
                appointment.DeclineOffer(beforeStart),
                appointment.ExpireOffer(Time.Start),
            ];

            attempts.ShouldAllBe(r => r.Error.Code == "appointment.invalid_status");
            appointment.Status.ToString().ShouldBe(status);
        }

        private static Appointment InStatus(string status)
        {
            var appointment = status == "Released" ? Held() : Booked();
            _ = status switch
            {
                "Cancelled" => appointment.CancelByPatient(BookedAt, Policy),
                "LateCancelled" => appointment.CancelByPatient(Time.Start - Duration.FromMinutes(1), Policy),
                "Completed" => appointment.Complete(Time.Start),
                "NoShow" => appointment.MarkNoShow(Time.Start),
                "Released" => appointment.DeclineOffer(BookedAt),
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            };
            appointment.ClearDomainEvents();
            return appointment;
        }
    }

    public class WaitlistHold
    {
        [Fact]
        public void Hold_reserves_slot_and_raises_offer()
        {
            var entryId = WaitlistEntryId.New();

            var appointment = Appointment.Hold(
                AppointmentId.New(), entryId, DoctorId.New(), PatientId.New(), ServiceId.New(), Time, BookedAt, Policy).Value;

            appointment.Status.ShouldBe(AppointmentStatus.Held);
            appointment.IsActive.ShouldBeTrue();
            appointment.BookedBy.ShouldBe(BookingSource.Waitlist);
            appointment.WaitlistEntryId.ShouldBe(entryId);
            appointment.HoldExpiresAt.ShouldBe(BookedAt + Policy.WaitlistOfferTtl);
            var offered = appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<WaitlistSlotOffered>();
            offered.WaitlistEntryId.ShouldBe(entryId);
            offered.ExpiresAt.ShouldBe(appointment.HoldExpiresAt!.Value);
        }

        [Fact]
        public void Hold_needs_time_for_answer_and_lead_time()
        {
            var latest = Time.Start - Policy.WaitlistOfferTtl - Policy.MinBookingLeadTime;

            Hold(latest).IsSuccess.ShouldBeTrue();
            Hold(latest + Duration.FromSeconds(1)).Error.ShouldBe(AppointmentErrors.OfferTooLate);
        }

        [Fact]
        public void Accept_before_expiry_books_appointment()
        {
            var appointment = Held();

            appointment.AcceptOffer(appointment.HoldExpiresAt!.Value - Duration.FromSeconds(1)).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.Booked);
            appointment.HoldExpiresAt.ShouldBeNull();
            appointment.DomainEvents.Count.ShouldBe(2);
            appointment.DomainEvents[0].ShouldBeOfType<WaitlistOfferAccepted>();
            appointment.DomainEvents[1].ShouldBeOfType<AppointmentBooked>().BookedBy.ShouldBe(BookingSource.Waitlist);
        }

        [Fact]
        public void Accept_at_expiry_fails()
        {
            var appointment = Held();

            appointment.AcceptOffer(appointment.HoldExpiresAt!.Value).Error.ShouldBe(AppointmentErrors.OfferExpired);
            appointment.Status.ShouldBe(AppointmentStatus.Held);
        }

        [Fact]
        public void Decline_releases_slot()
        {
            var appointment = Held();

            appointment.DeclineOffer(BookedAt).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.Released);
            appointment.IsActive.ShouldBeFalse();
            appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<WaitlistOfferDeclined>();
        }

        [Fact]
        public void Expire_only_after_deadline()
        {
            var appointment = Held();
            var deadline = appointment.HoldExpiresAt!.Value;

            appointment.ExpireOffer(deadline - Duration.FromSeconds(1)).Error.ShouldBe(AppointmentErrors.OfferNotExpiredYet);
            appointment.ExpireOffer(deadline).IsSuccess.ShouldBeTrue();

            appointment.Status.ShouldBe(AppointmentStatus.Released);
            appointment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<WaitlistOfferExpired>();
        }

        [Fact]
        public void Booked_appointment_cannot_be_treated_as_offer()
        {
            var appointment = Booked();

            appointment.AcceptOffer(BookedAt).Error.Code.ShouldBe("appointment.invalid_status");
            appointment.DeclineOffer(BookedAt).Error.Code.ShouldBe("appointment.invalid_status");
            appointment.ExpireOffer(Time.Start).Error.Code.ShouldBe("appointment.invalid_status");
        }

        [Fact]
        public void Held_appointment_cannot_be_cancelled_or_completed()
        {
            var appointment = Held();

            appointment.CancelByPatient(BookedAt, Policy).Error.Code.ShouldBe("appointment.invalid_status");
            appointment.Complete(Time.Start).Error.Code.ShouldBe("appointment.invalid_status");
        }

        private static Result<Appointment> Hold(Instant now) =>
            Appointment.Hold(AppointmentId.New(), WaitlistEntryId.New(), DoctorId.New(), PatientId.New(), ServiceId.New(), Time, now, Policy);
    }
}
