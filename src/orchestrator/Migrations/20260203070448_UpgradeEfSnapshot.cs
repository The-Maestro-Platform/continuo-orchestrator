using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class UpgradeEfSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Snapshot-only migration: updates EF Core model snapshot, no schema changes.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No schema changes to revert.
        }
    }
}
