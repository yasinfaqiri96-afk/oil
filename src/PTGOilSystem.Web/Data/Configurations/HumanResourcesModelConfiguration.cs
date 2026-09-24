using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Data.Configurations;

/// <summary>
/// مدیریت بشری. هر پیوندِ مالی/تاریخی Restrict است: کارمند، بخش یا بستی که سابقه دارد حذف
/// نمی‌شود و فقط غیرفعال می‌شود.
/// </summary>
public static class HumanResourcesModelConfiguration
{
    public static void ConfigureHumanResources(this ModelBuilder modelBuilder)
    {
        ConfigureOrganization(modelBuilder);
        ConfigureContractsAndCompensation(modelBuilder);
        ConfigureAttendanceAndLeave(modelBuilder);
        ConfigurePayrollAndLoans(modelBuilder);
        ConfigureDocumentsAndOffboarding(modelBuilder);
    }

    private static void ConfigureDocumentsAndOffboarding(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<EmployeeDocument>();
        document.Property(x => x.IssueDate).HasColumnType("date");
        document.Property(x => x.ExpiryDate).HasColumnType("date");
        document.HasIndex(x => new { x.EmployeeId, x.IsDeleted });
        document.HasIndex(x => x.ExpiryDate);
        document.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        document.HasOne(x => x.Attachment).WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Restrict);

        var employee = modelBuilder.Entity<Employee>();
        employee.HasIndex(x => x.UserId).IsUnique().HasFilter("\"UserId\" IS NOT NULL");
        employee.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePayrollAndLoans(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<PayrollRun>();
        run.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);
        run.HasIndex(x => new { x.Year, x.Month }).IsUnique();
        run.HasIndex(x => x.Status);

        var line = modelBuilder.Entity<PayrollRunLine>();
        line.Ignore(x => x.EarnedSalary);
        foreach (var money in new[]
                 {
                     nameof(PayrollRunLine.MonthlySalary), nameof(PayrollRunLine.BaseSalary),
                     nameof(PayrollRunLine.OvertimeAmount), nameof(PayrollRunLine.BonusAmount),
                     nameof(PayrollRunLine.AllowanceAmount), nameof(PayrollRunLine.OtherEarning),
                     nameof(PayrollRunLine.AbsenceDeduction), nameof(PayrollRunLine.LateDeduction),
                     nameof(PayrollRunLine.UnpaidLeaveDeduction), nameof(PayrollRunLine.LoanDeduction),
                     nameof(PayrollRunLine.AdvanceDeduction), nameof(PayrollRunLine.OtherDeduction),
                     nameof(PayrollRunLine.GrossSalary), nameof(PayrollRunLine.TotalDeduction),
                     nameof(PayrollRunLine.NetSalary)
                 })
        {
            line.Property<decimal>(money).HasColumnType("numeric(18,4)");
        }

        line.Property(x => x.DailyRate).HasColumnType("numeric(18,6)");
        line.Property(x => x.EmployedDays).HasColumnType("numeric(6,2)");
        line.Property(x => x.AbsentDays).HasColumnType("numeric(6,2)");
        line.Property(x => x.UnpaidLeaveDays).HasColumnType("numeric(6,2)");
        line.HasIndex(x => new { x.PayrollRunId, x.EmployeeId }).IsUnique();
        line.HasIndex(x => x.EmployeeId);
        line.HasOne(x => x.PayrollRun).WithMany(x => x.Lines).HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        var loan = modelBuilder.Entity<EmployeeLoan>();
        loan.Property(x => x.PrincipalAmount).HasColumnType("numeric(18,4)");
        loan.Property(x => x.InstallmentAmount).HasColumnType("numeric(18,4)");
        loan.Property(x => x.LoanDate).HasColumnType("date");
        loan.HasIndex(x => new { x.EmployeeId, x.Status });
        loan.HasIndex(x => x.DisbursementTransactionId).IsUnique();
        loan.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        loan.HasOne(x => x.DisbursementTransaction).WithMany().HasForeignKey(x => x.DisbursementTransactionId).OnDelete(DeleteBehavior.Restrict);

        var salary = modelBuilder.Entity<EmployeeSalaryTransaction>();
        salary.HasIndex(x => x.PayrollRunLineId);
        salary.HasIndex(x => x.EmployeeLoanId);
        salary.HasOne(x => x.PayrollRunLine).WithMany().HasForeignKey(x => x.PayrollRunLineId).OnDelete(DeleteBehavior.Restrict);
        salary.HasOne(x => x.EmployeeLoan).WithMany().HasForeignKey(x => x.EmployeeLoanId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAttendanceAndLeave(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<HrSettings>();
        settings.Property(x => x.WorkingHoursPerDay).HasColumnType("numeric(4,1)");

        var holiday = modelBuilder.Entity<HrHoliday>();
        holiday.Property(x => x.Date).HasColumnType("date");
        holiday.HasIndex(x => x.Date).IsUnique();

        var attendance = modelBuilder.Entity<DailyAttendance>();
        attendance.Property(x => x.Date).HasColumnType("date");
        attendance.Property(x => x.CheckIn).HasColumnType("time");
        attendance.Property(x => x.CheckOut).HasColumnType("time");
        attendance.HasIndex(x => new { x.EmployeeId, x.Date }).IsUnique();
        attendance.HasIndex(x => x.Date);
        attendance.HasIndex(x => x.Status);
        attendance.HasIndex(x => x.LeaveRequestId);
        attendance.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        attendance.HasOne(x => x.LeaveRequest).WithMany().HasForeignKey(x => x.LeaveRequestId).OnDelete(DeleteBehavior.Restrict);

        var leaveType = modelBuilder.Entity<LeaveType>();
        leaveType.Property(x => x.AnnualAllowanceDays).HasColumnType("numeric(6,1)");
        leaveType.HasIndex(x => x.Name).IsUnique();

        var leave = modelBuilder.Entity<LeaveRequest>();
        leave.Property(x => x.FromDate).HasColumnType("date");
        leave.Property(x => x.ToDate).HasColumnType("date");
        leave.Property(x => x.TotalDays).HasColumnType("numeric(6,1)");
        leave.HasIndex(x => new { x.EmployeeId, x.FromDate, x.ToDate });
        leave.HasIndex(x => x.Status);
        leave.HasIndex(x => x.LeaveTypeId);
        leave.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        leave.HasOne(x => x.LeaveType).WithMany().HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        leave.HasOne(x => x.Attachment).WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureContractsAndCompensation(ModelBuilder modelBuilder)
    {
        var attachment = modelBuilder.Entity<HrAttachment>();
        attachment.HasIndex(x => x.EmployeeId);
        attachment.HasIndex(x => x.StoredFileName).IsUnique();
        attachment.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        var contract = modelBuilder.Entity<EmploymentContract>();
        contract.Property(x => x.BaseSalary).HasColumnType("numeric(18,4)");
        contract.Property(x => x.WorkingDaysPerWeek).HasColumnType("numeric(4,1)");
        contract.Property(x => x.WorkingHoursPerDay).HasColumnType("numeric(4,1)");
        contract.Property(x => x.StartDate).HasColumnType("date");
        contract.Property(x => x.EndDate).HasColumnType("date");
        contract.Property(x => x.TerminatedOn).HasColumnType("date");
        contract.HasIndex(x => x.ContractNumber).IsUnique();
        contract.HasIndex(x => new { x.EmployeeId, x.StartDate });
        contract.HasIndex(x => new { x.Status, x.EndDate });
        // یک قراردادِ فعال برای هر کارمند.
        contract.HasIndex(x => x.EmployeeId)
            .HasDatabaseName("UX_EmploymentContracts_OneActivePerEmployee")
            .HasFilter("\"Status\" = 1")
            .IsUnique();
        contract.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        contract.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        contract.HasOne(x => x.Position).WithMany().HasForeignKey(x => x.PositionId).OnDelete(DeleteBehavior.Restrict);
        contract.HasOne(x => x.Attachment).WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Restrict);
        contract.HasOne(x => x.RenewedFromContract).WithMany().HasForeignKey(x => x.RenewedFromContractId).OnDelete(DeleteBehavior.Restrict);

        var compensation = modelBuilder.Entity<EmployeeCompensation>();
        compensation.Property(x => x.BaseSalary).HasColumnType("numeric(18,4)");
        compensation.Property(x => x.EffectiveFrom).HasColumnType("date");
        compensation.Property(x => x.EffectiveTo).HasColumnType("date");
        compensation.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom }).IsUnique();
        compensation.HasIndex(x => x.EmploymentContractId);
        compensation.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        compensation.HasOne(x => x.EmploymentContract).WithMany().HasForeignKey(x => x.EmploymentContractId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrganization(ModelBuilder modelBuilder)
    {
        var department = modelBuilder.Entity<Department>();
        department.HasIndex(x => x.Name).IsUnique();
        department.HasIndex(x => x.IsActive);

        var position = modelBuilder.Entity<Position>();
        position.HasIndex(x => x.Name).IsUnique();
        position.HasIndex(x => x.DepartmentId);
        position.HasIndex(x => x.IsActive);
        position.HasOne(x => x.Department)
            .WithMany(x => x.Positions)
            .HasForeignKey(x => x.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        var employee = modelBuilder.Entity<Employee>();
        employee.HasIndex(x => x.DepartmentId);
        employee.HasIndex(x => x.PositionId);
        employee.HasOne(x => x.DepartmentRef)
            .WithMany()
            .HasForeignKey(x => x.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
        employee.HasOne(x => x.PositionRef)
            .WithMany()
            .HasForeignKey(x => x.PositionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
