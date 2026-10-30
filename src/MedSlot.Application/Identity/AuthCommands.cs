using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;

namespace MedSlot.Application.Identity;

/// <summary>Самостоятельная регистрация пациента: создаёт карточку пациента и учётную запись, сразу открывает сессию.</summary>
public sealed record RegisterPatient(string FullName, string Phone, string Email, string Password) : ICommand<AuthTokens>;

public sealed record Login(string Email, string Password) : ICommand<AuthTokens>;

/// <summary>Обновить сессию: refresh-токен погашается, выдаётся новая пара (ADR 0011).</summary>
public sealed record RefreshSession(string RefreshToken) : ICommand<AuthTokens>;

/// <summary>Выход: отзывает все токены этого входа. Неизвестный токен — тоже успех.</summary>
public sealed record Logout(string RefreshToken) : ICommand<Unit>;

/// <summary>Учётная запись врача или администратора; создаёт администратор.</summary>
public sealed record CreateStaffAccount(string Email, string Password, UserRole Role, DoctorId? DoctorId) : ICommand<UserId>;

internal sealed class RegisterPatientHandler(
    IUserRepository users,
    IPatientRepository patients,
    IPasswordHasher hasher,
    Sessions sessions,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<RegisterPatient, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(RegisterPatient command, CancellationToken cancellationToken)
    {
        var email = Email.Create(command.Email);
        if (email.IsFailure)
        {
            return email.Error;
        }

        var phone = PhoneNumber.Create(command.Phone);
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        if (await users.EmailExistsAsync(email.Value, cancellationToken))
        {
            return IdentityErrors.EmailTaken;
        }

        // Контактный email пациента совпадает с логином; позже их можно будет менять раздельно.
        var patient = Patient.Create(PatientId.New(), command.FullName, phone.Value, email.Value);
        if (patient.IsFailure)
        {
            return patient.Error;
        }

        var now = clock.GetCurrentInstant();
        var account = UserAccount.ForPatient(UserId.New(), email.Value, hasher.Hash(command.Password), patient.Value.Id, now);
        patients.Add(patient.Value);
        users.Add(account);

        var tokens = sessions.Start(account, now);
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? tokens : saved.Error;
    }
}

internal sealed class LoginHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    Sessions sessions,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<Login, AuthTokens>
{
    // Хэш для проверки пароля, когда email не найден: время ответа не должно выдавать,
    // зарегистрирован ли email.
    private static string? s_dummyHash;

    public async Task<Result<AuthTokens>> Handle(Login command, CancellationToken cancellationToken)
    {
        var email = Email.Create(command.Email);
        var account = email.IsSuccess ? await users.GetByEmailAsync(email.Value, cancellationToken) : null;
        if (account is null)
        {
            hasher.Verify(s_dummyHash ??= hasher.Hash(Guid.NewGuid().ToString()), command.Password);
            return IdentityErrors.InvalidCredentials;
        }

        var check = hasher.Verify(account.PasswordHash, command.Password);
        if (check == PasswordCheck.Failed)
        {
            return IdentityErrors.InvalidCredentials;
        }

        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            account.ReplacePasswordHash(hasher.Hash(command.Password));
        }

        var tokens = sessions.Start(account, clock.GetCurrentInstant());
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? tokens : saved.Error;
    }
}

internal sealed class RefreshSessionHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokenService,
    Sessions sessions,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<RefreshSession, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(RefreshSession command, CancellationToken cancellationToken)
    {
        var token = await refreshTokens.GetByHashAsync(tokenService.HashRefreshToken(command.RefreshToken), cancellationToken);
        if (token is null)
        {
            return IdentityErrors.InvalidRefreshToken;
        }

        var now = clock.GetCurrentInstant();
        switch (token.Use(now))
        {
            case RefreshTokenUse.Reused:
                // Токен утёк: им уже воспользовались. Завершаем все сессии этого входа —
                // и у злоумышленника, и у владельца, который войдёт заново.
                await refreshTokens.RevokeFamilyAsync(token.FamilyId, now, cancellationToken);
                return IdentityErrors.RefreshTokenReused;

            case RefreshTokenUse.Expired or RefreshTokenUse.Revoked:
                return IdentityErrors.InvalidRefreshToken;
        }

        var account = await users.GetByIdAsync(token.UserId, cancellationToken);
        if (account is null)
        {
            return IdentityErrors.InvalidRefreshToken;
        }

        var tokens = sessions.Rotate(account, token, now);
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);

        // Два одновременных обновления одним токеном: второе проиграет по xmin. Это не утечка,
        // а двойной клик, поэтому семейство не отзываем — просто отказываем.
        return saved.IsSuccess ? tokens
            : saved.Error == ApplicationErrors.ConcurrentModification ? IdentityErrors.InvalidRefreshToken
            : saved.Error;
    }
}

internal sealed class LogoutHandler(IRefreshTokenRepository refreshTokens, ITokenService tokenService, IClock clock)
    : ICommandHandler<Logout, Unit>
{
    public async Task<Result<Unit>> Handle(Logout command, CancellationToken cancellationToken)
    {
        var token = await refreshTokens.GetByHashAsync(tokenService.HashRefreshToken(command.RefreshToken), cancellationToken);
        if (token is not null)
        {
            await refreshTokens.RevokeFamilyAsync(token.FamilyId, clock.GetCurrentInstant(), cancellationToken);
        }

        return Unit.Value;
    }
}

internal sealed class CreateStaffAccountHandler(
    IUserRepository users,
    IDoctorRepository doctors,
    IPasswordHasher hasher,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<CreateStaffAccount, UserId>
{
    public async Task<Result<UserId>> Handle(CreateStaffAccount command, CancellationToken cancellationToken)
    {
        var email = Email.Create(command.Email);
        if (email.IsFailure)
        {
            return email.Error;
        }

        if (await users.EmailExistsAsync(email.Value, cancellationToken))
        {
            return IdentityErrors.EmailTaken;
        }

        if (command.DoctorId is { } doctorId)
        {
            if (await doctors.GetByIdAsync(doctorId, cancellationToken) is null)
            {
                return ApplicationErrors.NotFound("doctor", doctorId);
            }

            if (await users.DoctorHasAccountAsync(doctorId, cancellationToken))
            {
                return new DomainError("auth.doctor_has_account", "У этого врача уже есть учётная запись.");
            }
        }

        var account = UserAccount.ForStaff(
            UserId.New(), email.Value, hasher.Hash(command.Password), command.Role, command.DoctorId, clock.GetCurrentInstant());
        if (account.IsFailure)
        {
            return account.Error;
        }

        users.Add(account.Value);
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? account.Value.Id : saved.Error;
    }
}

/// <summary>Выдача сессий: access-токен и refresh-токен (новое семейство или ротация).</summary>
internal sealed class Sessions(IRefreshTokenRepository refreshTokens, ITokenService tokenService)
{
    public AuthTokens Start(UserAccount account, Instant now) =>
        Issue(account, now, hash => RefreshToken.StartFamily(account.Id, hash, now, tokenService.RefreshTokenLifetime));

    public AuthTokens Rotate(UserAccount account, RefreshToken previous, Instant now) =>
        Issue(account, now, hash => previous.Rotate(hash, now, tokenService.RefreshTokenLifetime));

    private AuthTokens Issue(UserAccount account, Instant now, Func<string, RefreshToken> createRefresh)
    {
        var access = tokenService.CreateAccessToken(account, now);
        var issued = tokenService.CreateRefreshToken();
        var refresh = createRefresh(issued.Hash);
        refreshTokens.Add(refresh);
        return new AuthTokens(access.Token, access.ExpiresAt, issued.Token, refresh.ExpiresAt, account.Role);
    }
}
