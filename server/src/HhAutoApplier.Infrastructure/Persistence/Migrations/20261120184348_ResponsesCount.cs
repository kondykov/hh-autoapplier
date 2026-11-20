using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HhAutoApplier.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResponsesCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ResponsesAtCollect",
                table: "Vacancies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResponsesAtCollectAt",
                table: "Vacancies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsesAtApply",
                table: "JobApplications",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResponsesAtCollect",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "ResponsesAtCollectAt",
                table: "Vacancies");

            migrationBuilder.DropColumn(
                name: "ResponsesAtApply",
                table: "JobApplications");
        }
    }
}
