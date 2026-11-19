using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HhAutoApplier.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkOutages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaitingSince",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LinkOutages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkOutages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_WaitingSince",
                table: "Notifications",
                column: "WaitingSince",
                filter: "\"WaitingSince\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LinkOutages_EndedAt",
                table: "LinkOutages",
                column: "EndedAt",
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LinkOutages_StartedAt",
                table: "LinkOutages",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkOutages");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_WaitingSince",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "WaitingSince",
                table: "Notifications");
        }
    }
}
