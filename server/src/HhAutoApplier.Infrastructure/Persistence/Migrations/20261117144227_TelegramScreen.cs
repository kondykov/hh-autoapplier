using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HhAutoApplier.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelegramScreen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestMessageId",
                table: "TelegramChats",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DigestStartedAt",
                table: "TelegramChats",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DigestText",
                table: "TelegramChats",
                type: "character varying(3900)",
                maxLength: 3900,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "DraftBlocks",
                table: "TelegramChats",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "DraftVacancyId",
                table: "TelegramChats",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputBack",
                table: "TelegramChats",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputKind",
                table: "TelegramChats",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputNumber",
                table: "TelegramChats",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InputSince",
                table: "TelegramChats",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputTarget",
                table: "TelegramChats",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenMessageId",
                table: "TelegramChats",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestMessageId",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "DigestStartedAt",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "DigestText",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "DraftBlocks",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "DraftVacancyId",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "InputBack",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "InputKind",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "InputNumber",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "InputSince",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "InputTarget",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "ScreenMessageId",
                table: "TelegramChats");
        }
    }
}
