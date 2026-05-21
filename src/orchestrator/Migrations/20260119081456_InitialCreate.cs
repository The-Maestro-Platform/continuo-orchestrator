using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orch");

            migrationBuilder.CreateTable(
                name: "Services",
                schema: "orch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    InternalBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ExternalBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UiApps",
                schema: "orch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ClientKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AllowedOriginsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UiApps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Endpoints",
                schema: "orch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Endpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Endpoints_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "orch",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UiAppEndpoints",
                schema: "orch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UiAppId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UiAppEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UiAppEndpoints_Endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalSchema: "orch",
                        principalTable: "Endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UiAppEndpoints_UiApps_UiAppId",
                        column: x => x.UiAppId,
                        principalSchema: "orch",
                        principalTable: "UiApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Endpoints_ServiceId",
                schema: "orch",
                table: "Endpoints",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_UiAppEndpoints_EndpointId",
                schema: "orch",
                table: "UiAppEndpoints",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_UiAppEndpoints_UiAppId_EndpointId",
                schema: "orch",
                table: "UiAppEndpoints",
                columns: new[] { "UiAppId", "EndpointId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UiAppEndpoints",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "Endpoints",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "UiApps",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "Services",
                schema: "orch");
        }
    }
}
