using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackOffice.Database.Migrations;

[DbContext(typeof(BackOfficeDbContext))]
[Migration("20261109105212_AddStaffSignIns")]
public sealed class AddStaffSignIns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "staff_sign_ins",
            table => new
            {
                id = table.Column<string>("text", nullable: false),
                created_at = table.Column<DateTimeOffset>("timestamptz", nullable: false),
                modified_at = table.Column<DateTimeOffset>("timestamptz", nullable: true),
                staff_member_id = table.Column<string>("text", nullable: false),
                email = table.Column<string>("text", nullable: false),
                one_time_password_hash = table.Column<string>("text", nullable: false),
                retry_count = table.Column<int>("integer", nullable: false),
                completed = table.Column<bool>("boolean", nullable: false)
            },
            constraints: table => { table.PrimaryKey("pk_staff_sign_ins", x => x.id); }
        );

        migrationBuilder.CreateIndex("ix_staff_sign_ins_email", "staff_sign_ins", "email");
    }
}
