using FairWorkly.Application.Common.Interfaces;
using FairWorkly.Application.Roster.Features.ValidateRoster;
using FairWorkly.Application.Roster.Interfaces;
using FairWorkly.Application.Roster.Services;
using FairWorkly.Domain.Auth.Entities;
using FairWorkly.Domain.Auth.Enums;
using FairWorkly.Domain.Common.Enums;
using FairWorkly.Domain.Employees.Entities;
using FairWorkly.Domain.Roster.Entities;
using FairWorkly.Domain.Roster.Parameters;
using FairWorkly.Domain.Roster.Rules;
using FairWorkly.Infrastructure.Persistence;
using FairWorkly.Infrastructure.Persistence.Repositories.Roster;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using RosterEntity = FairWorkly.Domain.Roster.Entities.Roster;

namespace FairWorkly.IntegrationTests.Integration;

/// <summary>
/// Integration tests for Roster Validation feature
/// Tests full workflow with real PostgreSQL database
/// </summary>
[Collection("IntegrationTests")]
public class RosterValidationIntegrationTests : IAsyncLifetime
{
    private readonly string _connectionString = GetConnectionString();

    private static string GetConnectionString()
    {
        var envConnectionString = Environment.GetEnvironmentVariable(
            "FAIRWORKLY_TEST_DB_CONNECTION"
        );
        if (!string.IsNullOrWhiteSpace(envConnectionString))
        {
            return envConnectionString;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        return configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No database connection string configured. Set FAIRWORKLY_TEST_DB_CONNECTION or configure appsettings.json"
            );
    }

    private FairWorklyDbContext _dbContext = null!;
    private RosterRepository _rosterRepository = null!;
    private RosterValidationRepository _validationRepository = null!;
    private ValidateRosterHandler _handler = null!;
    private Guid _testOrganizationId;
    private Guid _testEmployeeFullTimeId;
    private Guid _testEmployeePartTimeId;
    private Guid _testRosterId;

    private readonly IDateTimeProvider _dateTimeProvider = new FixedDateTimeProvider(
        new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero)
    );

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<FairWorklyDbContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _dbContext = new FairWorklyDbContext(options);

        // Ensure schema matches current model
        await _dbContext.Database.MigrateAsync();

        // Create repositories
        _rosterRepository = new RosterRepository(_dbContext);
        _validationRepository = new RosterValidationRepository(_dbContext);

        // Create compliance engine with all rules
        var parametersProvider = new AwardRosterRuleParametersProvider();
        var rules = new List<IRosterComplianceRule>
        {
            new DataQualityRule(),
            new MinimumShiftHoursRule(parametersProvider),
            new MealBreakRule(parametersProvider),
            new RestPeriodRule(parametersProvider),
            new WeeklyHoursLimitRule(parametersProvider),
            new ConsecutiveDaysRule(parametersProvider),
        };
        var complianceEngine = new RosterComplianceEngine(rules);

        // Create unit of work
        var unitOfWork = new UnitOfWork(_dbContext);

        // Create handler
        _handler = new ValidateRosterHandler(
            _rosterRepository,
            _validationRepository,
            complianceEngine,
            unitOfWork
        );

        // Create test data
        await CreateTestDataAsync();
    }

    public async Task DisposeAsync()
    {
        await CleanupTestDataAsync();
        await _dbContext.DisposeAsync();
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public FixedDateTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
            Now = utcNow;
        }

        public DateTimeOffset Now { get; }
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class UnitOfWork : IUnitOfWork
    {
        private readonly FairWorklyDbContext _context;

        public UnitOfWork(FairWorklyDbContext context)
        {
            _context = context;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CreateTestDataAsync()
    {
        _testOrganizationId = Guid.NewGuid();
        _testEmployeeFullTimeId = Guid.NewGuid();
        _testEmployeePartTimeId = Guid.NewGuid();
        _testRosterId = Guid.NewGuid();

        // Generate unique ABN
        var random = new Random();
        var uniqueAbn = $"{random.Next(10000000, 99999999):D8}{random.Next(100, 999):D3}";

        // Create organization
        var organization = new Organization
        {
            Id = _testOrganizationId,
            CompanyName = "Roster Test Company",
            ABN = uniqueAbn,
            IndustryType = "Retail",
            AddressLine1 = "456 Roster Street",
            Suburb = "Sydney",
            State = AustralianState.NSW,
            Postcode = "2000",
            ContactEmail = $"roster-test{_testOrganizationId:N}@test.com.au",
            SubscriptionTier = SubscriptionTier.Tier1,
            SubscriptionStartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsSubscriptionActive = true,
        };

        // Create employees
        var employeeFullTime = new Employee
        {
            Id = _testEmployeeFullTimeId,
            OrganizationId = _testOrganizationId,
            EmployeeNumber = "RT001",
            FirstName = "Alice",
            LastName = "FullTimer",
            Email = "alice@test.com",
            JobTitle = "Sales Associate",
            AwardType = AwardType.GeneralRetailIndustryAward2020,
            AwardLevelNumber = 1,
            EmploymentType = EmploymentType.FullTime,
            StartDate = DateTime.SpecifyKind(DateTime.UtcNow.AddYears(-1), DateTimeKind.Utc),
            IsActive = true,
        };

        var employeePartTime = new Employee
        {
            Id = _testEmployeePartTimeId,
            OrganizationId = _testOrganizationId,
            EmployeeNumber = "RT002",
            FirstName = "Bob",
            LastName = "PartTimer",
            Email = "bob@test.com",
            JobTitle = "Cashier",
            AwardType = AwardType.GeneralRetailIndustryAward2020,
            AwardLevelNumber = 1,
            EmploymentType = EmploymentType.PartTime,
            StartDate = DateTime.SpecifyKind(DateTime.UtcNow.AddMonths(-6), DateTimeKind.Utc),
            IsActive = true,
        };

        // Create roster (week of Feb 2, 2026 - Monday)
        var weekStart = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var roster = new RosterEntity
        {
            Id = _testRosterId,
            OrganizationId = _testOrganizationId,
            WeekStartDate = weekStart,
            WeekEndDate = weekStart.AddDays(6),
            WeekNumber = 6, // Week 6 of 2026
            Year = 2026,
            IsFinalized = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Set<Organization>().Add(organization);
        _dbContext.Employees.AddRange(employeeFullTime, employeePartTime);
        _dbContext.Rosters.Add(roster);
        await _dbContext.SaveChangesAsync();
    }

    private async Task CleanupTestDataAsync()
    {
        try
        {
            // Clean up in reverse order of dependencies
            await _dbContext
                .RosterIssues.Where(i => i.OrganizationId == _testOrganizationId)
                .ExecuteDeleteAsync();

            await _dbContext
                .RosterValidations.Where(v => v.OrganizationId == _testOrganizationId)
                .ExecuteDeleteAsync();

            await _dbContext
                .Shifts.Where(s => s.OrganizationId == _testOrganizationId)
                .ExecuteDeleteAsync();

            await _dbContext
                .Rosters.Where(r => r.OrganizationId == _testOrganizationId)
                .ExecuteDeleteAsync();

            await _dbContext
                .Employees.Where(e => e.OrganizationId == _testOrganizationId)
                .ExecuteDeleteAsync();

            await _dbContext
                .Set<Organization>()
                .Where(o => o.Id == _testOrganizationId)
                .ExecuteDeleteAsync();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private async Task AddShiftsAsync(params Shift[] shifts)
    {
        _dbContext.Shifts.AddRange(shifts);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private Shift CreateCompliantShift(Guid employeeId, DateTime date)
    {
        // 8-hour shift with 30-minute meal break (compliant)
        return new Shift
        {
            Id = Guid.NewGuid(),
            OrganizationId = _testOrganizationId,
            RosterId = _testRosterId,
            EmployeeId = employeeId,
            Date = date,
            StartTime = new TimeSpan(9, 0, 0), // 09:00
            EndTime = new TimeSpan(17, 0, 0), // 17:00
            HasMealBreak = true,
            MealBreakDuration = 30,
            HasRestBreaks = false,
        };
    }

    private Shift CreateShortShift(Guid employeeId, DateTime date)
    {
        // 2-hour shift (violates 3-hour minimum for part-time/casual)
        return new Shift
        {
            Id = Guid.NewGuid(),
            OrganizationId = _testOrganizationId,
            RosterId = _testRosterId,
            EmployeeId = employeeId,
            Date = date,
            StartTime = new TimeSpan(10, 0, 0), // 10:00
            EndTime = new TimeSpan(12, 0, 0), // 12:00
            HasMealBreak = false,
            HasRestBreaks = false,
        };
    }

    private Shift CreateNoBreakShift(Guid employeeId, DateTime date)
    {
        // 6-hour shift without meal break (violates meal break requirement for shifts > 5 hours)
        return new Shift
        {
            Id = Guid.NewGuid(),
            OrganizationId = _testOrganizationId,
            RosterId = _testRosterId,
            EmployeeId = employeeId,
            Date = date,
            StartTime = new TimeSpan(9, 0, 0), // 09:00
            EndTime = new TimeSpan(15, 0, 0), // 15:00
            HasMealBreak = false,
            HasRestBreaks = false,
        };
    }

    [Fact]
    public async Task TC_VALIDATE_001_ValidRoster_ReturnsPassed()
    {
        // Arrange - add compliant shifts
        var weekStart = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await AddShiftsAsync(
            CreateCompliantShift(_testEmployeeFullTimeId, weekStart),
            CreateCompliantShift(_testEmployeeFullTimeId, weekStart.AddDays(1)),
            CreateCompliantShift(_testEmployeePartTimeId, weekStart.AddDays(2))
        );

        var command = new ValidateRosterCommand
        {
            RosterId = _testRosterId,
            OrganizationId = _testOrganizationId,
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be(ValidationStatus.Passed);
        result.Value.CriticalIssues.Should().Be(0);
        result.Value.TotalShifts.Should().Be(3);
        result.Value.PassedShifts.Should().Be(3);
        result.Value.FailedShifts.Should().Be(0);
    }

    [Fact]
    public async Task TC_VALIDATE_002_RosterWithViolations_ReturnsFailed()
    {
        // Arrange - add shifts with violations
        var weekStart = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await AddShiftsAsync(
            CreateCompliantShift(_testEmployeeFullTimeId, weekStart),
            CreateShortShift(_testEmployeePartTimeId, weekStart.AddDays(1)), // Violation: < 3 hours
            CreateNoBreakShift(_testEmployeeFullTimeId, weekStart.AddDays(2)) // Violation: no meal break
        );

        var command = new ValidateRosterCommand
        {
            RosterId = _testRosterId,
            OrganizationId = _testOrganizationId,
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be(ValidationStatus.Failed);
        result.Value.CriticalIssues.Should().BeGreaterThan(0);
        result.Value.TotalIssues.Should().BeGreaterThanOrEqualTo(result.Value.CriticalIssues);
        result.Value.Issues.Should().NotBeEmpty();

        // Verify specific violations
        result.Value.Issues.Should().Contain(i => i.CheckType == "MinimumShiftHours");
        result.Value.Issues.Should().Contain(i => i.CheckType == "MealBreak");
    }

    [Fact]
    public async Task TC_VALIDATE_003_NonExistentRoster_ReturnsNotFound()
    {
        // Arrange
        var command = new ValidateRosterCommand
        {
            RosterId = Guid.NewGuid(), // Non-existent roster
            OrganizationId = _testOrganizationId,
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Code.Should().Be(404);
    }

    [Fact]
    public async Task TC_VALIDATE_004_ValidationPersistedToDatabase()
    {
        // Arrange
        var weekStart = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await AddShiftsAsync(
            CreateCompliantShift(_testEmployeeFullTimeId, weekStart),
            CreateShortShift(_testEmployeePartTimeId, weekStart.AddDays(1))
        );

        var command = new ValidateRosterCommand
        {
            RosterId = _testRosterId,
            OrganizationId = _testOrganizationId,
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert - verify result
        result.IsSuccess.Should().BeTrue();
        var validationId = result.Value!.ValidationId;

        // Assert - verify database persistence
        _dbContext.ChangeTracker.Clear();

        var dbValidation = await _dbContext.RosterValidations.FirstOrDefaultAsync(v =>
            v.Id == validationId
        );
        dbValidation.Should().NotBeNull();
        dbValidation!.RosterId.Should().Be(_testRosterId);
        dbValidation.OrganizationId.Should().Be(_testOrganizationId);
        dbValidation.Status.Should().Be(ValidationStatus.Failed);
        dbValidation.CompletedAt.Should().NotBeNull();

        // Use Count to avoid AffectedDateSet value conversion issue with NULL columns
        var issueCount = await _dbContext
            .RosterIssues.Where(i => i.RosterValidationId == validationId)
            .CountAsync();
        issueCount.Should().BeGreaterThan(0);

        // Verify issues belong to correct organization using projection
        var issueOrgIds = await _dbContext
            .RosterIssues.Where(i => i.RosterValidationId == validationId)
            .Select(i => i.OrganizationId)
            .ToListAsync();
        issueOrgIds.Should().AllSatisfy(orgId => orgId.Should().Be(_testOrganizationId));
    }
}
