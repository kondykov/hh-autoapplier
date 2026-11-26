using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HhAutoApplier.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegionalVacancyDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DuplicateOfVacancyId",
                table: "JobApplications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegionalGroupKey",
                table: "JobApplications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobApplications_UserId_DuplicateOfVacancyId",
                table: "JobApplications",
                columns: new[] { "UserId", "DuplicateOfVacancyId" },
                filter: "\"DuplicateOfVacancyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JobApplications_UserId_RegionalGroupKey",
                table: "JobApplications",
                columns: new[] { "UserId", "RegionalGroupKey" },
                unique: true,
                filter: "\"RegionalGroupKey\" IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            // Из ранее найденных оставляем первый отклик. Только задания в работе могут уйти из очереди.
            // История отправленного отклика, приглашения и решения пользователя сохраняются.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT a."Id", a."VacancyId", v."Area", first_value(a."VacancyId") OVER regional AS original,
                        first_value(v."Area") OVER regional AS original_region
                    FROM "JobApplications" a JOIN "Vacancies" v ON v."Id" = a."VacancyId"
                    WHERE v."WorkFormat" = 'Удалённо' AND NOT v."Imported" AND a."Status" <> 'New'
                      AND btrim(v."Company") <> '' AND btrim(v."Title") <> '' AND btrim(v."Description") <> ''
                      AND btrim(v."Area") <> '' AND char_length(v."Description") < 20000
                    WINDOW regional AS (
                        PARTITION BY a."UserId", v."Company", v."Title", v."Description",
                                     v."SalaryFrom", v."SalaryTo", v."SalaryCurrency"
                        ORDER BY a."CreatedAt", a."Id")
                )
                UPDATE "JobApplications" a
                SET "DuplicateOfVacancyId" = ranked.original, "Status" = 'Skipped',
                    "StatusReason" = 'региональный дубль (обновление)',
                    "History" = COALESCE(a."History", '[]'::jsonb) || jsonb_build_array(jsonb_build_object(
                        'At', now(), 'From', a."Status", 'To', 'Skipped', 'Reason', 'региональный дубль (обновление)'))
                FROM ranked
                WHERE a."Id" = ranked."Id" AND ranked."VacancyId" <> ranked.original AND ranked."Area" <> ranked.original_region
                  AND a."AppliedAt" IS NULL AND a."Status" IN ('Pending', 'Review', 'Queued', 'Questionnaire')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "JobApplications"
                SET "Status" = "History" -> -1 ->> 'From',
                    "StatusReason" = "History" -> -2 ->> 'Reason',
                    "History" = "History" - (-1)
                WHERE "DuplicateOfVacancyId" IS NOT NULL AND "Status" = 'Skipped'
                  AND "History" -> -1 ->> 'Reason' = 'региональный дубль (обновление)'
                  AND "History" -> -1 ->> 'From' IN ('Pending', 'Review', 'Queued', 'Questionnaire')
                """);

            migrationBuilder.DropIndex(
                name: "IX_JobApplications_UserId_DuplicateOfVacancyId",
                table: "JobApplications");

            migrationBuilder.DropIndex(
                name: "IX_JobApplications_UserId_RegionalGroupKey",
                table: "JobApplications");

            migrationBuilder.DropColumn(
                name: "DuplicateOfVacancyId",
                table: "JobApplications");

            migrationBuilder.DropColumn(
                name: "RegionalGroupKey",
                table: "JobApplications");

        }
    }
}
