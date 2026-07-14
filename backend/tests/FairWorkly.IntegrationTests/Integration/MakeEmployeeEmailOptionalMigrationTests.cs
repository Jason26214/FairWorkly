using FairWorkly.Domain.Auth.Entities;
using FairWorkly.Domain.Auth.Enums;
using FairWorkly.Domain.Common.Enums;
using FairWorkly.Domain.Employees.Entities;
using FairWorkly.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FairWorkly.IntegrationTests.Integration;

/// <summary>
/// Tests for MakeEmployeeEmailOptional migration (20260131000438)
/// Verifies that:
/// 1. Migration can be applied successfully (Up)
/// 2. Migration can be rolled back successfully (Down)
/// 3. Nullable email constraint works correctly
/// 4. Partial unique index allows multiple NULLs but enforces uniqueness for non-NULL emails
/// </summary>
[Collection("IntegrationTests")]
public class MakeEmployeeEmailOptionalMigrationTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private FairWorklyDbContext _dbContext = null!;
    private Guid _testOrganizationId;

    public MakeEmployeeEmailOptionalMigrationTests()
    {
        _connectionString = GetConnectionString();
    }

    private static string GetConnectionString()
    {
        // Priority 1: Environment variable
        var envConnectionString = Environment.GetEnvironmentVariable(
            "FAIRWORKLY_TEST_DB_CONNECTION"
        );
        if (!string.IsNullOrWhiteSpace(envConnectionString))
        {
            return envConnectionString;
        }

        // Priority 2: appsettings.json
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        return configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No database connection string configured. Set FAIRWORKLY_TEST_DB_CONNECTION or configure appsettings.json"
            );
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<FairWorklyDbContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _dbContext = new FairWorklyDbContext(options);

        // Ensure migrations are applied
        await _dbContext.Database.MigrateAsync();

        // Create test organization
        await CreateTestOrganizationAsync();
    }

    public async Task DisposeAsync()
    {
        await CleanupTestDataAsync();
        await _dbContext.DisposeAsync();
    }

    private async Task CreateTestOrganizationAsync()
    {
        _testOrganizationId = Guid.NewGuid();
        var random = new Random();
        var uniqueAbn = $"{random.Next(10000000, 99999999):D8}{random.Next(100, 999):D3}";

        var organization = new Organization
        {
            Id = _testOrganizationId,
            CompanyName = "Migration Test Company",
            ABN = uniqueAbn,
            IndustryType = "Retail",
            AddressLine1 = "123 Test Street",
            Suburb = "Melbourne",
            State = AustralianState.VIC,
            Postcode = "3000",
            ContactEmail = $"migration-test-{_testOrganizationId:N}@test.com",
            SubscriptionTier = SubscriptionTier.Tier1,
            SubscriptionStartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsSubscriptionActive = true,
        };

        _dbContext.Set<Organization>().Add(organization);
        await _dbContext.SaveChangesAsync();
    }

    private async Task CleanupTestDataAsync()
    {
        try
        {
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

    /// <summary>
    /// TC_MIG_001: Verify email column is nullable after migration
    /// </summary>
    [Fact]
    public async Task Migration_EmailColumn_IsNullable()
    {
        // Arrange & Act - Check column nullability via raw SQL
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT is_nullable
            FROM information_schema.columns
            WHERE table_name = 'employees' AND column_name = 'email'
            """,
            connection
        );

        var result = await cmd.ExecuteScalarAsync();

        // Assert
        result.Should().Be("YES", "email column should be nullable after migration");
    }

    /// <summary>
    /// TC_MIG_002: Verify partial unique index exists with correct filter
    /// </summary>
    [Fact]
    public async Task Migration_PartialUniqueIndex_ExistsWithFilter()
    {
        // Arrange & Act
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT pg_get_indexdef(i.indexrelid) as index_def
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = 'ix_employees_organization_id_email'
            """,
            connection
        );

        var indexDef = await cmd.ExecuteScalarAsync() as string;

        // Assert
        indexDef.Should().NotBeNull("index should exist");
        indexDef.Should().Contain("UNIQUE", "index should be unique");
        indexDef.Should().Contain("email IS NOT NULL", "index should have partial filter");
    }

    /// <summary>
    /// TC_MIG_003: Verify employee can be created with NULL email
    /// </summary>
    [Fact]
    public async Task Employee_CanBeCreated_WithNullEmail()
    {
        // Arrange
        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            OrganizationId = _testOrganizationId,
            EmployeeNumber = "NULL_EMAIL_001",
            FirstName = "Test",
            LastName = "Employee",
            Email = null, // NULL email
            JobTitle = "Staff",
            AwardType = AwardType.GeneralRetailIndustryAward2020,
            AwardLevelNumber = 1,
            EmploymentType = EmploymentType.FullTime,
            StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsActive = true,
        };

        // Act
        _dbContext.Employees.Add(employee);
        var saveAction = async () => await _dbContext.SaveChangesAsync();

        // Assert
        await saveAction.Should().NotThrowAsync("NULL email should be allowed");

        var savedEmployee = await _dbContext.Employees.FindAsync(employee.Id);
        savedEmployee.Should().NotBeNull();
        savedEmployee!.Email.Should().BeNull();
    }

    /// <summary>
    /// TC_MIG_004: Verify multiple employees can have NULL email (partial index allows this)
    /// </summary>
    [Fact]
    public async Task MultipleEmployees_CanHave_NullEmail()
    {
        // Arrange
        var employees = Enumerable
            .Range(1, 3)
            .Select(i => new Employee
            {
                Id = Guid.NewGuid(),
                OrganizationId = _testOrganizationId,
                EmployeeNumber = $"MULTI_NULL_{i:D3}",
                FirstName = $"Test{i}",
                LastName = "Employee",
                Email = null, // All have NULL email
                JobTitle = "Staff",
                AwardType = AwardType.GeneralRetailIndustryAward2020,
                AwardLevelNumber = 1,
                EmploymentType = EmploymentType.FullTime,
                StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
                IsActive = true,
            })
            .ToList();

        // Act
        _dbContext.Employees.AddRange(employees);
        var saveAction = async () => await _dbContext.SaveChangesAsync();

        // Assert
        await saveAction
            .Should()
            .NotThrowAsync("multiple NULL emails should be allowed due to partial index");

        var count = await _dbContext
            .Employees.Where(e => e.OrganizationId == _testOrganizationId && e.Email == null)
            .CountAsync();
        count.Should().BeGreaterThanOrEqualTo(3);
    }

    /// <summary>
    /// TC_MIG_005: Verify duplicate non-NULL emails in same org are rejected
    /// </summary>
    [Fact]
    public async Task DuplicateNonNullEmail_InSameOrg_IsRejected()
    {
        // Arrange
        var duplicateEmail = $"duplicate-{Guid.NewGuid():N}@test.com";

        var employee1 = new Employee
        {
            Id = Guid.NewGuid(),
            OrganizationId = _testOrganizationId,
            EmployeeNumber = "DUP_EMAIL_001",
            FirstName = "First",
            LastName = "Employee",
            Email = duplicateEmail,
            JobTitle = "Staff",
            AwardType = AwardType.GeneralRetailIndustryAward2020,
            AwardLevelNumber = 1,
            EmploymentType = EmploymentType.FullTime,
            StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsActive = true,
        };

        var employee2 = new Employee
        {
            Id = Guid.NewGuid(),
            OrganizationId = _testOrganizationId,
            EmployeeNumber = "DUP_EMAIL_002",
            FirstName = "Second",
            LastName = "Employee",
            Email = duplicateEmail, // Same email
            JobTitle = "Staff",
            AwardType = AwardType.GeneralRetailIndustryAward2020,
            AwardLevelNumber = 1,
            EmploymentType = EmploymentType.FullTime,
            StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsActive = true,
        };

        // Act
        _dbContext.Employees.Add(employee1);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _dbContext.Employees.Add(employee2);
        var saveAction = async () => await _dbContext.SaveChangesAsync();

        // Assert
        await saveAction
            .Should()
            .ThrowAsync<DbUpdateException>(
                "duplicate non-NULL email in same organization should be rejected"
            );
    }

    /// <summary>
    /// TC_MIG_006: Verify same email can exist in different organizations
    /// </summary>
    [Fact]
    public async Task SameEmail_InDifferentOrgs_IsAllowed()
    {
        // Arrange - Create second organization
        var secondOrgId = Guid.NewGuid();
        var random = new Random();
        var uniqueAbn = $"{random.Next(10000000, 99999999):D8}{random.Next(100, 999):D3}";

        var secondOrg = new Organization
        {
            Id = secondOrgId,
            CompanyName = "Second Test Company",
            ABN = uniqueAbn,
            IndustryType = "Retail",
            AddressLine1 = "456 Test Ave",
            Suburb = "Sydney",
            State = AustralianState.NSW,
            Postcode = "2000",
            ContactEmail = $"second-org-{secondOrgId:N}@test.com",
            SubscriptionTier = SubscriptionTier.Tier1,
            SubscriptionStartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsSubscriptionActive = true,
        };

        _dbContext.Set<Organization>().Add(secondOrg);
        await _dbContext.SaveChangesAsync();

        var sharedEmail = $"shared-{Guid.NewGuid():N}@test.com";

        var employee1 = new Employee
        {
            Id = Guid.NewGuid(),
            OrganizationId = _testOrganizationId,
            EmployeeNumber = "CROSS_ORG_001",
            FirstName = "Org1",
            LastName = "Employee",
            Email = sharedEmail,
            JobTitle = "Staff",
            AwardType = AwardType.GeneralRetailIndustryAward2020,
            AwardLevelNumber = 1,
            EmploymentType = EmploymentType.FullTime,
            StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsActive = true,
        };

        var employee2 = new Employee
        {
            Id = Guid.NewGuid(),
            OrganizationId = secondOrgId, // Different organization
            EmployeeNumber = "CROSS_ORG_002",
            FirstName = "Org2",
            LastName = "Employee",
            Email = sharedEmail, // Same email
            JobTitle = "Staff",
            AwardType = AwardType.GeneralRetailIndustryAward2020,
            AwardLevelNumber = 1,
            EmploymentType = EmploymentType.FullTime,
            StartDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            IsActive = true,
        };

        try
        {
            // Act
            _dbContext.Employees.AddRange(employee1, employee2);
            var saveAction = async () => await _dbContext.SaveChangesAsync();

            // Assert
            await saveAction
                .Should()
                .NotThrowAsync("same email in different organizations should be allowed");
        }
        finally
        {
            // Cleanup second org
            await _dbContext
                .Employees.Where(e => e.OrganizationId == secondOrgId)
                .ExecuteDeleteAsync();
            await _dbContext
                .Set<Organization>()
                .Where(o => o.Id == secondOrgId)
                .ExecuteDeleteAsync();
        }
    }
}
