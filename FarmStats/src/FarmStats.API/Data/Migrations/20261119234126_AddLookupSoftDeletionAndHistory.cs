using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLookupSoftDeletionAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Species",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionNotes",
                table: "Species",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionReason",
                table: "Species",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "Species",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Breeds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionNotes",
                table: "Breeds",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionReason",
                table: "Breeds",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "Breeds",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Areas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionNotes",
                table: "Areas",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionReason",
                table: "Areas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "Areas",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "LookupHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LookupKind = table.Column<int>(type: "integer", nullable: false),
                    LookupId = table.Column<Guid>(type: "uuid", nullable: false),
                    NameSnapshot = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletionReason = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Species_DeletedAtUtc",
                table: "Species",
                column: "DeletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Breeds_DeletedAtUtc",
                table: "Breeds",
                column: "DeletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Areas_DeletedAtUtc",
                table: "Areas",
                column: "DeletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LookupHistory_LookupKind_LookupId_RecordedAtUtc",
                table: "LookupHistory",
                columns: new[] { "LookupKind", "LookupId", "RecordedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LookupHistory");

            migrationBuilder.DropIndex(
                name: "IX_Species_DeletedAtUtc",
                table: "Species");

            migrationBuilder.DropIndex(
                name: "IX_Breeds_DeletedAtUtc",
                table: "Breeds");

            migrationBuilder.DropIndex(
                name: "IX_Areas_DeletedAtUtc",
                table: "Areas");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Species");

            migrationBuilder.DropColumn(
                name: "DeletionNotes",
                table: "Species");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Species");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Species");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Breeds");

            migrationBuilder.DropColumn(
                name: "DeletionNotes",
                table: "Breeds");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Breeds");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Breeds");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Areas");

            migrationBuilder.DropColumn(
                name: "DeletionNotes",
                table: "Areas");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Areas");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Areas");
        }
    }
}
