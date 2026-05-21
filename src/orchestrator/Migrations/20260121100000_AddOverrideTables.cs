using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddOverrideTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UiApps_Override",
                schema: "orch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ClientKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AllowedOriginsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UiApps_Override", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Services_Override",
                schema: "orch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    InternalBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ExternalBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services_Override", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Endpoints_Override",
                schema: "orch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OperationId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequiresAuth = table.Column<bool>(type: "bit", nullable: false),
                    RolesJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PoliciesJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TagsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CacheStrategy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TimeoutMs = table.Column<int>(type: "int", nullable: true),
                    Idempotency = table.Column<bool>(type: "bit", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Endpoints_Override", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UiApps_Override_OriginalId",
                schema: "orch",
                table: "UiApps_Override",
                column: "OriginalId");

            migrationBuilder.CreateIndex(
                name: "IX_Services_Override_OriginalId",
                schema: "orch",
                table: "Services_Override",
                column: "OriginalId");

            migrationBuilder.CreateIndex(
                name: "IX_Endpoints_Override_OriginalId",
                schema: "orch",
                table: "Endpoints_Override",
                column: "OriginalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UiApps_Override",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "Services_Override",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "Endpoints_Override",
                schema: "orch");
        }
    }
}
